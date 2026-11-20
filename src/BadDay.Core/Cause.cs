using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// One recurring source of load: when it starts, how it rises, how often it comes back, how long it lasts and how
/// hard, on one emitter of the <see cref="Setting"/>. Times are seconds; a null period is a single burst.
/// </summary>
public sealed record Stressor(
    string Id, string EmitterId,
    double OnsetSeconds, double RampSeconds, double? PeriodSeconds, double DurationSeconds,
    double Intensity, double Jitter)
{
    /// <summary>
    /// The intensity at <paramref name="tick"/>, in whole ticks so the burst edges never wander with float error:
    /// zero before the onset and outside a burst, else Intensity rising over the ramp, scaled by (1 + Jitter * draw).
    /// </summary>
    public double Value(long tick, double tickSeconds, double draw)
    {
        long t = tick - Ticks(OnsetSeconds, tickSeconds);
        if (t < 0) return 0.0;
        long phase = PeriodSeconds is { } p ? t % Math.Max(1, Ticks(p, tickSeconds)) : t;
        if (phase >= Ticks(DurationSeconds, tickSeconds)) return 0.0;
        long ramp = Ticks(RampSeconds, tickSeconds);
        double rise = ramp > 0 ? Math.Min(1.0, (double)phase / ramp) : 1.0;
        return Math.Max(0.0, Intensity * rise * (1.0 + Jitter * draw));
    }

    private static long Ticks(double seconds, double tickSeconds) => (long)Math.Round(seconds / tickSeconds);
}

/// <summary>
/// What goes wrong: the stressors and the objective's pull (the job). It holds no geometry (the <see cref="Setting"/>'s)
/// and no senses (the <see cref="Character"/>'s). Code defaults, not persisted.
/// </summary>
public sealed record Cause(IReadOnlyList<Stressor> Stressors, double ObjectivePull)
{
    /// <summary>
    /// The office's sensory-load cause [P]: printer audio 5 s on, every 8 s for 1 s at 0.25; ceiling light 15 s
    /// every 11 s for 2 s at 0.2; desk phone 25 s every 14 s for 3 s at 0.35; desktop interrupt 75 s every 20 s for
    /// 1 s at 0.3; pull 0.6. The chatty neighbour and the stand-up crowd wait for coworkers (rung 6).
    /// </summary>
    public static Cause Office { get; } = new(new[]
    {
        new Stressor("printer", "printer", 5.0, 0.0, 8.0, 1.0, 0.25, 0.1),
        new Stressor("ceiling-light", "ceiling-light", 15.0, 0.0, 11.0, 2.0, 0.2, 0.1),
        new Stressor("desk-phone", "desk-phone", 25.0, 0.0, 14.0, 3.0, 0.35, 0.1),
        new Stressor("desktop-interrupt", "desktop", 75.0, 0.0, 20.0, 1.0, 0.3, 0.1),
    }, 0.6);

    /// <summary>Every stressor's intensity times <paramref name="factor"/>.</summary>
    public Cause Scaled(double factor) => this with
    {
        Stressors = Stressors.Select(s => s with { Intensity = s.Intensity * factor }).ToArray(),
    };

    public Cause Without(string stressorId) => this with { Stressors = Stressors.Where(s => s.Id != stressorId).ToArray() };
}
