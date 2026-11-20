using System;
using System.Collections.Generic;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// One run of the pain box: the seed's traits, the box schedule, a Lodur <see cref="Actor"/>, and the recorder.
/// The headless run and the body run both drive it, one <see cref="Tick"/> per step, and differ only in where
/// the hand point comes from and what ends the run. The session decides nothing about the character: the
/// timeline rows are end-of-tick state read from the Actor, and nothing reads them back.
/// </summary>
public sealed class PainBoxSession
{
    private readonly PainBoxParameters _p;
    private readonly PainBox _box;
    private readonly BoxStimulus[] _sources;
    private readonly List<TimelineEntry> _timeline = new();
    private readonly long _maxTick;
    private readonly long _sampleEvery;
    private readonly int _handRegion;
    private double _stimulus;
    private long _breakTick = -1;
    private int _flinches;
    private Outcome _outcome = Outcome.Endured;
    private long _endTick;

    /// <param name="box">The stimulus volume the hand is held in; its intensity is the session's to write.</param>
    /// <param name="handRegion">The region of the hand in the box; the timeline's stimulus column is the stimulus there.</param>
    public PainBoxSession(PainBoxParameters p, ulong seed, BoxStimulus box, int regionCount,
        ReadOnlySpan<Quat> holdPose, ReadOnlySpan<Quat> withdrawPose, int handRegion = 0)
    {
        _handRegion = handRegion;
        _p = p;
        var rng = new SplitMix64(seed);
        // Per-seed traits, drawn first and in fixed order so the stream never shifts. They multiply the caller's
        // Nerve.Gain and Drive.DrainTrait (1.0 by default), so a caller's own value is a baseline, never overwritten.
        double gainTrait = 1.0 + p.TraitJitter * rng.NextSigned();
        double drainTrait = 1.0 + p.TraitJitter * rng.NextSigned();
        Seed = seed;
        Actor = new Actor(p.TickSeconds, regionCount, p.Nerve with { Gain = p.Nerve.Gain * gainTrait }, p.Reflex,
            p.Drive with { DrainTrait = p.Drive.DrainTrait * drainTrait }, ChamberBrain.Build(p, holdPose, withdrawPose));
        _box = new PainBox(p, rng);
        _sources = new[] { box };
        _maxTick = Ticks(p.MaxSeconds);
        _sampleEvery = Math.Max(1, Ticks(p.SampleEverySeconds));
        _endTick = _maxTick;
    }

    public ulong Seed { get; }
    public Actor Actor { get; }

    /// <summary>The stimulus volume, with this tick's intensity once <see cref="Tick"/> has run.</summary>
    public ref readonly BoxStimulus Box => ref _sources[0];

    /// <summary>Ticks run so far; the tick the next <see cref="Tick"/> runs.</summary>
    public long TickIndex => Actor.TickCount;

    /// <summary>True once the run is over: the box was disabled, the time ran out, or the caller ended it.</summary>
    public bool Finished { get; private set; }

    /// <summary>True from the tick the Brain's withdraw goal takes over, its deliberate latency after the will breaks.</summary>
    public bool Withdrew { get; private set; }

    /// <summary>
    /// Whether the box is on: until it is switched off (by <see cref="SwitchOff"/> or the parameters' disable time), from the
    /// tick that switch names. It is the box's state, not the run's: a run that ended on the will breaking still has a box
    /// the player can switch off.
    /// </summary>
    public bool BoxOn => TickIndex < _box.DisableTick;

    /// <summary>Sets the box intensity the ramp scales; it takes effect on the next <see cref="Tick"/>. <see cref="PainBox.Level"/> owns the range and refuses a level outside it.</summary>
    public void SetIntensity(double level) => _box.Level = level;

    /// <summary>Switches the box off from the next tick; the <see cref="Disabled"/> check then ends the run if the will still holds.</summary>
    public void SwitchOff() => _box.SwitchOff(TickIndex);

    /// <summary>
    /// The disable check, before ticking: if the box switches off now while the will still holds, the run ends
    /// with the will intact and this returns true. A will already broken is not given back by the box going off.
    /// </summary>
    public bool Disabled()
    {
        long tick = TickIndex;
        if (tick != _box.DisableTick || Actor.Drive.WillBroken) return false;
        _stimulus = 0.0;
        _outcome = Outcome.BoxDisabled;
        _endTick = tick;
        Log(tick, EntryKind.BoxDisabled);
        Finished = true;
        return true;
    }

    /// <summary>
    /// One step: the box writes its intensity, the Actor runs on the caller's hand points and writes the
    /// caller's strengths and targets, and the tick's rows are logged from the flags and the end-of-tick state.
    /// </summary>
    public TickEvents Tick(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z,
        Span<float> strengths, Span<Quat> targets)
    {
        long tick = TickIndex;
        _box.Write(tick, ref _sources[0]);
        var events = Actor.Tick(x, y, z, _sources, strengths, targets);
        _stimulus = Actor.Stimulus(_handRegion);

        if ((events & TickEvents.Flinch) != 0)
        {
            _flinches++;
            Log(tick, EntryKind.Flinch);
        }
        if ((events & TickEvents.WillBroke) != 0)
        {
            _breakTick = tick;
            Log(tick, EntryKind.WillBroke);
        }
        if (tick % _sampleEvery == 0) Log(tick, EntryKind.Sample);
        if ((events & TickEvents.GoalChanged) != 0)
        {
            Withdrew = true;
            _outcome = Outcome.WillBroke;
            _endTick = tick;
            Log(tick, EntryKind.Withdrew);
        }
        else if (tick == _maxTick && _outcome == Outcome.Endured)
        {
            Log(tick, EntryKind.Endured);
        }
        if (tick >= _maxTick) Finished = true;
        return events;
    }

    /// <summary>Ends the run at <paramref name="tick"/> instead of where the session would (the body run ends when the hand is out).</summary>
    public void EndAt(long tick)
    {
        _endTick = tick;
        Finished = true;
    }

    public PainBoxResult Result() => new(
        Seed, _outcome, Ms(_endTick),
        _breakTick >= 0 ? Ms(_breakTick) : null,
        _flinches, _timeline);

    public long Ms(long tick) => (long)Math.Round(tick * _p.TickSeconds * 1000.0);

    private long Ticks(double seconds) => (long)Math.Round(seconds / _p.TickSeconds);

    private void Log(long tick, EntryKind kind)
    {
        var d = Actor.Drive;
        _timeline.Add(new TimelineEntry(Ms(tick), kind, _stimulus, d.Pain, d.Strain, d.Resolve,
            ChamberBrain.GoalAt(Actor.GoalIndex)));
    }
}
