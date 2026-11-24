using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>
/// The one place BDS builds a seated puppet's world: a ground, a static stool box, the ragdoll, and the weld that
/// holds its pelvis to the stool, because the PD ragdoll cannot balance for the length of a scenario. Every body run
/// (the pain box, the office) builds through here, so no run has a world of its own.
/// </summary>
public static class SeatedPuppet
{
    /// <summary>Physics substeps per 1/120 s step. The ragdoll gains were tuned against 4; the behaviour tests, not this number, are the rule.</summary>
    public const int Substeps = 4;

    /// <summary>Bodies and joints a world needs for <see cref="Build"/>: the ragdoll, the ground and the stool, and the weld.</summary>
    public static (int Bodies, int Joints) Needs => (Ragdoll.Needs(Plan).Bodies + 2, Ragdoll.Needs(Plan).Joints + 1);

    /// <summary>The humanoid plan every BDS body is built from; classes resolve their labels against it once.</summary>
    public static BodyPlan Plan { get; } = BodyPlans.Humanoid();

    /// <summary>Adds the ground, the stool, the ragdoll (stable ids from 100) and the pelvis weld to <paramref name="world"/>, which must be sized by <see cref="Needs"/>.</summary>
    public static Ragdoll Build(Box3DSoaWorld world)
    {
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.AddJoint(Box3DSoaJointKind.Weld, stool, ragdoll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));
        return ragdoll;
    }
}
