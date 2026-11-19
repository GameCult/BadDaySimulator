using System;
using System.Collections.Generic;
using BadDay.Core;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>Where the right hand was after a step, and whether it was inside the box.</summary>
public readonly record struct HandSample(float X, float Y, float Z, bool InBox);

/// <summary>
/// The body run's result: the same timeline and outcome as the headless run, plus what only a body has.
/// The outcome comes from the Actor's events (the run goes on to the deliberate withdrawal, then settles up to a second
/// for the hand to come out), so <see cref="Run"/>'s EndMs is the later of the Withdrew event and the hand-out time.
/// </summary>
/// <param name="HandOutMs">First step at or after the will broke with the hand point outside the box; null if it did not come out by the end of the settle. It can precede the Withdrew event.</param>
/// <param name="Excursions">Times the hand left the box between the end of the ramp and the break.</param>
/// <param name="Hand">The hand after every step; the index is the tick.</param>
public sealed record BodyPainBoxResult(
    PainBoxResult Run, long? HandOutMs, int Excursions, IReadOnlyList<HandSample> Hand);

/// <summary>
/// The pain box on the body: BDS creates the Ymir world (a ground, a static stool box welded to the pelvis
/// because the PD ragdoll cannot balance for 15 s, the ragdoll, the box volume around the right hand's hold
/// point) and steps it. Per step: the box writes its intensity, the body reports its hand points, Lodur's Actor
/// decides, the ragdoll is driven, the world steps. BDS reaches the puppet only through
/// <see cref="HumanoidRagdoll.Build"/>, <see cref="HumanoidRagdoll.ReadRegions"/> and <see cref="HumanoidRagdoll.Drive"/>.
/// </summary>
public static class BodyPainBox
{
    /// <summary>The ragdoll's gains are tuned at 120 Hz.</summary>
    public const double TickSeconds = 1.0 / 120.0;

    /// <summary>Centre of the box and the right hand's hold point.</summary>
    public static readonly (float X, float Y, float Z) HoldPoint = (-0.21f, 1.19f, 0.46f);

    private const float BoxHalf = 0.12f;

    /// <summary>Physics substeps per 1/120 s step. The ragdoll gains were tuned against 4; the behaviour tests, not this number, are the rule.</summary>
    private const int Substeps = 4;

    /// <summary>After the deliberate withdrawal begins the run waits this long for the hand to come out.</summary>
    private const double SettleSeconds = 1.0;

    public static BodyPainBoxResult Run(PainBoxParameters p, ulong seed)
    {
        if (Math.Abs(p.TickSeconds - TickSeconds) > 1e-12)
        {
            throw new ArgumentException($"The body run steps at 1/120 s; got TickSeconds {p.TickSeconds}.");
        }

        using var world = new Box3DSoaWorld(capacity: HumanoidRagdoll.LimbCount + 2, jointCapacity: HumanoidRagdoll.MuscleCount + 1);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        var ragdoll = HumanoidRagdoll.Build(world, (0f, 0f), firstStableId: 100);
        world.AddJoint(Box3DSoaJointKind.Weld, stool, ragdoll.BodyLane(Limb.Pelvis), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

        // Hold: the right arm raised and bent so the hand sits in the box. Withdraw: the arm pulled back and folded.
        var hold = new Quat[HumanoidRagdoll.MuscleCount];
        var withdraw = new Quat[HumanoidRagdoll.MuscleCount];
        Array.Fill(hold, Quat.Identity);
        Array.Fill(withdraw, Quat.Identity);
        hold[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, -1.0f);
        hold[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -0.6f);
        withdraw[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
        withdraw[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -2.0f);

        var volume = new BoxStimulus(HoldPoint.X, HoldPoint.Y, HoldPoint.Z, BoxHalf, BoxHalf, BoxHalf);
        var session = new PainBoxSession(p, seed, volume, HumanoidRagdoll.RegionCount, hold, withdraw, (int)Region.HandR);
        var x = new float[HumanoidRagdoll.RegionCount];
        var y = new float[HumanoidRagdoll.RegionCount];
        var z = new float[HumanoidRagdoll.RegionCount];
        var strengths = new float[HumanoidRagdoll.MuscleCount];
        var targets = new Quat[HumanoidRagdoll.MuscleCount];

        var samples = new List<HandSample>();
        long rampEnd = (long)Math.Round(p.BoxRampSeconds / p.TickSeconds);
        long? handOut = null;
        int excursions = 0;
        bool wasIn = false;
        long breakTick = -1, withdrewTick = -1;
        long settleTicks = (long)Math.Round(SettleSeconds / p.TickSeconds);

        HandSample ReadHand()
        {
            ragdoll.ReadRegions(x, y, z);
            float hx = x[(int)Region.HandR], hy = y[(int)Region.HandR], hz = z[(int)Region.HandR];
            return new HandSample(hx, hy, hz, session.Box.Contains(hx, hy, hz));
        }

        while (!session.Finished)
        {
            if (session.Disabled())
            {
                if (samples.Count == 0) samples.Add(ReadHand()); // disabled at tick 0: the track still holds the rest pose
                break;
            }
            long tick = session.TickIndex;
            ragdoll.ReadRegions(x, y, z);
            var events = session.Tick(x, y, z, strengths, targets);
            ragdoll.Drive(strengths, targets);
            world.Step((float)p.TickSeconds, Substeps);

            if ((events & TickEvents.WillBroke) != 0) breakTick = tick;
            var sample = ReadHand();
            var inBox = sample.InBox;
            samples.Add(sample);

            if (breakTick < 0)
            {
                if (tick > rampEnd && wasIn && !inBox) excursions++;
                wasIn = inBox;
            }
            else if (!inBox && handOut is null)
            {
                handOut = session.Ms(tick);
            }

            // The run ends on the Actor's events, never on the hand: after the deliberate withdrawal, once the
            // hand is out or the settle is over.
            if (session.Withdrew)
            {
                if (withdrewTick < 0) withdrewTick = tick;
                if (handOut is not null || tick - withdrewTick >= settleTicks) session.EndAt(tick);
            }
        }

        return new BodyPainBoxResult(session.Result(), handOut, excursions, samples);
    }
}
