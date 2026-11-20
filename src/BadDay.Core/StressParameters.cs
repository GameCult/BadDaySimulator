namespace BadDay.Core;

/// <summary>
/// Every tunable of the office stress scenario: the clock, the setting, the cause, and Lodur's parameter records
/// as members. The cause's pull is the keep-composure goal's, and its sense nerve is the only sense.
/// Defaults are placeholders, not measured.
/// </summary>
public sealed record StressParameters
{
    public double TickSeconds { get; init; } = 0.01;
    public double MaxSeconds { get; init; } = 180.0;
    public double SampleEverySeconds { get; init; } = 1.0;

    /// <summary>How long the run goes on after the rage takes over, seconds [P]: the geyser is seen, then the run ends.</summary>
    public double ReleaseSeconds { get; init; } = 6.0;

    /// <summary>Seconds from one rage key to the next [P]: wind-up left, strike left, wind-up right, strike right.</summary>
    public double RageKeySeconds { get; init; } = 0.25;

    /// <summary>The id of the Brain goal whose takeover starts the release: the run ends <see cref="ReleaseSeconds"/> after that goal's GoalChanged, and a change to any other goal does not. The session refuses an id the Brain lacks.</summary>
    public string ReleaseGoalId { get; init; } = OfficeBrain.RageId;

    public Setting Setting { get; init; } = Setting.Office;
    public Character Character { get; init; } = Character.OfficeFella;
    public Cause Cause { get; init; } = Cause.Office;

    // Lodur's records. The noxious nerve and the reflex idle here (no box); Hold is the composure goal's pose behaviour, which only a body reads.
    public Lodur.NerveParameters Nerve { get; init; } = new();
    public Lodur.DriveParameters Drive { get; init; } = new();
    public Lodur.WithdrawalParameters Reflex { get; init; } = new();
    public Lodur.HoldParameters Hold { get; init; } = new();

    /// <summary>Per-seed trait spread (fraction): the senses' gain and resolve drain.</summary>
    public double TraitJitter { get; init; } = 0.2;
}
