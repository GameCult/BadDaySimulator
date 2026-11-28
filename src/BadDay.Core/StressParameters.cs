namespace BadDay.Core;

/// <summary>
/// Every tunable of the office stress scenario: the clock, the setting, the cause, and Lodur's parameter records
/// as members. The cause's pull is the keep-composure goal's, and its sense nerve is the only sense.
/// Defaults are placeholders, not measured.
/// </summary>
public sealed record StressParameters
{
    public double TickSeconds { get; init; } = 0.01;
    public double SampleEverySeconds { get; init; } = 1.0;

    /// <summary>How long calm takes to fade his arousal and his senses' gain to nothing, seconds [P] (rulings lodur:ruling:stress-bar-shows-arousal-calm-silences, lodur:ruling:flinch-tail-after-stimulus): the aftershock tail, a few seconds, then he stops.</summary>
    public double CalmFadeSeconds { get; init; } = 3.0;

    /// <summary>What the rage costs his will per second while it is voluntary, resolve units [P]: the rage refills his will, and this is how fast it runs dry (about 20 s from full, so the rage lasts 5-20 s with the senses still pressing).</summary>
    public double RageEffort { get; init; } = 0.05;

    /// <summary>How long after it takes over the rage is involuntary, seconds [P]: full weight and no cost, the snap itself, before it becomes something he sustains.</summary>
    public double RageOnsetSeconds { get; init; } = 1.0;

    /// <summary>Seconds from one rage key to the next [P]: wind-up left, strike left, wind-up right, strike right.</summary>
    public double RageKeySeconds { get; init; } = 0.25;

    /// <summary>The air hunger at which Fella clutches his throat, drive units [P] (ruling lodur:ruling:bds-air-hunger-clutch-then-thrash): a breath tried and not got reads 0.6.</summary>
    public double ClutchAirHunger { get; init; } = 0.3;

    /// <summary>Seconds from one thrash key to the next [P] (ruling lodur:ruling:bds-thrash-when-will-breaks): a look knob, tuned on the capture.</summary>
    public double ThrashKeySeconds { get; init; } = 0.2;

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

    /// <summary>The per-session multiplier on slow body processes [P] (ruling lodur:ruling:bds-physiology-clock): a Slow affliction or regen runs this many times faster than its table rate; Acute ones do not move.</summary>
    public double SlowScale { get; init; } = 20.0;
}
