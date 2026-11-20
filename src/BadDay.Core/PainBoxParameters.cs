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

    // The nervous model is Lodur's: nerve, drive and reflex keep their records and defaults. Hold is the hold goal's pose
    // behaviour (the body run's; the headless run ignores what it writes). The goals themselves are ChamberBrain's.
    public Lodur.NerveParameters Nerve { get; init; } = new();
    public Lodur.DriveParameters Drive { get; init; } = new();
    public Lodur.WithdrawalParameters Reflex { get; init; } = new();
    public Lodur.HoldParameters Hold { get; init; } = new();

    // The objective: the volunteer is paid, and the payment sets how hard holding the hand in the box pulls
    // (ChamberBrain.Pull: Payment / (Payment + DollarsAtHalfPull), so DollarsAtHalfPull is the payment that pulls half). [P]
    public double Payment { get; init; } = 100.0;
    public double DollarsAtHalfPull { get; init; } = 100.0;

    // Per-seed trait spread (fraction): nociceptor gain and resolve drain. The seed's draw multiplies Nerve.Gain and Drive.DrainTrait.
    public double TraitJitter { get; init; } = 0.2;

    public double SampleEverySeconds { get; init; } = 1.0;
}
