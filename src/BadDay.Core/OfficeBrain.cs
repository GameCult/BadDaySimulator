using System;
using Lodur;

namespace BadDay.Core;

/// <summary>What the office timeline's goal column names: the goal in force, by <see cref="OfficeBrain"/>'s index. Display only.</summary>
public enum OfficeGoal { KeepComposure, Rage }

/// <summary>
/// The poses Office Fella's goals write, one rotation per muscle: the composed pose he holds while he keeps his
/// composure (and the pose a leak would pull toward), and the keys the rage walks in a loop. The body supplies them
/// (<c>OfficeBody.Poses</c>); the headless run, which has no body, takes <see cref="Identity"/>.
/// </summary>
public sealed record OfficePoses(Quat[] Composed, Quat[] Flinch, Quat[][] RageKeys)
{
    /// <summary>One muscle, every pose the identity: the headless run's behaviour has no body to move.</summary>
    public static OfficePoses Identity { get; } = new(
        new[] { Quat.Identity }, new[] { Quat.Identity },
        new[] { new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity }, new[] { Quat.Identity } });
}

/// <summary>
/// Office Fella's Brain: keep-composure (the job, pulling with <see cref="Cause.ObjectivePull"/>) until the will
/// breaks, then rage (a geyser: no pull, nothing held down, the rage keys in a loop). Authored as data; the Actor
/// commits the switch, so the timeline's goal column is read back from <see cref="Actor.GoalIndex"/>.
/// </summary>
public static class OfficeBrain
{
    public const int ComposureIndex = 0;
    public const int RageIndex = 1;

    /// <summary>The rage goal's id: what the scenario's release waits for.</summary>
    public const string RageId = "rage";

    public static OfficeGoal GoalAt(int index) => index == RageIndex ? OfficeGoal.Rage : OfficeGoal.KeepComposure;

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
                When = new[] { new Condition(DriveQuantity.WillBroken, ConditionOp.AtLeast, 1.0) },
                Behaviour = new PoseBehaviour
                {
                    Keys = poses.RageKeys,
                    SecondsPerKey = p.RageKeySeconds,
                    FlinchPose = poses.RageKeys[0],
                    Hold = p.Hold with { HoldStrength = 1f, FlinchGain = 0.0, StrainShake = 0.0 },
                },
            },
        },
    };
}
