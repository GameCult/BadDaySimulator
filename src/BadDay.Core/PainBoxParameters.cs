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

    // Nerve: threshold, sensitisation under sustained stimulus, conduction delay.
    public double NociceptorThreshold { get; init; } = 0.1;
    public double SensitisationGrowth { get; init; } = 0.04;
    public double SensitisationRecovery { get; init; } = 0.02;
    public double SensitisationCap { get; init; } = 2.0;
    public double ConductionDelaySeconds { get; init; } = 0.06;

    // Drive: pain, strain from holding a reflex down, resolve.
    public double PainRise { get; init; } = 1.0;
    public double PainDecay { get; init; } = 0.05;
    public double StrainRecover { get; init; } = 0.1;
    public double ResolveDrainPain { get; init; } = 0.012;
    public double ResolveDrainStrain { get; init; } = 0.05;
    public double ResolveRecovery { get; init; } = 0.02;
    public double ResolveRecoversBelowPain { get; init; } = 0.1;

    // Reflex: how much of it will can hold down, and the latencies.
    public double MaxSuppression { get; init; } = 0.95;
    public double FlinchLeakThreshold { get; init; } = 0.3;
    public double FlinchRefractorySeconds { get; init; } = 0.5;
    public double WithdrawalLatencySeconds { get; init; } = 0.1;

    // Goals: how hard the objective ("keep the hand in the box") pulls, 0..1.
    // It scales resolve drain by (1 - pull); it is not a second resolve bar.
    public double ObjectivePull { get; init; } = 0.5;

    // Per-seed trait spread (fraction): nociceptor gain and resolve drain.
    public double TraitJitter { get; init; } = 0.2;

    public double SampleEverySeconds { get; init; } = 1.0;
}
