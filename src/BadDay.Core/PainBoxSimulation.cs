using System;
using System.Collections.Generic;

namespace BadDay.Core;

/// <summary>
/// One virtual human with a hand in a pain box. Layers follow Lodur's target
/// (perception, reflex, drive, behaviour), flattened to one fixed-step loop:
/// the box emits a stimulus, a nociceptor with sensitisation turns it into an
/// afferent signal delayed by conduction, the delayed signal feeds pain and the
/// withdrawal reflex, will (resolve) suppresses the reflex at a strain cost,
/// and strain plus pain drain resolve. When resolve reaches zero the goal
/// switches from HoldHand to Withdraw and the hand leaves after the reflex
/// latency. There is no body, no physics and no balance here.
/// </summary>
public static class PainBoxSimulation
{
    public static PainBoxResult Run(PainBoxParameters p, ulong seed)
    {
        var rng = new SplitMix64(seed);
        // Per-seed traits, drawn first and in fixed order so the stream never shifts.
        double gainTrait = 1.0 + p.TraitJitter * rng.NextSigned();
        double drainTrait = 1.0 + p.TraitJitter * rng.NextSigned();

        long maxTick = Ticks(p.MaxSeconds, p.TickSeconds);
        long disableTick = p.BoxDisableAtSeconds is { } d ? Ticks(d, p.TickSeconds) : long.MaxValue;
        long sampleEvery = Math.Max(1, Ticks(p.SampleEverySeconds, p.TickSeconds));
        long refractory = Ticks(p.FlinchRefractorySeconds, p.TickSeconds);
        long withdrawLatency = Math.Max(1, Ticks(p.WithdrawalLatencySeconds, p.TickSeconds));
        int delayTicks = (int)Ticks(p.ConductionDelaySeconds, p.TickSeconds);

        var delay = new double[Math.Max(1, delayTicks)]; // afferent signal in flight along the nerve
        double dt = p.TickSeconds;
        double sens = 1.0, pain = 0.0, strain = 0.0, resolve = 1.0, stimulus = 0.0;
        var goal = Goal.HoldHand;
        long lastFlinch = long.MinValue / 2;
        long breakTick = -1, withdrawTick = -1;
        int flinches = 0;
        var timeline = new List<TimelineEntry>();
        var outcome = Outcome.Endured;
        long endTick = maxTick;

        void Log(long tick, EntryKind kind) => timeline.Add(new TimelineEntry(
            Ms(tick, p.TickSeconds), kind, stimulus, pain, strain, resolve, goal));

        for (long tick = 0; tick <= maxTick; tick++)
        {
            bool boxOn = tick < disableTick;
            if (tick == disableTick && goal == Goal.HoldHand)
            {
                stimulus = 0.0;
                outcome = Outcome.BoxDisabled;
                endTick = tick;
                Log(tick, EntryKind.BoxDisabled);
                break;
            }

            // Box -> stimulus (always draw jitter so the stream is independent of state).
            double jitter = rng.NextSigned();
            double ramp = Math.Min(1.0, tick * dt / p.BoxRampSeconds);
            stimulus = boxOn ? Math.Max(0.0, p.BoxIntensity * ramp * (1.0 + p.BoxNoise * jitter)) : 0.0;

            // Nociceptor: threshold, sensitisation, then conduction delay.
            double above = Math.Max(0.0, stimulus - p.NociceptorThreshold);
            sens += dt * (p.SensitisationGrowth * above - p.SensitisationRecovery * (sens - 1.0));
            sens = Math.Clamp(sens, 1.0, p.SensitisationCap);
            int slot = (int)(tick % delay.Length);
            double arrived = delay[slot]; // written delayTicks ticks ago
            delay[slot] = above * sens * gainTrait;

            // Pain: afferent drives it up toward 1, it decays on its own.
            pain += dt * (p.PainRise * arrived * (1.0 - pain) - p.PainDecay * pain);
            pain = Math.Clamp(pain, 0.0, 1.0);

            // Reflex: will holds down a fraction of it; the leak is a flinch,
            // the effort of holding is strain. Not free, per reflex-gain-from-drive.
            double suppression = goal == Goal.HoldHand ? p.MaxSuppression * resolve : 0.0;
            double leak = arrived * (1.0 - suppression);
            if (goal == Goal.HoldHand && leak > p.FlinchLeakThreshold && tick - lastFlinch >= refractory)
            {
                lastFlinch = tick;
                flinches++;
                Log(tick, EntryKind.Flinch);
            }
            strain += dt * (arrived * suppression - p.StrainRecover * strain);
            strain = Math.Max(0.0, strain);

            // Will decays under load, scaled down by how hard the objective pulls.
            if (goal == Goal.HoldHand)
            {
                double drain = (p.ResolveDrainPain * pain + p.ResolveDrainStrain * strain)
                               * (1.0 - p.ObjectivePull) * drainTrait;
                double recovery = pain < p.ResolveRecoversBelowPain ? p.ResolveRecovery * (1.0 - resolve) : 0.0;
                resolve = Math.Clamp(resolve + dt * (recovery - drain), 0.0, 1.0);
                if (resolve <= 0.0)
                {
                    goal = Goal.Withdraw;
                    breakTick = tick;
                    withdrawTick = tick + withdrawLatency;
                    Log(tick, EntryKind.WillBroke);
                }
            }

            if (tick % sampleEvery == 0) Log(tick, EntryKind.Sample);

            if (tick == withdrawTick)
            {
                outcome = Outcome.WillBroke;
                endTick = tick;
                Log(tick, EntryKind.Withdrew);
                break;
            }
            if (tick == maxTick) Log(tick, EntryKind.Endured);
        }

        return new PainBoxResult(
            seed, outcome, Ms(endTick, p.TickSeconds),
            breakTick >= 0 ? Ms(breakTick, p.TickSeconds) : null,
            flinches, timeline);
    }

    private static long Ticks(double seconds, double tickSeconds) => (long)Math.Round(seconds / tickSeconds);
    private static long Ms(long tick, double tickSeconds) => (long)Math.Round(tick * tickSeconds * 1000.0);
}
