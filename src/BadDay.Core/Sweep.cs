using System.Collections.Generic;
using System.Linq;

namespace BadDay.Core;

/// <summary>Runs consecutive seeds and summarises time-to-break. The break-time measure.</summary>
public sealed record SweepSummary(
    int Runs, int Broke, int BoxDisabled, int Endured,
    long? MinBreakMs, long? MedianBreakMs, long? MaxBreakMs,
    IReadOnlyList<PainBoxResult> Results)
{
    public static SweepSummary Run(PainBoxParameters p, ulong firstSeed, int count)
    {
        var results = new List<PainBoxResult>(count);
        for (int i = 0; i < count; i++) results.Add(PainBoxSimulation.Run(p, firstSeed + (ulong)i));
        var breaks = results.Where(r => r.TimeToBreakMs.HasValue).Select(r => r.TimeToBreakMs!.Value).OrderBy(x => x).ToList();
        return new SweepSummary(
            count,
            results.Count(r => r.Outcome == Outcome.WillBroke),
            results.Count(r => r.Outcome == Outcome.BoxDisabled),
            results.Count(r => r.Outcome == Outcome.Endured),
            breaks.Count > 0 ? breaks[0] : null,
            breaks.Count > 0 ? breaks[breaks.Count / 2] : null,
            breaks.Count > 0 ? breaks[^1] : null,
            results);
    }
}
