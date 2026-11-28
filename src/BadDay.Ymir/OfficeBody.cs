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
    /// <summary>The tick the rage took over (the GoalChanged row whose goal is Rage); null if the will held.</summary>
    public int? RageTick => Run.Timeline.Any(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Rage)
        ? (int)Math.Round(Run.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Rage).TimeMs * OfficeBody.Hz / 1000.0)
        : null;

    /// <summary>Strikes by both hands from tick <paramref name="from"/> up to <paramref name="to"/>, see <see cref="OfficeBody.CountStrikes"/>.</summary>
    public int Strikes(int from, int to) => OfficeBody.CountStrikes(HandL, from, to) + OfficeBody.CountStrikes(HandR, from, to);
}

/// <summary>
/// Office Fella on the seated puppet: <see cref="Run"/> records an <see cref="OfficeBodySession"/>, the one stepper of the
/// office's world, from its start to its end, the release after calm takes over (or after a death).
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
    /// elbow folded, the fist at the chest) and strikes (the shoulder forward, the elbow straight), left then right. Clutch: both hands at the throat, the shoulders swung across and forward, the elbows folded.
    /// Thrash: four keys, a flung and kicking one, its mirror, and two between where both arms drop back and the knees come up.
    /// The clutch and the thrash are placeholders [P] Deru tunes on the capture.
    /// </summary>
    public static OfficePoses Poses { get; } = BuildPoses();

    /// <summary>Runs the office on the puppet for <paramref name="seconds"/> (the harness's horizon: the session has no end of its own); the tick rate must be 1/120 s.</summary>
    public static OfficeBodyResult Run(StressParameters p, ulong seed, double seconds)
    {
        using var session = new OfficeBodySession(p, seed);
        var handL = new List<BodyPoint>();
        var handR = new List<BodyPoint>();
        var pelvis = new List<BodyPoint>();
        long ticks = (long)Math.Round(seconds * Hz);
        while (session.TickIndex < ticks)
        {
            session.Step();
            handL.Add(session.HandL);
            handR.Add(session.HandR);
            pelvis.Add(session.Pelvis);
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
        var plan = SeatedPuppet.Plan;
        Quat[] Pose() { var q = new Quat[plan.Joints.Count]; Array.Fill(q, Quat.Identity); return q; }

        var composed = Pose();
        var keys = new[] { Pose(), Pose(), Pose(), Pose() };
        // Key order: wind-up left, strike left, wind-up right, strike right. The arm not striking stays wound up.
        foreach (var k in keys) { Arm(plan, k, left: true, strike: false); Arm(plan, k, left: false, strike: false); }
        Arm(plan, keys[1], left: true, strike: true);
        Arm(plan, keys[3], left: false, strike: true);
        return new OfficePoses(composed, Pose(), keys, Clutch(plan, Pose()), Thrash(plan, Pose));
    }

    // Both hands at the throat: the shoulder swings across (z) and forward (x), the elbow folds. Left is +sx, right -sx.
    private static Quat[] Clutch(BodyPlan plan, Quat[] pose)
    {
        foreach (var (side, sx) in new[] { (Side.Left, 1f), (Side.Right, -1f) })
        {
            pose[plan.JointIndex("Shoulder", side)] = Quat.FromAxisAngle(0f, 0f, 1f, -sx * 1.0f) * Quat.FromAxisAngle(1f, 0f, 0f, -0.8f);
            pose[plan.JointIndex("Elbow", side)] = Quat.FromAxisAngle(0f, 0f, 1f, -2.4f);
        }
        return pose;
    }

    // Keys 0 and 2 mirror each other (one arm flung up and out, the other forward, the trunk twisted, the head back, one leg kicking);
    // keys 1 and 3 throw both arms down and back, the head forward, the knees up.
    private static Quat[][] Thrash(BodyPlan plan, Func<Quat[]> fresh)
    {
        var k = new Quat[4][];
        for (int i = 0; i < 4; i++) k[i] = fresh();
        int Joint(string name, Side side) => plan.JointIndex(name, side);
        Quat Q(float x, float y, float z, float a) => Quat.FromAxisAngle(x, y, z, a);
        foreach (var (key, flip, up, fwd) in new[] { (0, 1f, Side.Left, Side.Right), (2, -1f, Side.Right, Side.Left) })
        {
            k[key][Joint("Shoulder", up)] = Q(0f, 0f, 1f, 1.3f * flip);
            k[key][Joint("Elbow", up)] = Q(1f, 0f, 0f, -0.4f);
            k[key][Joint("Shoulder", fwd)] = Q(1f, 0f, 0f, -1.2f);
            k[key][Joint("Elbow", fwd)] = Q(1f, 0f, 0f, -1.6f);
            k[key][Joint("Spine", Side.Center)] = Q(0f, 1f, 0f, 0.35f * flip);
            k[key][Joint("Neck", Side.Center)] = Q(1f, 0f, 0f, -0.5f);
            k[key][Joint("Hip", up)] = Q(1f, 0f, 0f, -0.5f);
            k[key][Joint("Knee", up)] = Q(1f, 0f, 0f, 0.2f);
        }
        foreach (var key in new[] { 1, 3 })
        {
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                k[key][Joint("Shoulder", side)] = Q(1f, 0f, 0f, 0.6f);
                k[key][Joint("Elbow", side)] = Q(1f, 0f, 0f, -0.3f);
                k[key][Joint("Knee", side)] = Q(1f, 0f, 0f, 0.6f);
            }
            k[key][Joint("Neck", Side.Center)] = Q(1f, 0f, 0f, 0.3f);
        }
        return k;
    }

    private static void Arm(BodyPlan plan, Quat[] pose, bool left, bool strike)
    {
        var side = left ? Side.Left : Side.Right;
        pose[plan.JointIndex("Shoulder", side)] = Quat.FromAxisAngle(1f, 0f, 0f, strike ? -1.5f : -0.5f);
        pose[plan.JointIndex("Elbow", side)] = Quat.FromAxisAngle(0f, 0f, 1f, strike ? 0f : -2.2f);
    }
}
