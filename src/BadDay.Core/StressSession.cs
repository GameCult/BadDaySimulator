using System;
using System.Collections.Generic;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// One run of the office: the seed's traits, the cause's schedule writing the setting's emitters, a Lodur
/// <see cref="Actor"/>, and the recorder. The headless run, the sweep, the CLI and a client all drive it one
/// <see cref="Tick"/> per step, and the player's lever is <see cref="Silence"/>. The session never writes drive
/// state, computes load or arousal, or holds a stress field: rows are end-of-tick state read from the Actor, and
/// nothing reads them back. The rage is the Brain's (<see cref="OfficeBrain"/>): the session goes on past the break, logs the
/// tick the Actor commits the switch, and ends ReleaseSeconds after it, or at MaxSeconds.
/// </summary>
public sealed class StressSession
{
    private readonly StressParameters _p;
    private readonly SplitMix64 _rng;
    private readonly Stressor[] _stressors;
    private readonly int[] _emitterOf;
    private readonly bool[] _silenced;
    private readonly Emitter[] _emitters;
    private readonly List<StressEntry> _timeline = new();
    private readonly long _maxTick;
    private readonly long _sampleEvery;
    private readonly long _releaseTicks;
    private long _breakTick = -1;
    private double _minResolve = 1.0;
    private Outcome _outcome = Outcome.Endured;
    private long _endTick;

    /// <param name="poses">What the Brain's goals write, one rotation per muscle; the Actor's muscle count is theirs.</param>
    public StressSession(StressParameters p, ulong seed, int regionCount, OfficePoses poses, int senseRegion = 0)
    {
        _p = p;
        _stressors = new Stressor[p.Cause.Stressors.Count];
        _emitterOf = new int[_stressors.Length];
        _silenced = new bool[_stressors.Length];
        _emitters = new Emitter[p.Setting.Emitters.Count];
        for (int e = 0; e < _emitters.Length; e++)
        {
            _emitters[e] = p.Setting.Emitters[e].Emitter;
            for (int f = 0; f < e; f++)
            {
                if (p.Setting.Emitters[f].Id == p.Setting.Emitters[e].Id) throw new ArgumentException($"two emitters named '{p.Setting.Emitters[e].Id}'");
            }
        }
        for (int i = 0; i < _stressors.Length; i++)
        {
            _stressors[i] = p.Cause.Stressors[i];
            for (int f = 0; f < i; f++)
            {
                if (_stressors[f].Id == _stressors[i].Id) throw new ArgumentException($"two stressors named '{_stressors[i].Id}'");
            }
            _emitterOf[i] = EmitterIndex(_stressors[i].EmitterId);
            if (_emitterOf[i] < 0) throw new ArgumentException($"stressor '{_stressors[i].Id}' names unknown emitter '{_stressors[i].EmitterId}'");
        }

        _rng = new SplitMix64(seed);
        // Per-seed traits, drawn first and in fixed order so the stream never shifts: the senses' gain and the resolve drain.
        double gainTrait = 1.0 + p.TraitJitter * _rng.NextSigned();
        double drainTrait = 1.0 + p.TraitJitter * _rng.NextSigned();
        Seed = seed;
        Actor = new Actor(p.TickSeconds, regionCount, p.Nerve, p.Reflex,
            p.Drive with { DrainTrait = p.Drive.DrainTrait * drainTrait },
            OfficeBrain.Build(p, poses),
            p.Cause.Senses with { Gain = p.Cause.Senses.Gain * gainTrait }, senseRegion);
        _maxTick = Ticks(p.MaxSeconds);
        _sampleEvery = Math.Max(1, Ticks(p.SampleEverySeconds));
        _releaseTicks = Ticks(p.ReleaseSeconds);
        _endTick = _maxTick;
    }

    public ulong Seed { get; }
    public Actor Actor { get; }

    /// <summary>The setting's emitters in its order, with the last tick's intensities written.</summary>
    public ReadOnlySpan<Emitter> Emitters => _emitters;

    /// <summary>Ticks run so far; the tick the next <see cref="Tick"/> runs.</summary>
    public long TickIndex => Actor.TickCount;

    /// <summary>True once the release after the rage took over is over, or the time ran out.</summary>
    public bool Finished { get; private set; }

    /// <summary>
    /// The player silences a stressor: from the current tick on it writes nothing. Silencing a silenced stressor
    /// changes nothing; an unknown id is refused.
    /// </summary>
    public void Silence(string stressorId)
    {
        int i = Array.FindIndex(_stressors, s => s.Id == stressorId);
        if (i < 0) throw new ArgumentException($"unknown stressor '{stressorId}'", nameof(stressorId));
        if (_silenced[i]) return;
        _silenced[i] = true;
        Log(TickIndex, StressEntryKind.Silenced);
    }

    /// <summary>
    /// One step: each stressor draws its jitter (always, so the stream never depends on state) and writes its
    /// schedule value; each emitter takes the largest of its unsilenced stressors; the Actor runs on the caller's
    /// points; the tick's rows are logged from the flags and the end-of-tick state.
    /// </summary>
    public TickEvents Tick(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z,
        Span<float> strengths, Span<Quat> targets)
    {
        long tick = TickIndex;
        for (int e = 0; e < _emitters.Length; e++) _emitters[e].Intensity = 0.0;
        for (int i = 0; i < _stressors.Length; i++)
        {
            double value = _stressors[i].Value(tick, _p.TickSeconds, _rng.NextSigned());
            if (!_silenced[i] && value > _emitters[_emitterOf[i]].Intensity) _emitters[_emitterOf[i]].Intensity = value;
        }

        var events = Actor.Tick(x, y, z, default, strengths, targets, _emitters);
        _minResolve = Math.Min(_minResolve, Actor.Drive.Resolve);

        if ((events & TickEvents.WillBroke) != 0)
        {
            _breakTick = tick;
            _outcome = Outcome.WillBroke;
            Log(tick, StressEntryKind.WillBroke);
        }
        if (tick % _sampleEvery == 0) Log(tick, StressEntryKind.Sample);
        if ((events & TickEvents.GoalChanged) != 0)
        {
            _endTick = Math.Min(_maxTick, tick + _releaseTicks);
            Log(tick, StressEntryKind.GoalChanged);
        }
        if (tick >= _endTick)
        {
            if (_outcome == Outcome.Endured) Log(tick, StressEntryKind.Endured);
            Finished = true;
        }
        return events;
    }

    public StressResult Result() => new(
        Seed, _outcome, Ms(_endTick), _breakTick >= 0 ? Ms(_breakTick) : null, _minResolve, _timeline);

    public long Ms(long tick) => (long)Math.Round(tick * _p.TickSeconds * 1000.0);

    private long Ticks(double seconds) => (long)Math.Round(seconds / _p.TickSeconds);

    private int EmitterIndex(string id)
    {
        for (int e = 0; e < _p.Setting.Emitters.Count; e++) if (_p.Setting.Emitters[e].Id == id) return e;
        return -1;
    }

    private void Log(long tick, StressEntryKind kind)
    {
        var d = Actor.Drive;
        _timeline.Add(new StressEntry(Ms(tick), kind,
            Actor.ChannelStimulus(Channel.Audio), Actor.ChannelStimulus(Channel.Visual), Actor.ChannelStimulus(Channel.Interrupt),
            Actor.Load, d.Arousal, d.Resolve,
            OfficeBrain.GoalAt(Actor.GoalIndex)));
    }
}
