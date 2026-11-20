namespace BadDay.Core;

/// <summary>
/// Every tunable of the office stress scenario: the clock, the setting, the cause, and Lodur's parameter records
/// as members. The cause's pull and sense nerve win over <c>Drive.ObjectivePull</c> and are the only senses.
/// Defaults are placeholders, not measured.
/// </summary>
public sealed record StressParameters
{
    public double TickSeconds { get; init; } = 0.01;
    public double MaxSeconds { get; init; } = 180.0;
    public double SampleEverySeconds { get; init; } = 1.0;

    public Setting Setting { get; init; } = Setting.Office;
    public Cause Cause { get; init; } = Cause.Office;

    // Lodur's records. The noxious nerve and the reflex idle here (no box); Hold is the body's, which this run ignores.
    public Lodur.NerveParameters Nerve { get; init; } = new();
    public Lodur.DriveParameters Drive { get; init; } = new();
    public Lodur.WithdrawalParameters Reflex { get; init; } = new();
    public Lodur.HoldParameters Hold { get; init; } = new();

    /// <summary>Per-seed trait spread (fraction): the senses' gain and resolve drain.</summary>
    public double TraitJitter { get; init; } = 0.2;
}
