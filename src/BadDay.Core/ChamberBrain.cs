using System;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// The Synaptic Induction Chamber's Brain: what the volunteer wants to be doing, authored as data for Lodur's Actor to
/// run. He holds his hand in the box for the payment (the objective, pulling Payment / (Payment + DollarsAtHalfPull))
/// until the will breaks, then withdraws. No session decides a switch; the Actor commits the Brain's choice, so the
/// goal column of every timeline is read back from <see cref="Actor.GoalIndex"/>.
/// </summary>
public static class ChamberBrain
{
    public const int HoldIndex = 0;
    public const int WithdrawIndex = 1;

    /// <summary>The objective's pull for the payment: 0 for nothing, 0.5 at <see cref="PainBoxParameters.DollarsAtHalfPull"/>, toward 1 for more.</summary>
    public static double Pull(PainBoxParameters p)
    {
        if (!(p.Payment >= 0.0 && double.IsFinite(p.Payment))) throw new ArgumentException($"Payment must be finite and not negative, got {p.Payment}.");
        if (!(p.DollarsAtHalfPull > 0.0 && double.IsFinite(p.DollarsAtHalfPull))) throw new ArgumentException($"DollarsAtHalfPull must be finite and positive, got {p.DollarsAtHalfPull}.");
        return p.Payment / (p.Payment + p.DollarsAtHalfPull);
    }

    /// <summary>The chamber's display name for the goal at <paramref name="index"/> of this Brain.</summary>
    public static Goal GoalAt(int index) => index == WithdrawIndex ? Goal.Withdraw : Goal.HoldHand;

    /// <summary>
    /// hold-hand: always eligible, priority 0, the payment's pull, the hold pose with the reflex leak and strain shake
    /// pulling toward the withdraw pose. withdraw: eligible once the will has broken, priority 1, no pull, the withdraw
    /// pose with no leak or shake.
    /// </summary>
    public static Brain Build(PainBoxParameters p, ReadOnlySpan<Quat> hold, ReadOnlySpan<Quat> withdraw)
    {
        var holdPose = hold.ToArray();
        var withdrawPose = withdraw.ToArray();
        return new Brain
        {
            Start = HoldIndex,
            Goals = new[]
            {
                new GoalSpec
                {
                    Id = "hold-hand",
                    Priority = 0.0,
                    Pull = Pull(p),
                    Behaviour = new PoseBehaviour { Keys = new[] { holdPose }, FlinchPose = withdrawPose, Hold = p.Hold },
                },
                new GoalSpec
                {
                    Id = "withdraw",
                    Priority = 1.0,
                    Pull = 0.0,
                    When = new[] { new Condition(DriveQuantity.WillBroken, ConditionOp.AtLeast, 1.0) },
                    Behaviour = new PoseBehaviour
                    {
                        Keys = new[] { withdrawPose },
                        FlinchPose = withdrawPose,
                        Hold = p.Hold with { FlinchGain = 0.0, StrainShake = 0.0 },
                    },
                },
            },
        };
    }
}
