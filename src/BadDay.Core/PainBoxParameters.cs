namespace BadDay.Core;

/// <summary>
/// Every tunable of the pain box slice. Defaults are placeholders chosen so the
/// default run breaks in a readable time, not measured physiology: the figures
/// Lodur's target marks [M] (withdrawal 60-150 ms, voluntary ~200 ms) are
/// unverified, and so are these.
/// </summary>
public sealed record PainBoxParameters
{
    // Clock
    public double TickSeconds { get; init; } = 0.01;
    public double MaxSeconds { get; init; } = 120.0;

    // The box: noxious intensity on the hand, 0..1, ramped in, with seeded jitter.
    public double BoxIntensity { get; init; } = 0.8;
    public double BoxRampSeconds { get; init; } = 2.0;
    public double BoxNoise { get; init; } = 0.1;
    /// <summary>When set, the box switches off at this time (the game disables it).</summary>
    public double? BoxDisableAtSeconds { get; init; }

    // The nervous model is Lodur's: nerve, drive, reflex and the hold behaviour keep their records and defaults.
    // Hold matters to the body run only; the headless run ignores what it writes.
    public Lodur.NerveParameters Nerve { get; init; } = new();
    public Lodur.DriveParameters Drive { get; init; } = new();
    public Lodur.WithdrawalParameters Reflex { get; init; } = new();
    public Lodur.HoldParameters Hold { get; init; } = new();

    // Per-seed trait spread (fraction): nociceptor gain (Nerve.Gain) and resolve drain (Drive.DrainTrait).
    public double TraitJitter { get; init; } = 0.2;

    public double SampleEverySeconds { get; init; } = 1.0;
}
