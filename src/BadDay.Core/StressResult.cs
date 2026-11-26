using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lodur;

namespace BadDay.Core;

public enum StressEntryKind { Sample, Silenced, WillBroke, GoalChanged, Died, Endured }

/// <summary>One timeline row: end-of-tick state read from the Actor. Audio, Visual and Interrupt are the stimulus that reached the head on each channel.</summary>
public readonly record struct StressEntry(
    long TimeMs, StressEntryKind Kind, double Audio, double Visual, double Interrupt,
    double Load, double Arousal, double Resolve, OfficeGoal Goal);

/// <summary>A player's command to silence one stressor from <see cref="AtSeconds"/> on.</summary>
public readonly record struct SilenceCommand(string StressorId, double AtSeconds);

public sealed record StressResult(
    ulong Seed,
    /// <summary>WillBroke (the run goes on through the release), Endured, or Died (a body with a physiology died); the office has no box to disable.</summary>
    Outcome Outcome,
    /// <summary>When the run ended: the release after calm took over, or MaxSeconds.</summary>
    long EndMs,
    /// <summary>Time resolve hit zero, ms; null unless the will broke.</summary>
    long? TimeToBreakMs,
    /// <summary>The lowest resolve at any tick of the run.</summary>
    double MinResolve,
    IReadOnlyList<StressEntry> Timeline,
    /// <summary>When the body died, ms (Lodur's Physiology decided it); null if it did not.</summary>
    long? DiedAtMs = null,
    /// <summary>What killed it; None unless <see cref="DiedAtMs"/> is set.</summary>
    DeathCause DeathCause = DeathCause.None)
{
    /// <summary>What the stress bar means (ruling lodur:ruling:stress-bar-shows-arousal-calm-silences): how wound up he is right now, his arousal. Will is not on this bar.</summary>
    public const string StressBarLabel = "stress (arousal)";

    /// <summary>Deterministic text form (invariant culture, fixed precision). The stress column is the arousal column, display only.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("time_ms kind audio visual interrupt load arousal resolve stress goal\n");
        foreach (var e in Timeline)
        {
            sb.Append(e.TimeMs.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Kind);
            foreach (var v in new[] { e.Audio, e.Visual, e.Interrupt, e.Load, e.Arousal, e.Resolve, e.Arousal })
            {
                sb.Append(' ').Append(v.ToString("F3", CultureInfo.InvariantCulture));
            }
            sb.Append(' ').Append(e.Goal).Append('\n');
        }
        return sb.ToString();
    }

    public string Summary() => Outcome == Outcome.Died
        ? $"seed {Seed}: died of {DeathCause} at {DiedAtMs} ms"
        : Outcome == Outcome.WillBroke
        ? $"seed {Seed}: will broke at {TimeToBreakMs} ms"
        : $"seed {Seed}: endured to {EndMs} ms, will held, min resolve {MinResolve.ToString("F3", CultureInfo.InvariantCulture)}";
}
