using System;
using System.Collections.Generic;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// One run of the office: the seed's traits, the cause's schedule writing the setting's emitters, a Lodur
/// <see cref="Actor"/>, and the recorder. The headless run, the sweep, the CLI and a client all drive it one
/// <see cref="Tick"/> per step, and the player's lever is <see cref="Silence"/>. The session never writes drive
/// state, computes load or arousal, or holds a stress field: rows are end-of-tick state read from the Actor, and
/// nothing reads them back. The rage and the calm after it are the Brain's (<see cref="OfficeBrain"/>): the session logs each tick the Actor commits a switch. It has no end of its own
/// (ruling bds-office-is-a-sandbox-preset): the will's break is an event in the scene, and the caller stops stepping when the player ends it.
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
    private readonly long _sampleEvery;
    private readonly Physiology? _physiology;
    private readonly PhysiologyPlan? _physiologyPlan;
    private readonly int[]? _stackOf;
    private readonly int _cut;
    private long _breakTick = -1;
    private double _minResolve = 1.0;
    private Outcome _outcome = Outcome.Endured;
    private long _diedTick = -1;

    /// <param name="poses">What the Brain's goals write, one rotation per muscle; the Actor's muscle count is theirs.</param>
    /// <param name="physiology">The body's physiology, stepped by the Actor (null: a headless run, nothing can be wounded or die); it needs the <paramref name="body"/> it was built on and its <paramref name="physiologyPlan"/>.</param>
    public StressSession(StressParameters p, ulong seed, int regionCount, OfficePoses poses, int senseRegion = 0,
        Physiology? physiology = null, BodyPlan? body = null, PhysiologyPlan? physiologyPlan = null)
    {
        _p = p;
        if (physiology is not null)
        {
            if (body is null || physiologyPlan is null) throw new ArgumentException("a physiology needs the body and the physiology plan it was built on.");
            _physiology = physiology;
            _physiologyPlan = physiologyPlan;
            _stackOf = physiologyPlan.Resolve(body);
            _cut = physiologyPlan.AfflictionIndex("Cut");
        }
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
            p.Character.Senses with { Gain = p.Character.Senses.Gain * gainTrait }, senseRegion,
            physiology: physiology, body: body);
        _sampleEvery = Math.Max(1, Ticks(p.SampleEverySeconds));
    }

    public ulong Seed { get; }
    public Actor Actor { get; }

    /// <summary>The setting's emitters in its order, with the last tick's intensities written.</summary>
    public ReadOnlySpan<Emitter> Emitters => _emitters;

    /// <summary>Ticks run so far; the tick the next <see cref="Tick"/> runs.</summary>
    public long TickIndex => Actor.TickCount;

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
    /// The player wounds a part with a tool from the current tick on: one Cut affliction of the tool's severity on each layer its
    /// <see cref="WoundTool.Depth"/> reaches at <paramref name="point"/> (in the part's frame) through the part's PartTissuesDef.Reach,
    /// through <see cref="Physiology.Afflict"/>, the only way harm enters. All of the layers or none: false, and nothing wounded, for a
    /// headless session, a body that is dead (death is permanent, bds-death), a part the body
    /// lacks, or when the physiology has no room for every layer (its answer, not counted here).
    /// </summary>
    public bool Wound(int part, (float X, float Y, float Z) point, WoundTool tool)
    {
        if (_physiology is null || _physiologyPlan is null || _stackOf is null || (uint)part >= (uint)_stackOf.Length) return false;
        if (_physiology.Dead) return false;
        var stack = _physiologyPlan.Parts[_stackOf[part]];
        Span<int> layers = stackalloc int[stack.Tissues.Count];
        int n = stack.Reach(point, tool.Depth, layers);
        return n > 0 && _physiology.Afflict(_cut, part, layers[..n], tool.Severity);
    }

    /// <summary>
    /// One step: each stressor draws its jitter (always, so the stream never depends on state) and writes its
    /// schedule value; each emitter takes the largest of its unsilenced stressors; the Actor runs on the caller's
    /// points and on the airway the body found at its openings (<paramref name="airway"/>, nothing by default); the tick's rows are logged from the flags and the end-of-tick state.
    /// </summary>
    public TickEvents Tick(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z,
        Span<float> strengths, Span<Quat> targets, in AirwaySample airway = default)
    {
        long tick = TickIndex;
        for (int e = 0; e < _emitters.Length; e++) _emitters[e].Intensity = 0.0;
        for (int i = 0; i < _stressors.Length; i++)
        {
            double value = _stressors[i].Value(tick, _p.TickSeconds, _rng.NextSigned());
            if (!_silenced[i] && value > _emitters[_emitterOf[i]].Intensity) _emitters[_emitterOf[i]].Intensity = value;
        }

        var events = Actor.Tick(x, y, z, default, strengths, targets, _emitters, airway);
        _minResolve = Math.Min(_minResolve, Actor.Drive.Resolve);

        if ((events & TickEvents.WillBroke) != 0)
        {
            if (_breakTick < 0) _breakTick = tick; // the snap; the rage's own break is a row, not the time to break
            _outcome = Outcome.WillBroke;
            Log(tick, StressEntryKind.WillBroke);
        }
        if ((events & TickEvents.Died) != 0)
        {
            // The Actor reported the body's death (once); it is limp and frozen from here.
            _diedTick = tick;
            _outcome = Outcome.Died;
            Log(tick, StressEntryKind.Died);
        }
        if (tick % _sampleEvery == 0) Log(tick, StressEntryKind.Sample);
        if ((events & TickEvents.GoalChanged) != 0)
        {
            Log(tick, StressEntryKind.GoalChanged);
        }
        return events;
    }

    public StressResult Result() => new(
        Seed, _outcome, Ms(TickIndex), _breakTick >= 0 ? Ms(_breakTick) : null, _minResolve, _timeline,
        _diedTick >= 0 ? Ms(_diedTick) : null, _diedTick >= 0 ? _physiology!.Cause : DeathCause.None);

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
