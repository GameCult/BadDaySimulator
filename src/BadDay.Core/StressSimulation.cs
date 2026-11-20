using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// The headless office: one virtual human with the head fixed at the sense point, run through
/// <see cref="StressSession"/> to the end of the release after the break, or MaxSeconds. The silences are what a player would have done, each
/// applied at the tick its time falls on, before that tick runs.
/// </summary>
public static class StressSimulation
{
    public static StressResult Run(StressParameters p, ulong seed, IReadOnlyList<SilenceCommand>? silences = null)
    {
        var session = new StressSession(p, seed, 1, OfficePoses.Identity);
        var point = new float[1];       // the head sits at the origin
        var strengths = new float[1];   // the behaviour's output has no body to move
        var targets = new Quat[1];
        var pending = (silences ?? Array.Empty<SilenceCommand>()).OrderBy(s => s.AtSeconds).ToList();
        while (!session.Finished)
        {
            while (pending.Count > 0 && Math.Round(pending[0].AtSeconds / p.TickSeconds) <= session.TickIndex)
            {
                session.Silence(pending[0].StressorId);
                pending.RemoveAt(0);
            }
            session.Tick(point, point, point, strengths, targets);
        }
        return session.Result();
    }
}

/// <summary>Consecutive seeds of the office, summarised: how many broke, when, and how close the others came.</summary>
public sealed record StressSweep(
    int Runs, int Broke, int Endured,
    long? MinBreakMs, long? MedianBreakMs, long? MaxBreakMs,
    double? MinEnduredResolve,
    IReadOnlyList<StressResult> Results)
{
    public static StressSweep Run(StressParameters p, ulong firstSeed, int count, IReadOnlyList<SilenceCommand>? silences = null)
    {
        var results = new List<StressResult>(count);
        for (int i = 0; i < count; i++) results.Add(StressSimulation.Run(p, firstSeed + (ulong)i, silences));
        var breaks = results.Where(r => r.TimeToBreakMs.HasValue).Select(r => r.TimeToBreakMs!.Value).OrderBy(x => x).ToList();
        var endured = results.Where(r => r.Outcome == Outcome.Endured).ToList();
        return new StressSweep(
            count, breaks.Count, endured.Count,
            breaks.Count > 0 ? breaks[0] : null,
            breaks.Count > 0 ? breaks[breaks.Count / 2] : null,
            breaks.Count > 0 ? breaks[^1] : null,
            endured.Count > 0 ? endured.Min(r => r.MinResolve) : null,
            results);
    }
}
