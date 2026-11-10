using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BadDay.Core;

public enum Goal { HoldHand, Withdraw }

public enum Outcome
{
    /// <summary>Resolve reached zero before the box went off.</summary>
    WillBroke,
    /// <summary>The box was disabled while the will still held.</summary>
    BoxDisabled,
    /// <summary>MaxSeconds passed with the box on and the will intact.</summary>
    Endured,
}

public enum EntryKind { Sample, Flinch, WillBroke, Withdrew, BoxDisabled, Endured }

public readonly record struct TimelineEntry(
    long TimeMs, EntryKind Kind, double Stimulus, double Pain, double Strain, double Resolve, Goal Goal);

public sealed record PainBoxResult(
    ulong Seed,
    Outcome Outcome,
    /// <summary>Time the run ended, ms.</summary>
    long EndMs,
    /// <summary>Time resolve hit zero, ms; null unless <see cref="Outcome"/> is WillBroke.</summary>
    long? TimeToBreakMs,
    int Flinches,
    IReadOnlyList<TimelineEntry> Timeline)
{
    /// <summary>
    /// Deterministic text form (invariant culture, fixed precision). Two runs
    /// with the same seed and parameters render byte-identical.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("time_ms kind stimulus pain strain resolve goal\n");
        foreach (var e in Timeline)
        {
            sb.Append(e.TimeMs.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Kind).Append(' ')
              .Append(e.Stimulus.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Pain.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Strain.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Resolve.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Goal).Append('\n');
        }
        return sb.ToString();
    }

    public string Summary() => Outcome switch
    {
        Outcome.WillBroke => $"seed {Seed}: will broke at {TimeToBreakMs} ms, hand out at {EndMs} ms, {Flinches} flinches",
        Outcome.BoxDisabled => $"seed {Seed}: box disabled at {EndMs} ms, will held, {Flinches} flinches",
        _ => $"seed {Seed}: endured to {EndMs} ms, will held, {Flinches} flinches",
    };
}
