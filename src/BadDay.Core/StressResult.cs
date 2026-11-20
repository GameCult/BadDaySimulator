using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BadDay.Core;

public enum StressEntryKind { Sample, Silenced, WillBroke, Endured }

/// <summary>One timeline row: end-of-tick state read from the Actor. Audio, Visual and Interrupt are the stimulus that reached the head on each channel.</summary>
public readonly record struct StressEntry(
    long TimeMs, StressEntryKind Kind, double Audio, double Visual, double Interrupt,
    double Load, double Arousal, double Resolve, Goal Goal);

/// <summary>A player's command to silence one stressor from <see cref="AtSeconds"/> on.</summary>
public readonly record struct SilenceCommand(string StressorId, double AtSeconds);

public sealed record StressResult(
    ulong Seed,
    /// <summary>WillBroke or Endured; the office has no box to disable.</summary>
    Outcome Outcome,
    long EndMs,
    /// <summary>Time resolve hit zero, ms; null unless the will broke.</summary>
    long? TimeToBreakMs,
    /// <summary>The lowest resolve at any tick of the run.</summary>
    double MinResolve,
    IReadOnlyList<StressEntry> Timeline)
{
    /// <summary>What the stress bar means (lodur:question:stress-bar-meaning, recommendation taken): composure already lost, 1 - resolve.</summary>
    public const string StressBarLabel = "lost composure";

    /// <summary>Deterministic text form (invariant culture, fixed precision). The stress column is 1 - resolve, display only.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("time_ms kind audio visual interrupt load arousal resolve stress goal\n");
        foreach (var e in Timeline)
        {
            sb.Append(e.TimeMs.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Kind);
            foreach (var v in new[] { e.Audio, e.Visual, e.Interrupt, e.Load, e.Arousal, e.Resolve, 1.0 - e.Resolve })
            {
                sb.Append(' ').Append(v.ToString("F3", CultureInfo.InvariantCulture));
            }
            sb.Append(' ').Append(e.Goal).Append('\n');
        }
        return sb.ToString();
    }

    public string Summary() => Outcome == Outcome.WillBroke
        ? $"seed {Seed}: will broke at {TimeToBreakMs} ms"
        : $"seed {Seed}: endured to {EndMs} ms, will held, min resolve {MinResolve.ToString("F3", CultureInfo.InvariantCulture)}";
}
