using System;
using Lodur;

namespace BadDay.Core;

/// <summary>What the office timeline's goal column names: the goal in force, by <see cref="OfficeBrain"/>'s index. Display only.</summary>
public enum OfficeGoal { KeepComposure, Rage, Calm, Clutch, Thrash }

/// <summary>
/// The poses Office Fella's goals write, one rotation per muscle: the composed pose he holds while he keeps his
/// composure (and the pose a leak would pull toward), the keys the rage walks in a loop, the clutch (both hands at the
/// throat) and the keys the thrash walks. The body supplies them
/// (<c>OfficeBody.Poses</c>); the headless run, which has no body, takes <see cref="Identity"/>.
/// </summary>
public sealed record OfficePoses(Quat[] Composed, Quat[] Flinch, Quat[][] RageKeys, Quat[] Clutch, Quat[][] ThrashKeys)
{
    /// <summary>One muscle, every pose the identity: the headless run's behaviour has no body to move.</summary>
    public static OfficePoses Identity { get; } = new(
        new[] { Quat.Identity }, new[] { Quat.Identity },
        new[] { new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity } },
        new[] { Quat.Identity },
        new[] { new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity } });
}

/// <summary>
/// Office Fella's Brain: keep-composure (the job, pulling with <see cref="Cause.ObjectivePull"/>) until the will
/// breaks, then rage (a geyser: no pull, nothing held down, the rage keys in a loop; it refills his will on entry, is
/// involuntary for its first <see cref="StressParameters.RageOnsetSeconds"/> and then costs will at
/// <see cref="StressParameters.RageEffort"/> a second), until that will runs dry and breaks a second time, and then calm:
/// his composed pose at normal strength, for good (ruling lodur:ruling:fella-calm-after-rage); entering it fades his arousal and his senses' gain to nothing over
/// <see cref="StressParameters.CalmFadeSeconds"/> (rulings lodur:ruling:stress-bar-shows-arousal-calm-silences and lodur:ruling:flinch-tail-after-stimulus), so the stress bar
/// empties while the office keeps sounding. Calm is terminal as
/// data among the stress goals: it gates on Breaks, which never falls, and only the two air-hunger goals outrank it, since air
/// hunger is physiology, not a fit (ruling lodur:ruling:fella-calm-after-rage is about the rage). Air hunger, from a breath tried
/// and not got (<see cref="StressParameters.ClutchAirHunger"/>), makes him clutch his throat; once his will has broken while it
/// holds he thrashes (rulings lodur:ruling:bds-air-hunger-clutch-then-thrash, lodur:ruling:bds-thrash-when-will-breaks). Both are
/// involuntary throughout, so a will-weighted struggle never goes limp, and both sag with the Actor's motor ceiling, which
/// carries consciousness: BDS adds no sag goal. A Fella cut while his will is still broken thrashes at once. Authored as data; the Actor commits the
/// switches, so the timeline's goal column is read back from <see cref="Actor.GoalIndex"/>.
/// </summary>
public static class OfficeBrain
{
    public const int ComposureIndex = 0;
    public const int RageIndex = 1;
    public const int CalmIndex = 2;
    public const int ClutchIndex = 3;
    public const int ThrashIndex = 4;

    public const string RageId = "rage";

    /// <summary>The calm goal's id.</summary>
    public const string CalmId = "calm";

    /// <summary>The clutch goal's id.</summary>
    public const string ClutchId = "clutch";

    /// <summary>The thrash goal's id.</summary>
    public const string ThrashId = "thrash";

    public static OfficeGoal GoalAt(int index) => index switch { RageIndex => OfficeGoal.Rage, CalmIndex => OfficeGoal.Calm, ClutchIndex => OfficeGoal.Clutch, ThrashIndex => OfficeGoal.Thrash, _ => OfficeGoal.KeepComposure };

    public static Brain Build(StressParameters p, OfficePoses poses) => new()
    {
        Start = ComposureIndex,
        Goals = new[]
        {
            new GoalSpec
            {
                Id = "keep-composure",
                Priority = 0.0,
                Pull = p.Cause.ObjectivePull,
                Behaviour = new PoseBehaviour { Keys = new[] { poses.Composed }, FlinchPose = poses.Flinch, Hold = p.Hold },
            },
            new GoalSpec
            {
                Id = RageId,
                Priority = 1.0,
                Pull = 0.0,
                When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) },
                EntryResolve = 1.0,
                Effort = p.RageEffort,
                InvoluntarySeconds = p.RageOnsetSeconds,
                Behaviour = new PoseBehaviour
                {
                    Keys = poses.RageKeys,
                    SecondsPerKey = p.RageKeySeconds,
                    FlinchPose = poses.RageKeys[0],
                    Hold = p.Hold with { HoldStrength = 1f, FlinchGain = 0.0, StrainShake = 0.0 },
                },
            },
            new GoalSpec
            {
                Id = CalmId,
                Priority = 2.0,
                When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 2.0) },
                InvoluntarySeconds = double.PositiveInfinity,
                EntryArousal = 0.0,
                SensoryGain = 0.0,
                EntrySeconds = p.CalmFadeSeconds,
                Behaviour = new PoseBehaviour { Keys = new[] { poses.Composed }, FlinchPose = poses.Flinch, Hold = p.Hold },
            },
            new GoalSpec
            {
                Id = ClutchId,
                Priority = 3.0,
                When = new[] { new Condition(DriveQuantity.AirHunger, ConditionOp.AtLeast, p.ClutchAirHunger) },
                InvoluntarySeconds = double.PositiveInfinity,
                Pull = 0.0,
                Behaviour = new PoseBehaviour { Keys = new[] { poses.Clutch }, FlinchPose = poses.Clutch, Hold = p.Hold with { FlinchGain = 0.0 } },
            },
            new GoalSpec
            {
                Id = ThrashId,
                Priority = 4.0,
                When = new[] { new Condition(DriveQuantity.WillBroken, ConditionOp.AtLeast, 1.0), new Condition(DriveQuantity.AirHunger, ConditionOp.AtLeast, p.ClutchAirHunger) },
                InvoluntarySeconds = double.PositiveInfinity,
                Behaviour = new PoseBehaviour
                {
                    Keys = poses.ThrashKeys,
                    SecondsPerKey = p.ThrashKeySeconds,
                    FlinchPose = poses.ThrashKeys[0],
                    Hold = p.Hold with { HoldStrength = 1f, FlinchGain = 0.0, StrainShake = 0.0 },
                },
            },
        },
    };
}
