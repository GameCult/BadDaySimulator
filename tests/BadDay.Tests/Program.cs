using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BadDay.Core;
using BadDay.Ymir;
using Quat = Lodur.Quat;

namespace BadDay.Tests;

public static class Program
{
    private static readonly PainBoxParameters Default = new();
    private static readonly PainBoxParameters OnBody = new() { TickSeconds = BodyPainBox.TickSeconds };
    /// <summary>OnBody with a will that costs no strength (WillCurve toward 0 makes Will^WillCurve 1): the arm's strength is the same whatever the will, so a flinch-gain-0 control feeds nothing different back into the drive. The flinch test's premise.</summary>
    private static readonly PainBoxParameters Steady = OnBody with { Drive = new Lodur.DriveParameters { WillCurve = 1e-9 } };
    private const int Hz = 120;

    private static readonly (string Name, Action Body)[] Tests =
    {
        ("same seed and parameters render byte-identical timelines", () =>
        {
            var a = PainBoxSimulation.Run(Default, 42);
            var b = PainBoxSimulation.Run(Default, 42);
            Check(a.Render() == b.Render(), "rendered timelines differ");
            Check(a.TimeToBreakMs == b.TimeToBreakMs && a.Flinches == b.Flinches, "results differ");
        }),

        ("the same seed gives the same result no matter what ran before it", () =>
        {
            var alone = PainBoxSimulation.Run(Default, 7).Render();
            for (ulong s = 100; s < 105; s++) PainBoxSimulation.Run(Default, s);
            Check(PainBoxSimulation.Run(Default, 7).Render() == alone, "state leaked between runs");
        }),

        ("different seeds give different characters: break times spread", () =>
        {
            var sweep = SweepSummary.Run(Default, 1, 30);
            var distinct = sweep.Results.Select(r => r.TimeToBreakMs).Distinct().Count();
            Check(distinct > 10, $"only {distinct} distinct break times in 30 seeds");
            Check(sweep.MinBreakMs < sweep.MaxBreakMs, "no spread");
        }),

        ("golden: default parameters, seed 1 breaks at 14730 ms and the hand is out 100 ms later", () =>
        {
            // Pins the placeholder model; a deliberate retune changes this number and this line together.
            var r = PainBoxSimulation.Run(Default, 1);
            Check(r.Outcome == Outcome.WillBroke, $"outcome {r.Outcome}");
            Check(r.TimeToBreakMs == 14730, $"break at {r.TimeToBreakMs}");
            Check(r.EndMs == 14830, $"end at {r.EndMs}");
        }),

        ("break time is the WillBroke entry and the hand leaves after the withdrawal latency", () =>
        {
            var r = PainBoxSimulation.Run(Default, 3);
            var broke = r.Timeline.Single(e => e.Kind == EntryKind.WillBroke);
            var out_ = r.Timeline.Single(e => e.Kind == EntryKind.Withdrew);
            Check(broke.TimeMs == r.TimeToBreakMs, "TimeToBreakMs disagrees with the timeline");
            // The goal is the Actor's: the withdraw goal takes over the latency after the break, not on it.
            Check(broke.Resolve == 0.0 && broke.Goal == Goal.HoldHand, "break entry should show resolve 0, goal still HoldHand");
            Check(out_.TimeMs - broke.TimeMs == 100 && out_.Goal == Goal.Withdraw, $"latency {out_.TimeMs - broke.TimeMs} ms, goal {out_.Goal}");
            Check(r.Timeline.Where(e => e.TimeMs < broke.TimeMs).All(e => e.Resolve > 0.0 && e.Goal == Goal.HoldHand),
                "resolve hit zero or goal changed before the recorded break");
        }),

        ("a stronger box breaks the will sooner", () =>
        {
            long Break(double intensity) =>
                PainBoxSimulation.Run(Default with { BoxIntensity = intensity }, 5).TimeToBreakMs ?? long.MaxValue;
            Check(Break(1.0) < Break(0.8) && Break(0.8) < Break(0.6), "break times not ordered by intensity");
        }),

        ("the volunteer's payment sets the pull: 0 pays 0, the default 100 pulls exactly half, 400 pulls 0.8, and the hold goal carries it", () =>
        {
            double Pull(double pay) => ChamberBrain.Pull(Default with { Payment = pay });
            Check(Pull(0) == 0.0 && Pull(100) == 0.5 && Pull(400) == 0.8, $"pulls {Pull(0)}, {Pull(100)}, {Pull(400)}");
            Check(ChamberBrain.Pull(Default) == 0.5, "the default payment does not pull half");
            Check(ChamberBrain.Pull(Default with { Payment = 50, DollarsAtHalfPull = 50 }) == 0.5, "the pull is not the payment over the payment plus DollarsAtHalfPull");
            var brain = ChamberBrain.Build(Default with { Payment = 400 }, new[] { Quat.Identity }, new[] { Quat.Identity });
            Check(brain.Goals[ChamberBrain.HoldIndex].Pull == 0.8 && brain.Goals[ChamberBrain.WithdrawIndex].Pull == 0.0, "the Brain's pulls are not the payment's");
            foreach (var bad in new[] { Default with { Payment = -1 }, Default with { Payment = double.NaN }, Default with { Payment = double.PositiveInfinity }, Default with { DollarsAtHalfPull = 0 }, Default with { DollarsAtHalfPull = -5 } })
            {
                bool threw = false;
                try { PainBoxSimulation.Run(bad, 1); } catch (ArgumentException) { threw = true; }
                Check(threw, $"payment {bad.Payment}, half-pull {bad.DollarsAtHalfPull} was accepted");
            }
        }),

        ("a better paid volunteer holds the will longer: the break time rises strictly over payments 0, 20, 100, 400 (seed 5)", () =>
        {
            long Break(double pay) => PainBoxSimulation.Run(Default with { Payment = pay }, 5).TimeToBreakMs ?? long.MaxValue;
            Check(Break(0) < Break(20) && Break(20) < Break(100) && Break(100) < Break(400), "break times not ordered by payment");
        }),

        ("disabling the box before the break ends the run with the will intact", () =>
        {
            var r = PainBoxSimulation.Run(Default with { BoxDisableAtSeconds = 5.0 }, 1);
            Check(r.Outcome == Outcome.BoxDisabled, $"outcome {r.Outcome}");
            Check(r.TimeToBreakMs is null && r.EndMs == 5000, $"break {r.TimeToBreakMs}, end {r.EndMs}");
            Check(r.Timeline.Last().Resolve > 0.0, "resolve should be above zero");
        }),

        ("disabling the box after the break changes nothing", () =>
        {
            var plain = PainBoxSimulation.Run(Default, 1);
            var late = PainBoxSimulation.Run(Default with { BoxDisableAtSeconds = 60.0 }, 1);
            Check(plain.Render() == late.Render(), "a box disabled after the break altered the run");
            // Inside the withdrawal latency the goal is still HoldHand, but the will is broken and the box going off does not give it back.
            var inLatency = PainBoxSimulation.Run(Default with { BoxDisableAtSeconds = 14.78 }, 1);
            Check(inLatency.Outcome == Outcome.WillBroke && inLatency.TimeToBreakMs == plain.TimeToBreakMs && inLatency.EndMs == plain.EndMs, $"a box switched off inside the latency gave {inLatency.Outcome} ending at {inLatency.EndMs}");
        }),

        ("no stimulus: nothing flinches and the run endures", () =>
        {
            var r = PainBoxSimulation.Run(Default with { BoxIntensity = 0.0, MaxSeconds = 10.0 }, 1);
            Check(r.Outcome == Outcome.Endured && r.Flinches == 0 && r.TimeToBreakMs is null, r.Summary());
            Check(r.Timeline.All(e => e.Pain == 0.0 && e.Strain == 0.0 && e.Resolve == 1.0), "state moved without a stimulus");
        }),

        ("holding a reflex down costs strain before resolve gives out", () =>
        {
            var r = PainBoxSimulation.Run(Default, 1);
            Check(r.Timeline.Any(e => e.Goal == Goal.HoldHand && e.Strain > 0.5), "no visible strain while holding");
            Check(r.Flinches > 0, "no reflex leaked through before the break");
        }),

        ("sweep summary adds up and orders min <= median <= max", () =>
        {
            var s = SweepSummary.Run(Default, 1, 25);
            Check(s.Broke + s.BoxDisabled + s.Endured == s.Runs, "outcome counts do not sum");
            Check(s.MinBreakMs <= s.MedianBreakMs && s.MedianBreakMs <= s.MaxBreakMs, "order");
            Check(s.Results[0].Seed == 1 && s.Results[24].Seed == 25, "seeds not consecutive");
        }),

        // The body: the same pain box on the Ymir ragdoll, through BodyPainBox. Hands are read from body lanes.
        ("body: the same seed twice in one process gives the same timeline and hand-out time", () =>
        {
            var a = BodyPainBox.Run(OnBody, 1);
            var b = BodyPainBox.Run(OnBody, 1);
            Check(a.Run.Render() == b.Run.Render() && a.Run.HandOutMs == b.Run.HandOutMs && a.Excursions == b.Excursions, "body runs differ");
            Check(a.Hand.Count == b.Hand.Count && a.Hand[^1] == b.Hand[^1], "hand tracks differ");
        }),

        ("body: the hand stays in the box until the will breaks, a flinch moves it, the broken will pulls it out within 300 ms", () =>
        {
            var r = BodyPainBox.Run(OnBody, 1);
            Check(r.Run.Outcome == Outcome.WillBroke && r.Run.TimeToBreakMs is not null, $"outcome {r.Run.Outcome}");
            long broke = r.Run.TimeToBreakMs!.Value;
            Check(r.Excursions == 0, $"{r.Excursions} excursions before the break");
            int breakTick = Tick(broke);
            for (int i = 2 * Hz; i < breakTick; i++) Check(r.Hand[i].InBox, $"hand left the box at tick {i}, will broke at {breakTick}");

            var flinches = r.Run.Timeline.Where(e => e.Kind == EntryKind.Flinch).Select(e => Tick(e.TimeMs)).ToList();
            Check(flinches.Count > 0, "no flinch");
            bool moved = flinches.Any(f => r.Hand.Skip(f).Take(Hz / 5 + 1).Any(h => Dist(h) > 0.023f));
            Check(moved, "no flinch moved the hand over 2.3 cm from the hold point");

            Check(r.Run.HandOutMs is { } o && o - broke <= 300, $"hand out at {r.Run.HandOutMs}, will broke at {broke}");
            Check(r.Run.EndMs >= r.Run.HandOutMs, "the run ended before the hand was out");
        }),

        ("body: switching the box off at 5 s ends the run with the will intact and the hand still in the box", () =>
        {
            var r = BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = 5.0 }, 1);
            Check(r.Run.Outcome == Outcome.BoxDisabled && r.Run.TimeToBreakMs is null, $"outcome {r.Run.Outcome}");
            Check(r.Run.HandOutMs is null && r.Hand[^1].InBox, "the hand left the box");
            Check(r.Run.Timeline.Last().Resolve > 0.0, "resolve should be above zero");
        }),

        ("body: holding costs strain and strain shows: hand speed before the break exceeds hand speed after the ramp", () =>
        {
            var r = BodyPainBox.Run(OnBody, 1);
            int breakTick = Tick(r.Run.TimeToBreakMs!.Value);
            double early = SpeedRms(r, 2 * Hz, 4 * Hz), late = SpeedRms(r, breakTick - 2 * Hz, breakTick);
            Check(late > early, $"speed RMS {late:F4} m/s in the 2 s before the break, {early:F4} m/s in the 2 s after the ramp");
        }),

        ("body: a box at intensity 0 for 10 s: no flinch, no break, the hand stays near its hold point", () =>
        {
            var r = BodyPainBox.Run(OnBody with { BoxIntensity = 0.0, MaxSeconds = 10.0 }, 1);
            Check(r.Run.Outcome == Outcome.Endured && r.Run.Flinches == 0 && r.Run.TimeToBreakMs is null, r.Run.Summary());
            // Measured idle drift from the hold point (seed-independent without pain), substeps 1 to 8: the arm settles
            // slowly and sags about 1 cm under gravity, so from 3 s the largest drift is 2.0 to 2.3 cm (2.31 at 8 substeps),
            // from 8 s 1.1 to 1.5 cm. A flinch moves the hand over 2.3 cm from where a leak-free arm is; the limits sit
            // 50% and 35% above the largest measurement.
            for (int i = 3 * Hz; i < r.Hand.Count; i++) Check(Dist(r.Hand[i]) <= 0.035f, $"drift {Dist(r.Hand[i]):F3} m at tick {i}");
            for (int i = 8 * Hz; i < r.Hand.Count; i++) Check(Dist(r.Hand[i]) <= 0.020f, $"late drift {Dist(r.Hand[i]):F3} m at tick {i}");
        }),

        ("body: the stimulus and the flinches are the right hand's: the timeline's stimulus column is live while the hand is in the box", () =>
        {
            var r = BodyPainBox.Run(OnBody, 1);
            var at3 = r.Run.Timeline.Single(e => e.Kind == EntryKind.Sample && e.TimeMs == 3000);
            Check(at3.Stimulus > 0.3 && at3.Pain > 0.3, $"stimulus {at3.Stimulus}, pain {at3.Pain} at 3 s");
            Check(r.Hand[3 * Hz].InBox && r.Run.Flinches > 0, "right hand not in the box, or no flinch");
        }),

        ("body: a heavy hold (flinch gain 1, shake 0.3) still ends by the Actor's events: WillBroke, one Withdrew row, hand out reported separately", () =>
        {
            var heavy = OnBody with { Hold = new Lodur.HoldParameters { FlinchGain = 1.0, StrainShake = 0.3 } };
            for (ulong seed = 1; seed <= 6; seed++)
            {
                var r = BodyPainBox.Run(heavy, seed);
                Check(r.Run.Outcome == Outcome.WillBroke, $"seed {seed}: outcome {r.Run.Outcome}, break {r.Run.TimeToBreakMs}, hand out {r.Run.HandOutMs}");
                var withdrew = r.Run.Timeline.Single(e => e.Kind == EntryKind.Withdrew);
                Check(withdrew.TimeMs - r.Run.TimeToBreakMs == 100, $"seed {seed}: Withdrew row {withdrew.TimeMs - r.Run.TimeToBreakMs} ms after the break");
                Check(r.Run.EndMs >= withdrew.TimeMs && (r.Run.HandOutMs is null || r.Run.EndMs >= r.Run.HandOutMs), $"seed {seed}: ended before the withdrawal or the hand-out");
            }
        }),

        ("body: the flinch moves the hand: against a flinch-gain-0 control on the same seed the tracks part by over 2.3 cm within 200 ms of a flinch, and not before the first flinch", () =>
        {
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var full = BodyPainBox.Run(Steady, seed);
                var control = BodyPainBox.Run(Steady with { Hold = new Lodur.HoldParameters { FlinchGain = 0.0 } }, seed);
                Check(full.Run.Flinches == control.Run.Flinches, $"seed {seed}: the nervous state changed with the arm");
                var flinches = full.Run.Timeline.Where(e => e.Kind == EntryKind.Flinch).Select(e => Tick(e.TimeMs)).ToList();
                float Gap(int i) => MathF.Sqrt(MathF.Pow(full.Hand[i].X - control.Hand[i].X, 2) + MathF.Pow(full.Hand[i].Y - control.Hand[i].Y, 2) + MathF.Pow(full.Hand[i].Z - control.Hand[i].Z, 2));
                float after = flinches.Max(f => Enumerable.Range(f, Hz / 5 + 1).Max(Gap));
                float before = Enumerable.Range(0, flinches[0]).Max(Gap);
                Check(after > 0.023f, $"seed {seed}: the largest gap from the control after a flinch is {after:F4} m");
                Check(before < after / 2, $"seed {seed}: the gap before the first flinch is {before:F4} m, after {after:F4} m");
            }
        }),

        ("chamber: the withdrawal is one-way: ticked 90 s past it with the box off and the will mended, the goal stays Withdraw, and a box going off does not end the run as if the will held", () =>
        {
            var pose = new[] { Lodur.Quat.Identity };
            var session = new PainBoxSession(Default, 1, new Lodur.BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f), 1, pose, pose);
            var point = new float[1];
            var strengths = new float[1];
            var targets = new Quat[1];
            while (!session.Withdrew) session.Tick(point, point, point, strengths, targets);
            session.SwitchOff();
            bool mended = false;
            for (int i = 0; i < 9000; i++)
            {
                session.Tick(point, point, point, strengths, targets);
                mended |= !session.Actor.Drive.WillBroken;
                Check(session.Actor.GoalIndex == ChamberBrain.WithdrawIndex, $"the goal left Withdraw {i} ticks after the withdrawal");
            }
            Check(mended, "the will never mended: the test no longer exercises the rule");
            Check(!session.Actor.Drive.WillBroken && session.Actor.Drive.Breaks > 0, "the will should be mended and the break still counted");
            Check(!session.Disabled(), "a box going off ended the run of a will that had broken and mended");
        }),

        ("chamber release: the withdrawal is the Brain's release goal by id, not any goal change; an id the Brain lacks is refused", () =>
        {
            var r = PainBoxSimulation.Run(Default with { ReleaseGoalId = "hold-hand", MaxSeconds = 30.0 }, 1);
            Check(r.Outcome == Outcome.Endured && r.Timeline.All(e => e.Kind != EntryKind.Withdrew) && r.TimeToBreakMs == 14730,
                $"releasing on a goal the Brain never switches to: outcome {r.Outcome}, break {r.TimeToBreakMs}");
            bool threw = false;
            try { PainBoxSimulation.Run(Default with { ReleaseGoalId = "nowhere" }, 1); } catch (ArgumentException) { threw = true; }
            Check(threw, "a release goal the Brain lacks was accepted");
        }),

        ("body: hand-out is the start of a sustained exit: under five non-default holds over 30 seeds a reported hand-out is never followed by re-entry and starts at the first step out", () =>
        {
            var sets = new (string Name, PainBoxParameters P)[]
            {
                ("heavy", OnBody with { Hold = new Lodur.HoldParameters { FlinchGain = 1.0, StrainShake = 0.3 } }),
                ("flinch 1", OnBody with { Hold = new Lodur.HoldParameters { FlinchGain = 1.0 } }),
                ("shake 0.3", OnBody with { Hold = new Lodur.HoldParameters { StrainShake = 0.3 } }),
                ("flinch 2 shake 0.5", OnBody with { Hold = new Lodur.HoldParameters { FlinchGain = 2.0, StrainShake = 0.5 } }),
                ("intensity 1 heavy", OnBody with { BoxIntensity = 1.0, Hold = new Lodur.HoldParameters { FlinchGain = 1.0, StrainShake = 0.3 } }),
            };
            int broke = 0, exited = 0;
            foreach (var (name, p0) in sets)
            {
                var p = p0 with { MaxSeconds = 30.0 }; // a hand that flinches out of the box early stops the pain, and the will may never break
                for (ulong seed = 1; seed <= 30; seed++)
                {
                    var r = BodyPainBox.Run(p, seed);
                    string who = $"{name} seed {seed}";
                    if (r.Run.Outcome != Outcome.WillBroke) continue;
                    broke++;
                    string summary = r.Run.Summary();
                    Check(r.Run.HandOutMs is null == summary.Contains("hand not out"), $"{who}: summary '{summary}' disagrees with hand-out {r.Run.HandOutMs}");
                    if (r.Run.HandOutMs is not { } ms) continue;
                    exited++;
                    int outTick = Tick(ms), breakTick = Tick(r.Run.TimeToBreakMs!.Value);
                    for (int i = outTick; i < r.Hand.Count; i++) Check(!r.Hand[i].InBox, $"{who}: hand out at tick {outTick} but back in the box at tick {i}");
                    Check(outTick >= breakTick && (outTick == breakTick || r.Hand[outTick - 1].InBox), $"{who}: hand-out at tick {outTick} is not the first step out of the exit (break {breakTick})");
                    Check(r.Hand.Count - outTick >= (int)Math.Round(BodyPainBox.OutHoldSeconds * Hz), $"{who}: the exit was held {r.Hand.Count - outTick} ticks");
                }
            }
            Check(broke >= 75 && exited >= 50, $"only {broke} of 150 runs broke the will and {exited} got the hand out; the test no longer exercises the rule");
        }),

        ("body: a box disabled at tick 0 ends at once with the will intact and a hand track to read", () =>
        {
            var r = BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = 0.0 }, 1);
            Check(r.Run.Outcome == Outcome.BoxDisabled && r.Hand.Count == 1 && !r.Hand[0].InBox, $"{r.Run.Outcome}, {r.Hand.Count} samples");
        }),

        ("body: a live SwitchOff at tick T equals BoxDisableAtSeconds = T/120: before the break the will holds, after it the box goes to 0 and the run still ends WillBroke", () =>
        {
            var full = BodyPainBox.Run(OnBody, 1);
            int breakTick = Tick(full.Run.TimeToBreakMs!.Value);
            foreach (int t in new[] { 5 * Hz, breakTick + 2 })
            {
                var scripted = BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = t / (double)Hz }, 1);
                var live = RunLive(OnBody, 1, s => { if (s.TickIndex == t) s.SwitchOff(); });
                SameRun(scripted, live, $"switch-off at tick {t}");
            }
            Check(BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = 5.0 }, 1).Run.Outcome == Outcome.BoxDisabled, "a switch-off with the will holding should end BoxDisabled");
            Check(BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = (breakTick + 2) / (double)Hz }, 1).Run.Outcome == Outcome.WillBroke, "a switch-off after the break should leave the run to end WillBroke");
        }),

        ("body: SetIntensity(x) before the first Step equals BoxIntensity = x", () =>
        {
            var set = RunLive(OnBody, 1, s => { if (s.TickIndex == 0) s.SetIntensity(0.5); });
            SameRun(BodyPainBox.Run(OnBody with { BoxIntensity = 0.5 }, 1), set, "intensity 0.5");
        }),

        ("body: SetIntensity mid-run takes effect on the next tick only", () =>
        {
            const int at = 4 * Hz; // well after the 2 s ramp
            using var control = new BodyPainBoxSession(OnBody, 1);
            using var live = new BodyPainBoxSession(OnBody, 1);
            for (int i = 0; i < at; i++) { control.Step(); live.Step(); }
            double scheduled = control.BoxIntensity;
            Check(scheduled > 0.3 && live.BoxIntensity == scheduled, $"before the command the tick's intensity is the schedule's: {live.BoxIntensity} against {scheduled}");
            live.SetIntensity(0.0);
            Check(live.BoxIntensity == scheduled, "the command rewrote a tick that had already run");
            control.Step(); live.Step();
            Check(control.BoxIntensity > 0.3 && live.BoxIntensity == 0.0, $"next tick: control {control.BoxIntensity}, live {live.BoxIntensity}");
        }),

        ("body: the session owns whether the box is on: on until switched off or its disable time, off at once for the reader after a SwitchOff even when the run is over, and a run ended by the will breaking still takes the switch", () =>
        {
            using var live = new BodyPainBoxSession(OnBody, 1);
            for (int i = 0; i < 4 * Hz; i++) live.Step();
            Check(live.BoxOn && live.BoxIntensity > 0.3, $"on {live.BoxOn}, intensity {live.BoxIntensity}");
            live.SwitchOff();
            Check(!live.BoxOn && live.BoxIntensity == 0.0, "a switched-off box still reads on");
            live.Step();
            Check(live.Finished && live.Result().Outcome == Outcome.BoxDisabled && !live.BoxOn && live.BoxIntensity == 0.0, "a box that ended the run is not off");

            using var scheduled = new BodyPainBoxSession(OnBody with { BoxDisableAtSeconds = 5.0 }, 1);
            for (int i = 0; i < 5 * Hz - 1; i++) scheduled.Step();
            Check(scheduled.BoxOn, "the box was off before its disable tick");
            scheduled.Step();
            Check(!scheduled.BoxOn, "the box was still on once the next tick is its disable tick");

            using var broke = new BodyPainBoxSession(OnBody, 1);
            while (!broke.Finished) broke.Step();
            string render = broke.Result().Render();
            Check(broke.Result().Outcome == Outcome.WillBroke && broke.BoxOn && broke.BoxIntensity > 0.3, "a run ended by the will breaking should leave the box on");
            broke.SwitchOff();
            Check(!broke.BoxOn && broke.BoxIntensity == 0.0 && broke.Result().Render() == render && broke.Result().Outcome == Outcome.WillBroke, "switching off after the run changed the box, or the result");
        }),

        ("body: stepping past Finished keeps the world moving, the arm driven and the result frozen", () =>
        {
            using var s = new BodyPainBoxSession(OnBody, 1);
            while (!s.Finished) s.Step();
            var drained = new BodyEvent[64];
            s.DrainEvents(drained);
            string render = s.Result().Render();
            long? handOut = s.HandOutMs;
            int excursions = s.Excursions;
            var atEnd = s.Hand;
            bool moved = false;
            for (int i = 0; i < Hz; i++)
            {
                s.Step();
                moved |= s.Hand != atEnd;
            }
            Check(moved, "the world did not step after Finished");
            // Once the arm has settled (a second), the driven arm moves 0.01 to 0.03 m in the next second; with the re-drive removed it goes limp and moves 0.3 m.
            var settled = s.Hand;
            float drift = 0f;
            for (int i = 0; i < Hz; i++)
            {
                s.Step();
                drift = MathF.Max(drift, MathF.Sqrt(MathF.Pow(s.Hand.X - settled.X, 2) + MathF.Pow(s.Hand.Y - settled.Y, 2) + MathF.Pow(s.Hand.Z - settled.Z, 2)));
            }
            Check(drift < 0.08f, $"the hand moved {drift:F3} m in the second after settling: the arm is not being driven");
            Check(s.Result().Render() == render && s.HandOutMs == handOut && s.Excursions == excursions && s.DrainEvents(drained) == 0,
                "the result, hand-out, excursions or events changed after Finished");
        }),

        ("body: the run ends on the confirmed hand-out, not on the settle timeout", () =>
        {
            var r = BodyPainBox.Run(OnBody, 1);
            int outTick = Tick(r.Run.HandOutMs!.Value), holdTicks = (int)Math.Round(BodyPainBox.OutHoldSeconds * Hz);
            Check(r.Hand.Count - 1 == outTick + holdTicks - 1, $"the last step is tick {r.Hand.Count - 1}; the hand-out at tick {outTick} is confirmed at tick {outTick + holdTicks - 1}");
        }),

        ("body: before the first Step the hand reads the rest pose, outside the box", () =>
        {
            using var s = new BodyPainBoxSession(OnBody, 1);
            Check(s.TickIndex == 0 && !s.Hand.InBox, "the fresh session's hand is not the rest pose");
            Check(s.Hand == BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = 0.0 }, 1).Hand[0], "the rest pose differs from the track of a box disabled at tick 0");
        }),

        ("body: events queue per tick, so draining every 1, 2, 3 or 7 steps (or through a two-slot buffer) gives the Run's flinch, break and withdrawal rows in order", () =>
        {
            var run = BodyPainBox.Run(OnBody, 1).Run;
            var rows = run.Timeline.Where(e => e.Kind is EntryKind.Flinch or EntryKind.WillBroke or EntryKind.Withdrew).Select(e => (e.TimeMs, e.Kind)).ToList();
            Check(rows.Count >= 10, $"only {rows.Count} event rows in the Run");
            List<BodyEvent> Collect(int every, int slots)
            {
                using var s = new BodyPainBoxSession(OnBody, 1);
                var got = new List<BodyEvent>();
                var buffer = new BodyEvent[slots];
                void Drain() { int n; while ((n = s.DrainEvents(buffer)) > 0) got.AddRange(buffer.Take(n)); }
                for (int i = 1; !s.Finished; i++)
                {
                    s.Step();
                    if (i % every == 0) Drain();
                }
                Drain();
                return got;
            }
            foreach (var (every, slots) in new[] { (1, 8), (2, 8), (3, 8), (7, 8), (7, 2) })
            {
                var got = Collect(every, slots);
                var expanded = new List<(long, EntryKind)>();
                foreach (var e in got)
                {
                    long ms = (long)Math.Round(e.Tick * 1000.0 / Hz);
                    if ((e.Events & Lodur.TickEvents.Flinch) != 0) expanded.Add((ms, EntryKind.Flinch));
                    if ((e.Events & Lodur.TickEvents.WillBroke) != 0) expanded.Add((ms, EntryKind.WillBroke));
                    if ((e.Events & Lodur.TickEvents.GoalChanged) != 0) expanded.Add((ms, EntryKind.Withdrew));
                }
                Check(expanded.SequenceEqual(rows), $"draining every {every} steps through {slots} slots: {expanded.Count} events against the Run's {rows.Count}, or out of order");
            }
        }),

        ("SetIntensity refuses a level that is non-finite, negative or above PainBox.MaxLevel, in the box and in the body session, and a refused call changes nothing", () =>
        {
            double[] bad = { double.PositiveInfinity, double.NegativeInfinity, double.NaN, -0.1, double.MaxValue, PainBox.MaxLevel * 1.000001 };
            var box = new PainBox(Default, new SplitMix64(1));
            foreach (double level in bad)
            {
                bool threw = false;
                try { box.Level = level; } catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw && box.Level == Default.BoxIntensity, $"PainBox.Level = {level}: threw {threw}, level now {box.Level}");
                using var s = new BodyPainBoxSession(OnBody, 1);
                threw = false;
                try { s.SetIntensity(level); } catch (ArgumentOutOfRangeException) { threw = true; }
                Check(threw, $"SetIntensity({level}) was accepted");
            }
            box.Level = 0.0;
            box.Level = PainBox.MaxLevel;
            bool built = false;
            try { PainBoxSimulation.Run(Default with { BoxIntensity = double.MaxValue }, 1); built = true; } catch (ArgumentOutOfRangeException) { }
            Check(!built, "a session was built with BoxIntensity = double.MaxValue");
            var refused = RunLive(OnBody, 1, s =>
            {
                if (s.TickIndex != 0) return;
                foreach (double level in bad)
                {
                    try { s.SetIntensity(level); } catch (ArgumentOutOfRangeException) { }
                }
            });
            SameRun(BodyPainBox.Run(OnBody, 1), refused, "refused intensities");
        }),

        ("body: draining through a buffer smaller than the queue returns a full buffer and keeps the rest, in order", () =>
        {
            using var s = new BodyPainBoxSession(OnBody, 1);
            while (!s.Finished) s.Step();
            var all = new BodyEvent[64];
            int total;
            using (var t = new BodyPainBoxSession(OnBody, 1))
            {
                while (!t.Finished) t.Step();
                total = t.DrainEvents(all);
            }
            Check(total >= 10, $"only {total} events pending");
            var small = new BodyEvent[3];
            var got = new List<BodyEvent>();
            int n = s.DrainEvents(small);
            Check(n == 3, $"the first drain of {total} pending events through 3 slots wrote {n}");
            got.AddRange(small.Take(n));
            while ((n = s.DrainEvents(small)) > 0)
            {
                Check(n == 3 || got.Count + n == total, $"a drain wrote {n} with {total - got.Count} pending");
                got.AddRange(small.Take(n));
            }
            Check(got.Count == total && got.SequenceEqual(all.Take(total)), $"drained {got.Count} of {total}, or out of order");
        }),

        ("body: after Dispose every member throws ObjectDisposedException and a second Dispose does nothing", () =>
        {
            var s = new BodyPainBoxSession(OnBody, 1);
            s.Step();
            s.Dispose();
            s.Dispose();
            var members = new (string Name, Action Call)[]
            {
                ("Step", () => s.Step()), ("Hand", () => _ = s.Hand), ("Result", () => s.Result()), ("SetIntensity", () => s.SetIntensity(0.5)),
                ("SwitchOff", () => s.SwitchOff()), ("DrainEvents", () => s.DrainEvents(new BodyEvent[4])), ("View", () => _ = s.View),
                ("Finished", () => _ = s.Finished), ("TickIndex", () => _ = s.TickIndex), ("Excursions", () => _ = s.Excursions),
                ("HandOutMs", () => _ = s.HandOutMs), ("BoxIntensity", () => _ = s.BoxIntensity), ("Pain", () => _ = s.Pain),
                ("Chamber", () => _ = s.Chamber), ("BoxOn", () => _ = s.BoxOn), ("Strain", () => _ = s.Strain), ("Resolve", () => _ = s.Resolve), ("Goal", () => _ = s.Goal),
            };
            foreach (var (name, call) in members)
            {
                bool threw = false;
                try { call(); } catch (ObjectDisposedException) { threw = true; }
                Check(threw, $"{name} did not throw after Dispose");
            }
        }),

        ("the seed's trait multiplies the caller's nerve gain and drain trait instead of replacing them", () =>
        {
            Check(PainBoxSimulation.Run(Default with { Nerve = new Lodur.NerveParameters { Gain = 0.0 }, MaxSeconds = 20.0 }, 1).Outcome == Outcome.Endured, "gain 0 should leave no pain");
            long Break(double gain) => PainBoxSimulation.Run(Default with { Nerve = new Lodur.NerveParameters { Gain = gain } }, 1).TimeToBreakMs ?? long.MaxValue;
            Check(Break(2.0) < PainBoxSimulation.Run(Default, 1).TimeToBreakMs, "a caller's nerve gain of 2 did not shorten the break");
            long Drain(double d) => PainBoxSimulation.Run(Default with { Drive = new Lodur.DriveParameters { DrainTrait = d } }, 1).TimeToBreakMs ?? long.MaxValue;
            Check(Drain(2.0) < Drain(1.0) && Drain(1.0) == 14730, "a caller's drain trait did not scale the drain");
        }),

        ("CLI run is deterministic and sweep reports a break-time line", () =>
        {
            string Run(params string[] a) { var w = new StringWriter(); BadDay.Cli.Program.Execute(a, w); return w.ToString(); }
            Check(Run("run", "--seed", "9") == Run("run", "--seed", "9"), "CLI output differs between runs");
            Check(Run("run", "--seed", "1").Contains("will broke at 14730 ms"), "CLI summary missing break time");
            Check(Run("sweep", "--seed", "1", "--count", "5").Contains("time to break min/median/max ms:"), "no sweep summary");
            bool threw = false;
            try { Run("run"); } catch (ArgumentException) { threw = true; }
            Check(threw, "missing --seed should be rejected");
            threw = false;
            try { Run("run", "--seed", "1", "--pull", "0.3"); } catch (ArgumentException) { threw = true; }
            Check(threw, "the chamber takes --pay, not --pull");
            Check(Run("run", "--seed", "5", "--pay", "20") != Run("run", "--seed", "5", "--pay", "400"), "--pay changed nothing");
        }),

        ("office: a stressor is silent before its onset, on for its duration, and back every period", () =>
        {
            var printer = Cause.Office.Stressors.Single(s => s.Id == "printer"); // 5 s on, 8 s period, 1 s long, 0.25
            double V(double seconds) => printer.Value((long)Math.Round(seconds * 100), 0.01, 0.0);
            Check(V(4.99) == 0.0 && V(5.0) == 0.25 && V(5.99) == 0.25 && V(6.0) == 0.0, "first burst is not [5 s, 6 s)");
            Check(V(12.99) == 0.0 && V(13.0) == 0.25 && V(13.99) == 0.25 && V(14.0) == 0.0, "second burst is not [13 s, 14 s)");
            var once = printer with { PeriodSeconds = null };
            Check(once.Value(1300, 0.01, 0.0) == 0.0 && once.Value(500, 0.01, 0.0) == 0.25, "a stressor with no period repeated");
            var ramped = printer with { RampSeconds = 0.5 };
            Check(ramped.Value(525, 0.01, 0.0) == 0.125 && ramped.Value(560, 0.01, 0.0) == 0.25, "the ramp does not rise over its seconds");
        }),

        ("office: the setting holds the emitters and no schedule, the cause refers to them by id", () =>
        {
            var ids = Setting.Office.Emitters.Select(e => e.Id).ToHashSet();
            Check(Cause.Office.Stressors.All(s => ids.Contains(s.EmitterId)), "a stressor names an emitter the setting lacks");
            Check(Setting.Office.Emitters.All(e => e.Emitter.Intensity == 0.0), "the setting carries an intensity");
            Check(Character.OfficeFella.Senses.Threshold == 0.05 && Character.OfficeFella.Senses.SensitisationGrowth == 0.0 && Character.OfficeFella.Senses.ConductionDelaySeconds == 0.0,
                "Office Fella does not carry his own sense parameters");
        }),

        ("office: the same seed gives the same bytes, and sessions stepped in turn equal each run alone", () =>
        {
            var p = new StressParameters();
            Check(Office(p, 1).Render() == Office(p, 1).Render(), "rendered timelines differ");
            var a = NewOffice(p, 1);
            var b = NewOffice(p, 2);
            while (!a.Finished || !b.Finished)
            {
                if (!a.Finished) Step(a);
                if (!b.Finished) Step(b);
            }
            Check(a.Result().Render() == Office(p, 1).Render() && b.Result().Render() == Office(p, 2).Render(), "stepped sessions differ from runs alone");
        }),

        ("office: an empty cause gives no load, resolve 1 on every row, and the run endures", () =>
        {
            var r = Office(new StressParameters { Cause = Cause.Office with { Stressors = Array.Empty<Stressor>() } }, 1);
            Check(r.Outcome == Outcome.Endured && r.TimeToBreakMs is null && r.EndMs == 180000, $"outcome {r.Outcome} at {r.EndMs}");
            Check(r.Timeline.All(e => e.Load == 0.0 && e.Arousal == 0.0 && e.Resolve == 1.0), "an empty office loaded the character");
            Check(r.Timeline[^1].Kind == StressEntryKind.Endured, "no Endured row");
        }),

        ("office tuning [P]: seeds 1..50, at least 80% break within 180 s, and p10 and p90 break times differ by more than 20%", () =>
        {
            var sweep = StressSweep.Run(new StressParameters(), 1, 50);
            Check(sweep.Broke >= 40, $"only {sweep.Broke} of 50 broke");
            var b = sweep.Results.Where(r => r.TimeToBreakMs.HasValue).Select(r => (double)r.TimeToBreakMs!.Value).OrderBy(x => x).ToArray();
            double p10 = b[(int)(b.Length * 0.1)], p90 = b[(int)(b.Length * 0.9)];
            Check((p90 - p10) / p10 > 0.2, $"p10 {p10} and p90 {p90} ms differ by under 20%");
        }),

        ("office: the break sits near 90 s at pull 0.6 with the office sense nerve, and Lodur's default nerve breaks later", () =>
        {
            long own = MedianBreak(new StressParameters(), 1, 20);
            long lodurDefault = MedianBreak(new StressParameters { Character = Character.OfficeFella with { Senses = new Lodur.NerveParameters() } }, 1, 20);
            Check(own is > 75000 and < 100000, $"median break {own} ms");
            Check(lodurDefault > own * 1.05, $"default nerve breaks at {lodurDefault} ms, office nerve at {own} ms");
        }),

        ("office: monotone in load: scale 1.5 breaks no later, and removing any stressor breaks no sooner", () =>
        {
            var p = new StressParameters();
            long baseline = MedianBreak(p, 1, 20);
            Check(MedianBreak(p with { Cause = p.Cause.Scaled(1.5) }, 1, 20) <= baseline, "scale 1.5 lengthened the median break");
            foreach (var s in p.Cause.Stressors)
            {
                long without = MedianBreak(p with { Cause = p.Cause.Without(s.Id) }, 1, 20);
                Check(without >= baseline, $"removing {s.Id} shortened the median break ({without} < {baseline})");
            }
        }),

        ("office: silencing is the player lever: the silenced emitter writes nothing from that tick, one Silenced row is logged, and the median break comes later", () =>
        {
            var p = new StressParameters();
            long baseline = MedianBreak(p, 1, 20);
            Check(MedianBreak(p, 1, 20, new SilenceCommand("printer", 20)) > baseline, "silencing the printer at 20 s did not delay the median break");

            var silenced = NewOffice(p, 1);
            var control = NewOffice(p, 1);
            while (silenced.TickIndex < 2000) { Step(silenced); Step(control); }
            silenced.Silence("printer");
            silenced.Silence("printer"); // again: no second row
            bool controlSounded = false;
            while (silenced.TickIndex < 4000)
            {
                Step(silenced); Step(control);
                controlSounded |= control.Emitters[0].Intensity > 0.0;
                Check(silenced.Emitters[0].Intensity == 0.0, $"the silenced printer wrote {silenced.Emitters[0].Intensity} at tick {silenced.TickIndex}");
                // The draws never depend on state: silencing the printer leaves every other stressor's jitter, hence its intensity, as it was.
                for (int e = 1; e < silenced.Emitters.Length; e++)
                    Check(silenced.Emitters[e].Intensity == control.Emitters[e].Intensity, $"silencing the printer changed emitter {e} at tick {silenced.TickIndex}");
            }
            Check(controlSounded, "the control printer never sounded, so the check proved nothing");
            var rows = silenced.Result().Timeline.Where(e => e.Kind == StressEntryKind.Silenced).ToArray();
            Check(rows.Length == 1 && rows[0].TimeMs == 20000, "expected exactly one Silenced row at 20000 ms");
            bool threw = false;
            try { silenced.Silence("no-such-stressor"); } catch (ArgumentException) { threw = true; }
            Check(threw, "an unknown stressor id was accepted");
        }),

        ("office: an emitter takes the largest of its unsilenced stressors, not their sum", () =>
        {
            var weak = new Stressor("weak", "printer", 0, 0, null, 10, 0.2, 0);
            var strong = new Stressor("strong", "printer", 0, 0, null, 10, 0.35, 0);
            var s = NewOffice(new StressParameters { Cause = Cause.Office with { Stressors = new[] { weak, strong } } }, 1);
            Step(s);
            Check(s.Emitters[0].Intensity == 0.35, $"two stressors gave {s.Emitters[0].Intensity}, not the larger 0.35");
            s.Silence("strong");
            Step(s);
            Check(s.Emitters[0].Intensity == 0.2, $"with the strong one silenced the emitter gave {s.Emitters[0].Intensity}, not 0.2");
        }),

        ("office: a stressor naming an emitter the setting lacks is refused before any tick", () =>
        {
            var p = new StressParameters { Cause = Cause.Office with { Stressors = new[] { new Stressor("x", "nowhere", 0, 0, null, 1, 0.5, 0) } } };
            bool threw = false;
            try { NewOffice(p, 1); } catch (ArgumentException) { threw = true; }
            Check(threw, "an unknown emitter id was accepted");
        }),

        ("office: after one 10 s burst resolve does not rise while arousal is at or above the threshold, then it rises", () =>
        {
            var burst = new Stressor("burst", "printer", 0, 0, null, 10, 0.5, 0);
            var p = new StressParameters { MaxSeconds = 120, Cause = Cause.Office with { Stressors = new[] { burst } } };
            var s = NewOffice(p, 1);
            double gate = p.Drive.ResolveRecoversBelowArousal, prev = 1.0, lowest = 1.0;
            int heldDown = 0, rose = 0;
            while (!s.Finished)
            {
                Step(s);
                var d = s.Actor.Drive;
                if (d.Arousal >= gate)
                {
                    Check(d.Resolve <= prev, $"resolve rose to {d.Resolve} at tick {s.TickIndex} with arousal {d.Arousal}");
                    if (d.Resolve < 1.0) heldDown++;
                }
                else if (d.Resolve > prev) rose++;
                lowest = Math.Min(lowest, d.Resolve);
                prev = d.Resolve;
            }
            Check(heldDown > 1000, "arousal never held the gate shut, so the check proved nothing");
            Check(rose > 1000 && prev > lowest + 0.001, "resolve never recovered once the room was quiet");
            Check(s.Result().Outcome == Outcome.Endured, "the burst broke the will");
        }),

        ("office: the stress column is 1 - resolve on every rendered row", () =>
        {
            var lines = Office(new StressParameters(), 3).Render().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Check(lines.Length > 10, "too few rows");
            foreach (var line in lines)
            {
                var f = line.Split(' ');
                double resolve = double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
                double stress = double.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture);
                Check(f.Length == 10 && Math.Abs(stress - (1.0 - resolve)) < 0.0015, $"row '{line}'");
            }
        }),

        ("pain box parity: sweep --seed 1 --count 20 hashes to the output before rung 4, and run --seed 1 does too once the break row's goal reads Withdraw as it then did", () =>
        {
            string Out(params string[] a)
            {
                var w = new StringWriter();
                BadDay.Cli.Program.Execute(a, w);
                return w.ToString().Replace("\r\n", "\n");
            }
            string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
            Check(Hash(Out("sweep", "--seed", "1", "--count", "20")) == "2cc88f9ffbbf301b8264c0be600edb34b21a3b572f927ed96ea49eda20dc73db", "sweep --seed 1 --count 20 changed");
            var run = Out("run", "--seed", "1").Split('\n');
            int row = Array.FindIndex(run, l => l.Contains(" WillBroke "));
            Check(row > 0 && run[row].EndsWith(" HoldHand"), "the break row should read HoldHand, the goal in force");
            run[row] = run[row][..^" HoldHand".Length] + " Withdraw";
            Check(Hash(string.Join('\n', run)) == "a3a800612f1e610deb8c648ea74a0a3e2f4639541a46d92af0d1bcfbdd8cfb45", "run --seed 1 differs from the pre-rung-4 output in more than the break row's goal");
        }),

        ("office CLI: office runs a timeline with the stress label, --silence repeats, office-sweep summarises", () =>
        {
            string Run(params string[] a) { var w = new StringWriter(); BadDay.Cli.Program.Execute(a, w); return w.ToString(); }
            var plain = Run("office", "--seed", "1");
            Check(plain == Run("office", "--seed", "1"), "office output differs between runs");
            Check(plain.Contains("lost composure") && plain.Contains("time_ms kind audio visual interrupt load arousal resolve stress goal"), "no label or header");
            Check(plain.Contains("will broke at"), "seed 1 should break at the default settings");
            var quiet = Run("office", "--seed", "1", "--silence", "printer@0", "--silence", "desk-phone@0");
            Check(quiet.Split('\n').Count(l => l.Contains(" Silenced ")) == 2, "two --silence options should give two Silenced rows");
            Check(Run("office", "--seed", "1", "--scale", "0").Contains("endured to 180000 ms"), "--scale 0 should be a quiet office");
            Check(Run("office", "--seed", "1", "--pull", "1").Contains("endured"), "--pull 1 never drains");
            Check(Run("office-sweep", "--seed", "1", "--count", "5").Contains("min resolve of endured runs:"), "no sweep summary");
            foreach (var bad in new[] { new[] { "office" }, new[] { "office", "--seed", "1", "--silence", "printer" }, new[] { "office", "--seed", "1", "--silence", "nope@1" }, new[] { "office-sweep", "--seed", "1", "--body" }, new[] { "office", "--seed", "1", "--body", "--silence", "printer@1" }, new[] { "office", "--seed", "1", "--pay", "5" } })
            {
                bool threw = false;
                try { Run(bad); } catch (ArgumentException) { threw = true; }
                Check(threw, $"'{string.Join(' ', bad)}' should be rejected");
            }
        }),

        ("office --body: a break whose rage --max clipped is reported as clipped, not as a will that held", () =>
        {
            var w = new StringWriter();
            BadDay.Cli.Program.Execute(new[] { "office", "--seed", "1", "--max", "83.4", "--body" }, w);
            var text = w.ToString();
            Check(text.Contains("will broke at 83358 ms") && text.Contains("--max") && !text.Contains("the will held"), $"clipped run reads: {text.Split((char)10, StringSplitOptions.RemoveEmptyEntries)[^1]}");
        }),

        ("office: a silence at S seconds lands on the tick S falls on: exactly, rounded to nearest, never early or late", () =>
        {
            long SilencedAt(double seconds) =>
                Office(new StressParameters(), 1, new SilenceCommand("printer", seconds)).Timeline.Single(e => e.Kind == StressEntryKind.Silenced).TimeMs;
            Check(SilencedAt(0.0) == 0, $"a silence at 0 s landed at {SilencedAt(0.0)} ms");
            Check(SilencedAt(20.0) == 20000, $"a silence at 20 s landed at {SilencedAt(20.0)} ms");
            Check(SilencedAt(20.004) == 20000, $"a silence at 20.004 s landed at {SilencedAt(20.004)} ms");
            Check(SilencedAt(20.006) == 20010, $"a silence at 20.006 s landed at {SilencedAt(20.006)} ms");
        }),

        ("office: an emitter takes the largest of its stressors whichever one is listed last", () =>
        {
            var weak = new Stressor("weak", "printer", 0, 0, null, 10, 0.2, 0);
            var strong = new Stressor("strong", "printer", 0, 0, null, 10, 0.35, 0);
            var s = NewOffice(new StressParameters { Cause = Cause.Office with { Stressors = new[] { strong, weak } } }, 1);
            Step(s);
            Check(s.Emitters[0].Intensity == 0.35, $"with the strong stressor first the emitter gave {s.Emitters[0].Intensity}, not 0.35");
            s.Silence("strong");
            Step(s);
            Check(s.Emitters[0].Intensity == 0.2, $"with the strong one silenced the emitter gave {s.Emitters[0].Intensity}, not 0.2");
        }),

        ("office: the seed's first draw is the senses' gain trait and its second the resolve-drain trait", () =>
        {
            var steady = Cause.Office with { Stressors = Cause.Office.Stressors.Select(s => s with { Jitter = 0.0 }).ToArray() };
            var p = new StressParameters { SampleEverySeconds = 0.1, Cause = steady };
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var rng = new SplitMix64(seed);
                double gain = 1.0 + p.TraitJitter * rng.NextSigned();
                double drain = 1.0 + p.TraitJitter * rng.NextSigned();
                var spelledOut = p with
                {
                    TraitJitter = 0.0,
                    Character = p.Character with { Senses = p.Character.Senses with { Gain = p.Character.Senses.Gain * gain } },
                    Drive = p.Drive with { DrainTrait = p.Drive.DrainTrait * drain },
                };
                Check(Office(p, seed).Render() == Office(spelledOut, seed).Render(), $"seed {seed}: the traits are not the first two draws, gain then drain");
            }
        }),

        ("office golden [P]: the rows up to the break are rung 3's bytes, the 50-seed sweep ends in its captured line, and the run's own bytes are captured (re-captured with the arc and again for the 8 s settle)", () =>
        {
            string Out(params string[] a)
            {
                var w = new StringWriter();
                BadDay.Cli.Program.Execute(a, w);
                return w.ToString().Replace("\r\n", "\n");
            }
            string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
            var lines = Out("office", "--seed", "1").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            int brk = lines.FindIndex(l => l.Contains(" WillBroke "));
            // Rung 3 ended at the break and named its goals HoldHand and Withdraw; what it printed up to there is what this prints, goal column aside.
            var rung3 = new List<string> { lines[0].Replace("rung 4", "rung 3"), lines[1] };
            for (int i = 2; i <= brk; i++) rung3.Add(lines[i].Replace(" KeepComposure", i == brk ? " Withdraw" : " HoldHand"));
            rung3.Add(lines[^1]);
            Check(Hash(string.Join('\n', rung3) + "\n") == "66098b69834c3c8b5e5b694f0059b8ed566e31d332f36be7e57baf70ae650823", "the office's rows up to the break differ from rung 3's");
            Check(Hash(string.Join('\n', lines) + "\n") == "14e226cbb85d68bb9104314b08802c3c20207f378456915a004b60f1ab657089", $"office --seed 1 changed: {Hash(string.Join('\n', lines) + "\n")}");
            var last = Out("office-sweep", "--seed", "1", "--count", "50").TrimEnd('\n').Split('\n')[^1];
            Check(last == "runs 50: broke 50, endured 0; time to break min/median/max ms: 72670/86570/113420; min resolve of endured runs: -", $"sweep line changed: {last}");
        }),

        ("office arc: the snap, then a rage on a refilled will, a second break 5-20 s later, and calm for good: every switch 100 ms after its break, nothing after calm, and the run ends ReleaseSeconds after calm", () =>
        {
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var r = Office(new StressParameters(), seed);
                var breaks = r.Timeline.Where(e => e.Kind == StressEntryKind.WillBroke).ToList();
                var switches = r.Timeline.Where(e => e.Kind == StressEntryKind.GoalChanged).ToList();
                Check(breaks.Count == 2 && switches.Count == 2, $"seed {seed}: {breaks.Count} breaks and {switches.Count} switches, wanted 2 and 2");
                var rage = switches[0];
                var calm = switches[1];
                Check(breaks[0].TimeMs == r.TimeToBreakMs, $"seed {seed}: the time to break is not the first break");
                Check(rage.Goal == OfficeGoal.Rage && rage.TimeMs - breaks[0].TimeMs == 100 && rage.Resolve == 1.0, $"seed {seed}: the rage row is {rage.Goal} {rage.TimeMs - breaks[0].TimeMs} ms after the snap with resolve {rage.Resolve}");
                long lasted = breaks[1].TimeMs - breaks[0].TimeMs;
                Check(lasted >= 5000 && lasted <= 20000, $"seed {seed}: the second break came {lasted} ms after the snap");
                Check(calm.Goal == OfficeGoal.Calm && calm.TimeMs - breaks[1].TimeMs == 100, $"seed {seed}: the calm row is {calm.Goal} {calm.TimeMs - breaks[1].TimeMs} ms after the second break");
                Check(r.Timeline.Where(e => e.TimeMs < rage.TimeMs).All(e => e.Goal == OfficeGoal.KeepComposure), $"seed {seed}: a row before the rage is not KeepComposure");
                Check(r.Timeline.Where(e => e.TimeMs >= rage.TimeMs && e.TimeMs < calm.TimeMs).All(e => e.Goal == OfficeGoal.Rage), $"seed {seed}: a row in the rage is not Rage");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).All(e => e.Goal == OfficeGoal.Calm), $"seed {seed}: a row from calm on is not Calm");
                Check(r.Outcome == Outcome.WillBroke && r.EndMs == calm.TimeMs + 8000, $"seed {seed}: ended at {r.EndMs}, calm was at {calm.TimeMs}");
            }
            var short_ = Office(new StressParameters { ReleaseSeconds = 2.0 }, 1);
            Check(short_.EndMs == short_.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm).TimeMs + 2000, "ReleaseSeconds did not set the settle time");
            var clipped = Office(new StressParameters { MaxSeconds = 85.0 }, 1);
            Check(clipped.Outcome == Outcome.WillBroke && clipped.EndMs == 85000 && clipped.Timeline.All(e => e.Kind != StressEntryKind.Endured && e.Goal != OfficeGoal.Calm), $"a release past MaxSeconds ended at {clipped.EndMs}");
        }),

        ("office calm is terminal: with the release moved off calm the run goes to MaxSeconds, and after the Calm row the goal never changes again, whatever the senses and the mending will do", () =>
        {
            // Bursts of heavy load with quiet between: the will breaks, mends in the quiet, and breaks again, which the office's steady load never does.
            var Bursty = Cause.Office with { Stressors = new[] { new Stressor("printer", "printer", 5.0, 0.0, 100.0, 40.0, 1.0, 0.0) } };
            int thirdBreaks = 0;
            for (ulong seed = 1; seed <= 5; seed++)
            {
                var r = Office(new StressParameters { ReleaseGoalId = "keep-composure", Cause = Bursty }, seed);
                var calm = r.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm);
                Check(r.EndMs == 180000, $"seed {seed}: ended at {r.EndMs}");
                Check(r.Timeline.Count(e => e.Kind == StressEntryKind.GoalChanged) == 2, $"seed {seed}: a goal changed after calm");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).All(e => e.Goal == OfficeGoal.Calm), $"seed {seed}: the goal column left Calm");
                thirdBreaks += r.Timeline.Count(e => e.Kind == StressEntryKind.WillBroke && e.TimeMs > calm.TimeMs);
            }
            Check(thirdBreaks > 0, "no run broke the will a third time after calm; the test no longer exercises the rule");
        }),

        ("office release: the run ends on the Brain's release goal, by id, not on any goal change; an id the Brain lacks is refused", () =>
        {
            var r = Office(new StressParameters { ReleaseGoalId = "keep-composure" }, 1);
            Check(r.EndMs == 180000 && r.Outcome == Outcome.WillBroke, $"releasing on a goal the Brain never switches to ended the run at {r.EndMs}");
            var rage = Office(new StressParameters { ReleaseGoalId = OfficeBrain.RageId }, 1);
            var sw = rage.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged);
            Check(sw.Goal == OfficeGoal.Rage && rage.EndMs == sw.TimeMs + 8000, $"releasing on the rage ended the run at {rage.EndMs}, the rage was at {sw.TimeMs}");
            bool threw = false;
            try { NewOffice(new StressParameters { ReleaseGoalId = "nowhere" }, 1); } catch (ArgumentException) { threw = true; }
            Check(threw, "a release goal the Brain lacks was accepted");
        }),

        ("office rage: a will that holds never switches, and the job's pull is the composure goal's alone", () =>
        {
            var quiet = Office(new StressParameters { Cause = Cause.Office with { Stressors = Array.Empty<Stressor>() } }, 1);
            Check(quiet.Timeline.All(e => e.Kind != StressEntryKind.GoalChanged && e.Goal == OfficeGoal.KeepComposure), "a will that held switched goals");
            var p = new StressParameters();
            var brain = OfficeBrain.Build(p, OfficePoses.Identity);
            Check(brain.Goals[OfficeBrain.ComposureIndex].Pull == p.Cause.ObjectivePull && brain.Goals[OfficeBrain.RageIndex].Pull == 0.0, "the pulls are not the cause's and zero");
        }),

        ("office body: the rage is visible: both hands move at least 3x faster, and over 1 m/s (a limp rage gives 0.6), in the 2 s after the switch than in the 2 s before, the pelvis stays within 1 cm, and two runs agree", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var a = OfficeBody.Run(p, 1);
            var b = OfficeBody.Run(p, 1);
            Check(a.Run.Render() == b.Run.Render() && a.HandL.SequenceEqual(b.HandL) && a.HandR.SequenceEqual(b.HandR) && a.Pelvis.SequenceEqual(b.Pelvis), "two body runs differ");
            Check(a.Run.Outcome == Outcome.WillBroke && a.RageTick is not null, $"outcome {a.Run.Outcome}");
            int sw = a.RageTick!.Value;
            var calmRow = a.Run.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm);
            int calmTick = (int)Math.Round(calmRow.TimeMs * Hz / 1000.0);
            Check(a.HandL.Count == calmTick + 8 * Hz + 1, $"the run has {a.HandL.Count} steps, calm is at {calmTick}");
            double Rms(IReadOnlyList<BodyPoint> h, int from, int to)
            {
                double sum = 0;
                for (int i = from + 1; i < to; i++)
                {
                    double dx = h[i].X - h[i - 1].X, dy = h[i].Y - h[i - 1].Y, dz = h[i].Z - h[i - 1].Z;
                    sum += (dx * dx + dy * dy + dz * dz) * Hz * Hz;
                }
                return Math.Sqrt(sum / (to - from - 1));
            }
            foreach (var (name, hand) in new[] { ("left", a.HandL), ("right", a.HandR) })
            {
                double before = Rms(hand, sw - 2 * Hz, sw), after = Rms(hand, sw, sw + 2 * Hz);
                Check(after >= 3 * before && after > 1.0, $"{name} hand: {after:F3} m/s after the switch, {before:F3} m/s before");
            }
            Check(a.Strikes(0, sw) == 0 && a.Strikes(sw, a.HandL.Count) >= 11, $"strikes: {a.Strikes(0, sw)} before the switch, {a.Strikes(sw, a.HandL.Count)} after");
            // The strike reaches forward: the shoulder's angle shows in how far the fist gets ahead of where the composed hand hung.
            double Reach(IReadOnlyList<BodyPoint> h) => h.Skip(sw).Max(q => q.Z) - h[sw - 1].Z;
            Check(Reach(a.HandL) > 0.58 && Reach(a.HandR) > 0.54, $"the strikes reach {Reach(a.HandL):F3} m (left) and {Reach(a.HandR):F3} m (right) ahead of the composed hand");
            var rage = OfficeBrain.Build(p, OfficeBody.Poses).Goals[OfficeBrain.RageIndex].Behaviour;
            Check(rage.Hold.HoldStrength == 1f, "the rage does not write full muscle strength");
            float drift = a.Pelvis.Max(q => MathF.Sqrt(MathF.Pow(q.X - a.Pelvis[0].X, 2) + MathF.Pow(q.Y - a.Pelvis[0].Y, 2) + MathF.Pow(q.Z - a.Pelvis[0].Z, 2)));
            Check(drift < 0.01f, $"the pelvis moved {drift:F4} m");
        }),

        ("office body: the rage weakens as it drains his will (each hand slower in its last 2 s than in the 2 s after onset), and he settles: no hand moves faster than 0.05 m/s over the run's last second", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var a = OfficeBody.Run(p, seed);
                int rage = a.RageTick!.Value;
                int calm = (int)Math.Round(a.Run.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm).TimeMs * Hz / 1000.0);
                double Speed(IReadOnlyList<BodyPoint> h, int i)
                {
                    double dx = h[i].X - h[i - 1].X, dy = h[i].Y - h[i - 1].Y, dz = h[i].Z - h[i - 1].Z;
                    return Math.Sqrt(dx * dx + dy * dy + dz * dz) * Hz;
                }
                double Rms(IReadOnlyList<BodyPoint> h, int from, int to) => Math.Sqrt(Enumerable.Range(from + 1, to - from - 1).Average(i => Speed(h, i) * Speed(h, i)));
                foreach (var (name, hand) in new[] { ("left", a.HandL), ("right", a.HandR) })
                {
                    double onset = Rms(hand, rage, rage + 2 * Hz), late = Rms(hand, calm - 2 * Hz, calm);
                    Check(late < onset, $"seed {seed} {name} hand: {late:F3} m/s in the rage's last 2 s, {onset:F3} m/s in the 2 s after onset");
                    double still = Enumerable.Range(hand.Count - Hz, Hz).Max(i => Speed(hand, i));
                    Check(still < 0.05, $"seed {seed} {name} hand: {still:F3} m/s at its fastest in the run's last second");
                }
            }
        }),

        ("office body settle: the seated body is still when the run ends: no hand faster than 0.025 m/s (half the 0.05 limit) over the last second, on the seeds that were tightest at 6 s and every seed 1..100 when BDS_SETTLE_SWEEP is set", () =>
        {
            // The release is a timer (the office never ends on body stillness), so the timer is calibrated and this is where that is checked.
            // 6 s left seeds 55 and 100 at 0.0512 and 0.0521 m/s; at 8 s the worst of seeds 1..100 is 0.0124 m/s (seed 100). The full sweep takes about a minute:
            //   BDS_SETTLE_SWEEP=1 dotnet run -c Release --project tests/BadDay.Tests -- "office body settle"
            var seeds = Environment.GetEnvironmentVariable("BDS_SETTLE_SWEEP") is { Length: > 0 }
                ? Enumerable.Range(1, 100).Select(n => (ulong)n)
                : new ulong[] { 1, 2, 3, 7, 55, 69, 100 };
            double worst = 0.0;
            ulong worstSeed = 0;
            foreach (ulong seed in seeds)
            {
                var a = OfficeBody.Run(new StressParameters { TickSeconds = OfficeBody.TickSeconds }, seed);
                foreach (var h in new[] { a.HandL, a.HandR })
                {
                    for (int k = h.Count - Hz; k < h.Count; k++)
                    {
                        double dx = h[k].X - h[k - 1].X, dy = h[k].Y - h[k - 1].Y, dz = h[k].Z - h[k - 1].Z;
                        double v = Math.Sqrt(dx * dx + dy * dy + dz * dz) * Hz;
                        if (v > worst) { worst = v; worstSeed = seed; }
                    }
                }
            }
            Check(worst < 0.025, $"seed {worstSeed}: a hand still moves at {worst:F4} m/s in the run's last second");
        }),

        ("office body: the desk's emitters are sensed at the head, so the body breaks within 0.5 s of the headless run (sensed at a hand it is 3.6 s off)", () =>
        {
            var body = OfficeBody.Run(new StressParameters { TickSeconds = OfficeBody.TickSeconds }, 1).Run;
            var headless = Office(new StressParameters(), 1);
            Check(Math.Abs(body.TimeToBreakMs!.Value - headless.TimeToBreakMs!.Value) < 500, $"body {body.TimeToBreakMs} ms, headless {headless.TimeToBreakMs} ms");
        }),
    };

    private static readonly float[] Point = new float[1], Strengths = new float[1];
    private static readonly Quat[] Targets = new Quat[1];

    private static StressSession NewOffice(StressParameters p, ulong seed) => new(p, seed, 1, OfficePoses.Identity);

    private static void Step(StressSession s) => s.Tick(Point, Point, Point, Strengths, Targets);

    private static StressResult Office(StressParameters p, ulong seed, params SilenceCommand[] silences) =>
        StressSimulation.Run(p, seed, silences);

    /// <summary>The median break time over <paramref name="count"/> seeds; a run that endured counts as never.</summary>
    private static long MedianBreak(StressParameters p, ulong first, int count, params SilenceCommand[] silences)
    {
        var times = new List<long>();
        for (int i = 0; i < count; i++) times.Add(Office(p, first + (ulong)i, silences).TimeToBreakMs ?? long.MaxValue);
        times.Sort();
        return times[times.Count / 2];
    }

    private static int Tick(long ms) => (int)Math.Round(ms * Hz / 1000.0);

    private static float Dist(HandSample h)
    {
        var c = BodyPainBox.HoldPoint;
        return MathF.Sqrt((h.X - c.X) * (h.X - c.X) + (h.Y - c.Y) * (h.Y - c.Y) + (h.Z - c.Z) * (h.Z - c.Z));
    }

    private static double SpeedRms(BodyPainBoxResult r, int from, int to)
    {
        double sum = 0;
        for (int i = from + 1; i < to; i++)
        {
            double dx = r.Hand[i].X - r.Hand[i - 1].X, dy = r.Hand[i].Y - r.Hand[i - 1].Y, dz = r.Hand[i].Z - r.Hand[i - 1].Z;
            sum += (dx * dx + dy * dy + dz * dz) * Hz * Hz;
        }
        return Math.Sqrt(sum / (to - from - 1));
    }

    /// <summary>Steps a body session to its end, calling <paramref name="beforeStep"/> before every step, and records the hand per tick as <see cref="BodyPainBox.Run"/> does.</summary>
    private static BodyPainBoxResult RunLive(PainBoxParameters p, ulong seed, Action<BodyPainBoxSession> beforeStep)
    {
        using var s = new BodyPainBoxSession(p, seed);
        var hand = new List<HandSample>();
        while (!s.Finished)
        {
            beforeStep(s);
            long before = s.TickIndex;
            s.Step();
            if (s.TickIndex != before || hand.Count == 0) hand.Add(s.Hand);
        }
        return new BodyPainBoxResult(s.Result(), s.Excursions, hand);
    }

    private static void SameRun(BodyPainBoxResult a, BodyPainBoxResult b, string who)
    {
        Check(a.Run.Render() == b.Run.Render() && a.Run.Summary() == b.Run.Summary() && a.Excursions == b.Excursions, $"{who}: results differ: '{a.Run.Summary()}' against '{b.Run.Summary()}'");
        Check(a.Hand.Count == b.Hand.Count && a.Hand.Zip(b.Hand).All(h => h.First == h.Second), $"{who}: hand tracks differ ({a.Hand.Count} and {b.Hand.Count} samples)");
    }

    /// <summary>Runs every test; with an argument, only the tests whose name contains it (the body tests take minutes).</summary>
    public static int Main(string[] args)
    {
        int failed = 0;
        var tests = args.Length > 0 ? Tests.Where(t => t.Name.Contains(args[0])).ToArray() : Tests;
        foreach (var (name, body) in tests)
        {
            try { body(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
