using System;
using BadDay.Core;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>
/// The office on the seated body, stepped by its caller like <see cref="BodyPainBoxSession"/>: a <see cref="SeatedBody"/>
/// (the one owner of the world), a Lodur <see cref="Physiology"/> on the humanoid plan with the session's slow scale,
/// and the <see cref="StressSession"/> the headless run drives, with the desk's emitters moved to where the head sits and
/// sensed there. Per <see cref="Step"/>: the body reports its points, the session decides (Lodur's Actor, which steps
/// the physiology first), the ragdoll is driven, the world steps. Harm enters only through <see cref="Wound"/>; the
/// session never decides death, blood or consciousness: the read-outs are Lodur's. A dead body is limp (the Actor writes
/// no strength) and the run ends a release after the death. After <see cref="Finished"/> <see cref="Step"/> only keeps
/// the body moving, as the chamber does.
/// </summary>
public sealed class OfficeBodySession : IDisposable
{
    private readonly SeatedBody _body;
    private readonly StressSession _session;
    private readonly Physiology _physiology;
    private readonly double _tickSeconds;
    private readonly int _handL, _handR, _pelvisLane;
    private bool _disposed;

    public OfficeBodySession(StressParameters p, ulong seed)
    {
        if (Math.Abs(p.TickSeconds - OfficeBody.TickSeconds) > 1e-12)
        {
            throw new ArgumentException($"The body run steps at 1/120 s; got TickSeconds {p.TickSeconds}.");
        }

        _tickSeconds = p.TickSeconds;
        _body = new SeatedBody();
        try
        {
            var plan = _body.Plan;
            _body.ReadSenses();
            int head = plan.SenseIndex("Head");
            _handL = plan.SenseIndex("Hand", Side.Left);
            _handR = plan.SenseIndex("Hand", Side.Right);
            _pelvisLane = _body.BodyLane(plan.PartIndex("Pelvis"));
            var physiologyPlan = PhysiologyPlans.Humanoid();
            _physiology = new Physiology(plan, physiologyPlan, p.SlowScale);
            // The desk's emitters sit where the head does and are sensed there.
            _session = new StressSession(p with { Setting = p.Setting.Shifted(_body.X[head], _body.Y[head], _body.Z[head]) }, seed,
                plan.SensePoints.Count, OfficeBody.Poses, head, _physiology, plan, physiologyPlan);
        }
        catch
        {
            _body.Dispose();
            throw;
        }
    }

    /// <summary>The live lanes of the world, for rendering. Read-only: nothing outside the body writes them.</summary>
    public Box3DSoaView View { get { Live(); return _body.View; } }

    /// <summary>Ticks run so far; the tick the next <see cref="Step"/> runs.</summary>
    public long TickIndex { get { Live(); return _session.TickIndex; } }

    public bool Finished { get { Live(); return _session.Finished; } }

    /// <summary>The Actor's state at the end of the latest tick, and the body's (Lodur's numbers, never BDS's).</summary>
    public double Will { get { Live(); return _session.Actor.Drive.Will; } }
    public double Arousal { get { Live(); return _session.Actor.Drive.Arousal; } }
    public double Resolve { get { Live(); return _session.Actor.Drive.Resolve; } }
    public double AirHunger { get { Live(); return _session.Actor.Drive.AirHunger; } }
    public double Consciousness { get { Live(); return _physiology.Consciousness; } }
    public double BloodLoss { get { Live(); return _physiology.BloodLoss; } }
    public bool Dead { get { Live(); return _physiology.Dead; } }
    public DeathCause DeathCause { get { Live(); return _physiology.Cause; } }
    public OfficeGoal Goal { get { Live(); return OfficeBrain.GoalAt(_session.Actor.GoalIndex); } }

    /// <summary>The hands and the pelvis as of the latest step (read from the world each time).</summary>
    public BodyPoint HandL { get { Live(); return Sense(_handL); } }
    public BodyPoint HandR { get { Live(); return Sense(_handR); } }
    public BodyPoint Pelvis
    {
        get
        {
            Live();
            var v = _body.View;
            return new BodyPoint(v.PositionX[_pelvisLane], v.PositionY[_pelvisLane], v.PositionZ[_pelvisLane]);
        }
    }

    /// <summary>One step of 1/120 s; see the type's summary for what it does after <see cref="Finished"/>.</summary>
    public void Step()
    {
        Live();
        if (!_session.Finished)
        {
            long tick = _session.TickIndex;
            _body.ReadSenses();
            var events = _session.Tick(_body.X, _body.Y, _body.Z, _body.Strengths, _body.Targets);
            if (events != TickEvents.None) _body.Enqueue(tick, events);
        }
        _body.DriveAndStep(_tickSeconds); // after Finished: the last strengths and targets again
    }

    /// <summary>Moves the queued events (oldest first) into <paramref name="buffer"/>; see <see cref="SeatedBody.DrainEvents"/>.</summary>
    public int DrainEvents(Span<BodyEvent> buffer)
    {
        Live();
        return _body.DrainEvents(buffer);
    }

    /// <summary>The player silences a stressor from the next tick; see <see cref="StressSession.Silence"/>.</summary>
    public void Silence(string stressorId)
    {
        Live();
        _session.Silence(stressorId);
    }

    /// <summary>Wounds the body's part from the next tick; false when refused (see <see cref="StressSession.Wound"/>).</summary>
    public bool Wound(int part, WoundTool tool)
    {
        Live();
        return _session.Wound(part, tool);
    }

    /// <summary>The nervous result so far, with the death if there was one.</summary>
    public StressResult Result()
    {
        Live();
        return _session.Result();
    }

    /// <summary>Frees the world; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _body.Dispose();
    }

    private BodyPoint Sense(int i)
    {
        _body.ReadSenses();
        return new BodyPoint(_body.X[i], _body.Y[i], _body.Z[i]);
    }

    private void Live() => ObjectDisposedException.ThrowIf(_disposed, this);
}
