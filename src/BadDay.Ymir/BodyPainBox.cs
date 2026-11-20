using System;
using System.Collections.Generic;
using BadDay.Core;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>Where the right hand was after a step, and whether it was inside the box.</summary>
public readonly record struct HandSample(float X, float Y, float Z, bool InBox);

/// <summary>
/// The body run's result: the same timeline and outcome as the headless run, plus what only a body has.
/// The outcome comes from the Actor's events (the run goes on to the deliberate withdrawal, then settles up to a second
/// for the hand to leave the box for good). <see cref="PainBoxResult.HandOutMs"/> is the start of the first exit after the break
/// that the hand held for <see cref="OutHoldSeconds"/> without re-entering (a flinch excursion is not an exit); null if there was none.
/// <see cref="Run"/>'s EndMs is when the run stopped.
/// </summary>
/// <param name="Excursions">Times the hand left the box between the end of the ramp and the break.</param>
/// <param name="Hand">The hand after every step; the index is the tick.</param>
public sealed record BodyPainBoxResult(
    PainBoxResult Run, int Excursions, IReadOnlyList<HandSample> Hand);

/// <summary>
/// The pain box on the body: <see cref="BodyPainBoxSession"/> is the one owner of the Ymir world and its loop, and
/// <see cref="Run"/> steps one to the end. BDS reaches the puppet only through <see cref="HumanoidRagdoll.Build"/>,
/// <see cref="HumanoidRagdoll.ReadRegions"/> and <see cref="HumanoidRagdoll.Drive"/>.
/// </summary>
public static class BodyPainBox
{
    /// <summary>The ragdoll's gains are tuned at 120 Hz.</summary>
    public const double TickSeconds = 1.0 / 120.0;

    /// <summary>Centre of the box and the right hand's hold point.</summary>
    public static readonly (float X, float Y, float Z) HoldPoint = (-0.21f, 1.19f, 0.46f);

    /// <summary>The hand is out when it has stayed outside the box this long after the break.</summary>
    public const double OutHoldSeconds = 0.2;

    /// <summary>Steps a <see cref="BodyPainBoxSession"/> to its end and records the hand after every tick (a box disabled at tick 0 leaves the rest pose as the one sample).</summary>
    public static BodyPainBoxResult Run(PainBoxParameters p, ulong seed)
    {
        using var s = new BodyPainBoxSession(p, seed);
        var samples = new List<HandSample>();
        while (!s.Finished)
        {
            long before = s.TickIndex;
            s.Step();
            if (s.TickIndex != before || samples.Count == 0) samples.Add(s.Hand);
        }
        return new BodyPainBoxResult(s.Result(), s.Excursions, samples);
    }
}

/// <summary>What the Actor reported on one tick of a <see cref="BodyPainBoxSession"/>; only ticks that reported something are queued.</summary>
public readonly record struct BodyEvent(long Tick, TickEvents Events);

/// <summary>
/// One body run, stepped by its caller: BDS creates the Ymir world (a ground, a static stool box welded to the pelvis
/// because the PD ragdoll cannot balance for 15 s, the ragdoll, the box volume around the right hand's hold
/// point) and is its only stepper; the nervous session inside it is never exposed, so nothing else can tick it.
/// Per <see cref="Step"/>: the box writes its intensity, the body reports its hand points, Lodur's Actor decides,
/// the ragdoll is driven, the world steps. The run ends on the Actor's events, never on the hand: after the
/// deliberate withdrawal, once the hand is out or the settle is over. After <see cref="Finished"/>
/// <see cref="Step"/> only keeps the body moving: the last strengths and targets are driven again and the world steps,
/// while the result, <see cref="HandOutMs"/> and <see cref="Excursions"/> stay as they were. That free motion is
/// float arithmetic on the platform's math and is not bit-identical across operating systems; everything up to
/// <see cref="Finished"/> is.
/// <see cref="SetIntensity"/> and <see cref="SwitchOff"/> take effect on the next tick. Events queue per tick and a
/// client drains them with <see cref="DrainEvents"/>, so a frame that spans several steps loses none. After
/// <see cref="Dispose"/> every member throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class BodyPainBoxSession : IDisposable
{
    private const float BoxHalf = 0.12f;

    /// <summary>Physics substeps per 1/120 s step. The ragdoll gains were tuned against 4; the behaviour tests, not this number, are the rule.</summary>
    private const int Substeps = 4;

    /// <summary>After the deliberate withdrawal begins the run waits this long for the hand to come out.</summary>
    private const double SettleSeconds = 1.0;

    private readonly PainBoxParameters _p;
    private readonly Box3DSoaWorld _world;
    private readonly HumanoidRagdoll _ragdoll;
    private readonly PainBoxSession _session;
    private readonly Queue<BodyEvent> _events = new();
    private readonly float[] _x = new float[HumanoidRagdoll.RegionCount];
    private readonly float[] _y = new float[HumanoidRagdoll.RegionCount];
    private readonly float[] _z = new float[HumanoidRagdoll.RegionCount];
    private readonly float[] _strengths = new float[HumanoidRagdoll.MuscleCount];
    private readonly Quat[] _targets = new Quat[HumanoidRagdoll.MuscleCount];
    private readonly long _rampEnd;
    private readonly long _settleTicks;
    private readonly long _outHoldTicks;
    private HandSample _hand;
    private int _excursions;
    private long? _handOutMs;
    private long _outSince = -1; // first tick of the current continuous stay outside the box after the break
    private bool _wasIn;
    private long _breakTick = -1, _withdrewTick = -1;
    private bool _disposed;

    public BodyPainBoxSession(PainBoxParameters p, ulong seed)
    {
        if (Math.Abs(p.TickSeconds - BodyPainBox.TickSeconds) > 1e-12)
        {
            throw new ArgumentException($"The body run steps at 1/120 s; got TickSeconds {p.TickSeconds}.");
        }

        _p = p;
        _world = new Box3DSoaWorld(capacity: HumanoidRagdoll.LimbCount + 2, jointCapacity: HumanoidRagdoll.MuscleCount + 1);
        try
        {
            _world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
            var stool = _world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
            _ragdoll = HumanoidRagdoll.Build(_world, (0f, 0f), firstStableId: 100);
            _world.AddJoint(Box3DSoaJointKind.Weld, stool, _ragdoll.BodyLane(Limb.Pelvis), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

            // Hold: the right arm raised and bent so the hand sits in the box. Withdraw: the arm pulled back and folded.
            var hold = new Quat[HumanoidRagdoll.MuscleCount];
            var withdraw = new Quat[HumanoidRagdoll.MuscleCount];
            Array.Fill(hold, Quat.Identity);
            Array.Fill(withdraw, Quat.Identity);
            hold[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, -1.0f);
            hold[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -0.6f);
            withdraw[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
            withdraw[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -2.0f);

            var hp = BodyPainBox.HoldPoint;
            var volume = new BoxStimulus(hp.X, hp.Y, hp.Z, BoxHalf, BoxHalf, BoxHalf);
            _session = new PainBoxSession(p, seed, volume, HumanoidRagdoll.RegionCount, hold, withdraw, (int)Region.HandR);
        }
        catch
        {
            _world.Dispose();
            throw;
        }

        _rampEnd = (long)Math.Round(p.BoxRampSeconds / p.TickSeconds);
        _settleTicks = (long)Math.Round(SettleSeconds / p.TickSeconds);
        _outHoldTicks = Math.Max(1, (long)Math.Round(BodyPainBox.OutHoldSeconds / p.TickSeconds));
        _hand = ReadHand();
    }

    /// <summary>The live lanes of the world, for rendering. Read-only: nothing outside this session writes them.</summary>
    public Box3DSoaView View { get { Live(); return _world.View; } }

    /// <summary>The right hand after the latest step (the rest pose before the first).</summary>
    public HandSample Hand { get { Live(); return _hand; } }

    /// <summary>Times the hand left the box between the end of the ramp and the break.</summary>
    public int Excursions { get { Live(); return _excursions; } }

    /// <summary>The start of the first sustained exit after the break; null if there was none (see <see cref="BodyPainBoxResult"/>).</summary>
    public long? HandOutMs { get { Live(); return _handOutMs; } }

    public bool Finished { get { Live(); return _session.Finished; } }

    /// <summary>Ticks run so far; the tick the next <see cref="Step"/> runs.</summary>
    public long TickIndex { get { Live(); return _session.TickIndex; } }

    /// <summary>The box's intensity as written on the latest tick.</summary>
    public double BoxIntensity { get { Live(); return _session.Box.Intensity; } }

    /// <summary>The stimulus volume the hand is held in (a copy), for drawing the chamber.</summary>
    public BoxStimulus Chamber { get { Live(); return _session.Box; } }

    /// <summary>The Actor's state at the end of the latest tick.</summary>
    public double Pain { get { Live(); return _session.Actor.Drive.Pain; } }
    public double Strain { get { Live(); return _session.Actor.Drive.Strain; } }
    public double Resolve { get { Live(); return _session.Actor.Drive.Resolve; } }
    public Lodur.Goal Goal { get { Live(); return _session.Actor.Goal; } }

    /// <summary>One step of 1/120 s; see the type's summary for what it does after <see cref="Finished"/>.</summary>
    public void Step()
    {
        Live();
        bool wasFinished = _session.Finished;
        bool ticked = !wasFinished && !_session.Disabled(); // a step that finds the box switched off ends the run without stepping the world
        long tick = _session.TickIndex;
        var events = TickEvents.None;
        if (ticked)
        {
            _ragdoll.ReadRegions(_x, _y, _z);
            events = _session.Tick(_x, _y, _z, _strengths, _targets);
            if (events != TickEvents.None) _events.Enqueue(new BodyEvent(tick, events));
        }
        if (ticked || wasFinished)
        {
            _ragdoll.Drive(_strengths, _targets); // after Finished: the last strengths and targets again
            _world.Step((float)_p.TickSeconds, Substeps);
        }

        var sample = ReadHand(); // the rest pose when the box was off at tick 0
        _hand = sample;
        if (!ticked) return;

        if ((events & TickEvents.WillBroke) != 0) _breakTick = tick;
        bool inBox = sample.InBox;

        if (_breakTick < 0)
        {
            if (tick > _rampEnd && _wasIn && !inBox) _excursions++;
            _wasIn = inBox;
        }
        else if (inBox)
        {
            _outSince = -1; // back in: whatever exit this was, it was a flinch
            _handOutMs = null;
        }
        else
        {
            if (_outSince < 0) _outSince = tick;
            if (tick - _outSince + 1 >= _outHoldTicks) _handOutMs = _session.Ms(_outSince);
        }

        // The run ends on the Actor's events, never on the hand.
        if (_session.Withdrew)
        {
            if (_withdrewTick < 0) _withdrewTick = tick;
            if (_handOutMs is not null || tick - _withdrewTick >= _settleTicks) _session.EndAt(tick);
        }
    }

    /// <summary>
    /// Moves the queued events (oldest first) into <paramref name="buffer"/> and returns how many it wrote; the rest stay
    /// queued for the next call. Allocation-free once the queue has grown to the client's drain interval.
    /// </summary>
    public int DrainEvents(Span<BodyEvent> buffer)
    {
        Live();
        int n = 0;
        while (n < buffer.Length && _events.Count > 0) buffer[n++] = _events.Dequeue();
        return n;
    }

    /// <summary>Sets the box intensity from the next tick; refuses a level outside 0 to <see cref="PainBox.MaxLevel"/>.</summary>
    public void SetIntensity(double level)
    {
        Live();
        _session.SetIntensity(level);
    }

    /// <summary>Switches the box off from the next tick; the run ends as BoxDisabled if the will still holds.</summary>
    public void SwitchOff()
    {
        Live();
        _session.SwitchOff();
    }

    /// <summary>The nervous result so far, with <see cref="HandOutMs"/>.</summary>
    public PainBoxResult Result()
    {
        Live();
        return _session.Result() with { HandOutMs = _handOutMs };
    }

    /// <summary>Frees the world; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.Dispose();
    }

    private void Live() => ObjectDisposedException.ThrowIf(_disposed, this);

    private HandSample ReadHand()
    {
        _ragdoll.ReadRegions(_x, _y, _z);
        float hx = _x[(int)Region.HandR], hy = _y[(int)Region.HandR], hz = _z[(int)Region.HandR];
        return new HandSample(hx, hy, hz, _session.Box.Contains(hx, hy, hz));
    }
}
