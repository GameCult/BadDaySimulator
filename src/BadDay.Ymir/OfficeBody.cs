using System;
using System.Collections.Generic;
using System.Linq;
using BadDay.Core;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>A point in the world, metres.</summary>
public readonly record struct BodyPoint(float X, float Y, float Z);

/// <summary>
/// The office on the body: the same timeline as the headless run, plus what only a body has.
/// <see cref="HandL"/>, <see cref="HandR"/> and <see cref="Pelvis"/> hold the point after every step; the index is the tick.
/// </summary>
public sealed record OfficeBodyResult(
    StressResult Run, IReadOnlyList<BodyPoint> HandL, IReadOnlyList<BodyPoint> HandR, IReadOnlyList<BodyPoint> Pelvis)
{
    /// <summary>The tick the rage took over (the GoalChanged row); null if the will held.</summary>
    public int? RageTick => Run.Timeline.Any(e => e.Kind == StressEntryKind.GoalChanged)
        ? (int)Math.Round(Run.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged).TimeMs * OfficeBody.Hz / 1000.0)
        : null;

    /// <summary>Strikes by both hands from tick <paramref name="from"/> up to <paramref name="to"/>, see <see cref="OfficeBody.CountStrikes"/>.</summary>
    public int Strikes(int from, int to) => OfficeBody.CountStrikes(HandL, from, to) + OfficeBody.CountStrikes(HandR, from, to);
}

/// <summary>
/// Office Fella on the seated puppet: <see cref="SeatedPuppet"/>'s world, the desk's emitters moved to where the head
/// sits and sensed there, and the same <see cref="StressSession"/> the headless run drives. Per step: the body reports its
/// points, the session decides (Lodur's Actor commits the Brain's goals, <see cref="OfficeBrain"/>), the ragdoll is driven,
/// the world steps. The run ends as the session ends, the release after the rage takes over.
/// </summary>
public static class OfficeBody
{
    public const int Hz = 120;

    /// <summary>The ragdoll's gains are tuned at 120 Hz.</summary>
    public const double TickSeconds = 1.0 / Hz;

    /// <summary>A strike begins when a hand's forward (+z) speed passes this, m/s [P].</summary>
    public const float StrikeSpeed = 1.0f;

    /// <summary>A strike is over when the forward speed falls back under this fraction of <see cref="StrikeSpeed"/>.</summary>
    public const float StrikeEnds = 0.5f;

    /// <summary>
    /// The office puppet's poses [P]. Composed: every muscle at the creation pose, arms down. Rage: each arm winds up (the
    /// elbow folded, the fist at the chest) and strikes (the shoulder forward, the elbow straight), left then right.
    /// </summary>
    public static OfficePoses Poses { get; } = BuildPoses();

    /// <summary>Runs the office on the puppet to the end of the release; the tick rate must be 1/120 s.</summary>
    public static OfficeBodyResult Run(StressParameters p, ulong seed)
    {
        if (Math.Abs(p.TickSeconds - TickSeconds) > 1e-12)
        {
            throw new ArgumentException($"The body run steps at 1/120 s; got TickSeconds {p.TickSeconds}.");
        }

        var needs = SeatedPuppet.Needs;
        using var world = new Box3DSoaWorld(capacity: needs.Bodies, jointCapacity: needs.Joints);
        var ragdoll = SeatedPuppet.Build(world);
        var x = new float[HumanoidRagdoll.RegionCount];
        var y = new float[HumanoidRagdoll.RegionCount];
        var z = new float[HumanoidRagdoll.RegionCount];
        ragdoll.ReadRegions(x, y, z);
        int head = (int)Region.Head;
        // The desk's emitters sit where the head does and are sensed there.
        var session = new StressSession(p with { Setting = p.Setting.Shifted(x[head], y[head], z[head]) }, seed,
            HumanoidRagdoll.RegionCount, Poses, head);

        var strengths = new float[HumanoidRagdoll.MuscleCount];
        var targets = new Quat[HumanoidRagdoll.MuscleCount];
        var handL = new List<BodyPoint>();
        var handR = new List<BodyPoint>();
        var pelvis = new List<BodyPoint>();
        int pelvisLane = ragdoll.BodyLane(Limb.Pelvis);
        while (!session.Finished)
        {
            ragdoll.ReadRegions(x, y, z);
            session.Tick(x, y, z, strengths, targets);
            ragdoll.Drive(strengths, targets);
            world.Step((float)p.TickSeconds, SeatedPuppet.Substeps);

            ragdoll.ReadRegions(x, y, z);
            handL.Add(new BodyPoint(x[(int)Region.HandL], y[(int)Region.HandL], z[(int)Region.HandL]));
            handR.Add(new BodyPoint(x[(int)Region.HandR], y[(int)Region.HandR], z[(int)Region.HandR]));
            var v = world.View;
            pelvis.Add(new BodyPoint(v.PositionX[pelvisLane], v.PositionY[pelvisLane], v.PositionZ[pelvisLane]));
        }
        return new OfficeBodyResult(session.Result(), handL, handR, pelvis);
    }

    /// <summary>
    /// Strikes in <paramref name="hand"/>'s steps from <paramref name="from"/> up to <paramref name="to"/>: forward (+z) speed
    /// excursions, each from passing <see cref="StrikeSpeed"/> to falling under <see cref="StrikeEnds"/> of it, so a strike
    /// that wobbles at its peak counts once.
    /// </summary>
    public static int CountStrikes(IReadOnlyList<BodyPoint> hand, int from, int to)
    {
        int strikes = 0;
        bool inStrike = false;
        for (int i = Math.Max(from, 1); i < Math.Min(to, hand.Count); i++)
        {
            float forward = (hand[i].Z - hand[i - 1].Z) * Hz;
            if (!inStrike && forward >= StrikeSpeed) { inStrike = true; strikes++; }
            else if (inStrike && forward < StrikeSpeed * StrikeEnds) inStrike = false;
        }
        return strikes;
    }

    private static OfficePoses BuildPoses()
    {
        Quat[] Pose() { var q = new Quat[HumanoidRagdoll.MuscleCount]; Array.Fill(q, Quat.Identity); return q; }

        var composed = Pose();
        var keys = new[] { Pose(), Pose(), Pose(), Pose() };
        // Key order: wind-up left, strike left, wind-up right, strike right. The arm not striking stays wound up.
        foreach (var k in keys) { Arm(k, left: true, strike: false); Arm(k, left: false, strike: false); }
        Arm(keys[1], left: true, strike: true);
        Arm(keys[3], left: false, strike: true);
        return new OfficePoses(composed, Pose(), keys);
    }

    private static void Arm(Quat[] pose, bool left, bool strike)
    {
        pose[(int)(left ? Muscle.ShoulderL : Muscle.ShoulderR)] = Quat.FromAxisAngle(1f, 0f, 0f, strike ? -1.5f : -0.5f);
        pose[(int)(left ? Muscle.ElbowL : Muscle.ElbowR)] = Quat.FromAxisAngle(0f, 0f, 1f, strike ? 0f : -2.2f);
    }
}
