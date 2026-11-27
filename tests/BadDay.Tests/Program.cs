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
    // Why the flinch test runs on Steady and not on the shipped curve (WillCurve 0.25): its control is the same run with FlinchGain 0, and that is a
    // controlled comparison only while the arm cannot feed back differently into the drive. On the shipped curve the control's strength is lower from the
    // first tick (the leak adds to the weight), so the two hands part by a centimetre before any flinch and the two drives part at tick 96; the gap a flinch
    // adds is then lost in that drift. What the shipped curve adds on top, the flinch's strength recruitment (min(1, weight + leak x FlinchGain)), is
    // defended by "hand stays in the box until the will breaks" (Soul killed the mutant that removes it, in that test).
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
            // Measured idle drift of the palm from the hold point (seed 1; without pain the drift is seed-independent): under the gravity feedforward the arm no longer sags,
            // it settles onto the hold pose. At 4 substeps (the tuned count) the largest drift is 1.35 cm from 3 s and 0.66 cm from 8 s, at 8 substeps 1.16 and 0.48 cm: the
            // limits sit 2.6 and 3.0 times the 4-substep reading, 3.0 and 4.2 times the 8-substep one. The limits hold at 4 and 8 substeps only: at 2 the drift is 5.75 cm
            // from 3 s and 0.90 cm from 8 s, at 1 it is 20.5 cm and 20.4 cm.
            // A flinch raises the gap from a leak-free arm by a median 3.32 cm over seeds 1-9 (the flinch control below); the limits stay at 0.035 and 0.020.
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

        ("body: the flinch moves the hand: against a flinch-gain-0 control on the same seed, the median over seeds 1-9 of the largest rise of the gap within 200 ms of a pre-break flinch, above the gap at that flinch, is over 1.68 cm", () =>
        {
            // The claim is a distribution (ruling deterministic-bound-margin). Per seed: the largest rise, over the flinches whose 200 ms window ends before the break tick, of
            // max Gap over [f, f+200 ms] minus Gap(f); the gap before the first flinch is the continuous leak of the feedforward, so a rise is read against the gap at its own tick.
            // Pre-break only: the flinch at the break tick has a window that reaches the withdrawal. Measured medians over seeds 1-9: shipped 3.32 cm (per seed 1.92 2.84 2.85 3.22 3.32
            // 3.40 3.46 3.47 4.06), FlinchGain halved 0.85 cm, flinch pose = hold pose 0.30 cm; over seeds 1-50 shipped 3.43 (min 1.40, max 4.62), halved 0.92, no flinch 0.30.
            // The bound 1.68 cm is the geometric mean of 3.32 and 0.85 (separation 3.91 on the halved mutant). The nervous state is checked per seed: the flinches are equal.
            var rises = new List<float>();
            for (ulong seed = 1; seed <= 9; seed++)
            {
                var full = BodyPainBox.Run(Steady, seed);
                var control = BodyPainBox.Run(Steady with { Hold = new Lodur.HoldParameters { FlinchGain = 0.0 } }, seed);
                Check(full.Run.Flinches == control.Run.Flinches, $"seed {seed}: the nervous state changed with the arm");
                var flinches = full.Run.Timeline.Where(e => e.Kind == EntryKind.Flinch).Select(e => Tick(e.TimeMs)).ToList();
                float Gap(int i) => MathF.Sqrt(MathF.Pow(full.Hand[i].X - control.Hand[i].X, 2) + MathF.Pow(full.Hand[i].Y - control.Hand[i].Y, 2) + MathF.Pow(full.Hand[i].Z - control.Hand[i].Z, 2));
                int breakTick = Tick(full.Run.TimeToBreakMs ?? long.MaxValue / 2);
                var pre = flinches.Where(f => f + Hz / 5 < breakTick && f + Hz / 5 < Math.Min(full.Hand.Count, control.Hand.Count)).ToList();
                rises.Add(pre.Count == 0 ? 0f : pre.Max(f => Enumerable.Range(f, Hz / 5 + 1).Max(Gap) - Gap(f)));
            }
            rises.Sort();
            Check(rises[rises.Count / 2] > 0.0168f, $"median largest pre-break rise over seeds 1-9 is {rises[rises.Count / 2]:F4} m: {string.Join(" ", rises.Select(x => (x * 100).ToString("F2")))} cm");
        }),

        ("chamber: the withdrawal is one-way: with the box quiet for 90 s past it and the will mended, the goal stays Withdraw, and switching the box off at that moment does not end the run as if the will held", () =>
        {
            var pose = new[] { Lodur.Quat.Identity };
            var session = new PainBoxSession(Default, 1, new Lodur.BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f), 1, pose, pose);
            var point = new float[1];
            var strengths = new float[1];
            var targets = new Lodur.Quat[1];
            while (!session.Withdrew) session.Tick(point, point, point, strengths, targets);
            session.SetIntensity(0.0); // quiet, with the box still on: the will can mend
            bool mended = false;
            for (int i = 0; i < 9000; i++)
            {
                session.Tick(point, point, point, strengths, targets);
                Check(session.Actor.GoalIndex == ChamberBrain.WithdrawIndex, $"the goal left Withdraw {i} ticks after the withdrawal");
                if (session.Actor.Drive.WillBroken) continue;
                mended = true;
                break;
            }
            Check(mended && session.Actor.Drive.Breaks > 0, "the will never mended: the test no longer exercises the rule");
            // A second past the mend: a withdraw gate that read WillBroken would switch back after DeliberateLatency (10 ticks), so stopping at the first mended tick cannot see it.
            for (int i = 0; i < 100; i++)
            {
                session.Tick(point, point, point, strengths, targets);
                Check(session.Actor.GoalIndex == ChamberBrain.WithdrawIndex && !session.Actor.Drive.WillBroken, $"the goal left Withdraw {i} ticks after the will mended");
            }
            // The box goes off with the will mended: a reader that asked WillBroken would call the run a disabled box with the will intact.
            session.SwitchOff();
            Check(!session.BoxOn && !session.Disabled(), "a box going off after the mend ended the run as a disabled box");
            for (int i = 0; i < 100; i++)
            {
                session.Tick(point, point, point, strengths, targets);
                Check(session.Actor.GoalIndex == ChamberBrain.WithdrawIndex, $"the goal left Withdraw {i} ticks after the box went off");
            }
        }),

        ("pain box: a break after a mend is a row, not the time to break: the result's break is the first one and never after the run's end", () =>
        {
            var pose = new[] { Lodur.Quat.Identity };
            var session = new PainBoxSession(Default with { MaxSeconds = 300.0 }, 1, new Lodur.BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f), 1, pose, pose);
            var point = new float[1];
            var strengths = new float[1];
            var targets = new Lodur.Quat[1];
            while (!session.Withdrew) session.Tick(point, point, point, strengths, targets);
            session.SetIntensity(0.0);
            while (session.Actor.Drive.WillBroken) session.Tick(point, point, point, strengths, targets);
            session.SetIntensity(PainBox.MaxLevel);
            int breaks = 0;
            while (!session.Finished && breaks < 1)
            {
                if ((session.Tick(point, point, point, strengths, targets) & Lodur.TickEvents.WillBroke) != 0) breaks++;
            }
            var r = session.Result();
            Check(breaks == 1 && r.Timeline.Count(e => e.Kind == EntryKind.WillBroke) == 2, "the will did not break a second time: the test no longer exercises the rule");
            Check(r.TimeToBreakMs == 14730 && r.TimeToBreakMs <= r.EndMs, $"break {r.TimeToBreakMs} ms, end {r.EndMs} ms");
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

        ("body: the withdrawal is the body's: seeds 1..20 on the chamber end WillBroke with the hand out within 300 ms of the break, and never before it", () =>
        {
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var r = BodyPainBox.Run(OnBody, seed);
                Check(r.Run.Outcome == Outcome.WillBroke && r.Run.HandOutMs is not null, $"seed {seed}: outcome {r.Run.Outcome}, hand out {r.Run.HandOutMs}");
                long late = r.Run.HandOutMs!.Value - r.Run.TimeToBreakMs!.Value;
                Check(late >= 0 && late <= 300, $"seed {seed}: the hand was out {late} ms after the break");
            }
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

        ("office: the stress column is the arousal column on every rendered row, through the rage and the calm too (the bar shows arousal, never 1 - resolve)", () =>
        {
            foreach (ulong seed in new ulong[] { 1, 3 })
            {
                var lines = Office(new StressParameters(), seed).Render().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                Check(lines.Length > 10, "too few rows");
                bool apart = false;
                foreach (var line in lines)
                {
                    var f = line.Split(' ');
                    Check(f.Length == 10 && f[8] == f[6], $"row '{line}'");
                    apart |= Math.Abs(double.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture) - (1.0 - double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture))) > 0.05;
                }
                Check(apart, "arousal never differed from 1 - resolve, so the check proved nothing");
            }
        }),

        ("office calm tail: arousal on the calm row starts from where the rage left it, falls without rising to exactly 0 by CalmFadeSeconds and stays 0 to the end, while the stressors keep sounding", () =>
        {
            var p = new StressParameters { SampleEverySeconds = 0.1 };
            int sounding = 0, loaded = 0;
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var r = Office(p, seed);
                var calm = r.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm);
                var rageLast = r.Timeline.Last(e => e.TimeMs < calm.TimeMs);
                Check(rageLast.Arousal > 0.0 && Math.Abs(calm.Arousal - rageLast.Arousal) < 0.1 * rageLast.Arousal, $"seed {seed}: arousal on the calm row is {calm.Arousal}, the rage's last was {rageLast.Arousal}; calm did not start from where the rage left it");
                var tail = r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).ToList();
                for (int i = 1; i < tail.Count; i++) Check(tail[i].Arousal <= tail[i - 1].Arousal, $"seed {seed}: arousal rose at {tail[i].TimeMs} ms");
                var mid = tail.First(e => e.TimeMs >= calm.TimeMs + 2500);
                Check(mid.Arousal > 0.0, $"seed {seed}: arousal was already {mid.Arousal} at 2.5 s into calm; the fade is a hard cut");
                // The fade is 3 s by the ruling's "a few seconds", written out here rather than read back from CalmFadeSeconds: a fade of 2.6 s is already 0 at 2.8 s, one of 3.9 s is not yet 0 at 3.0 s.
                Check(tail.First(e => e.TimeMs >= calm.TimeMs + 2800).Arousal > 0.0, $"seed {seed}: arousal already 0 at 2.8 s into calm");
                var after = tail.Where(e => e.TimeMs >= calm.TimeMs + 3000).ToList();
                Check(after.Count > 40 && after.All(e => e.Arousal == 0.0), $"seed {seed}: arousal not exactly 0 from 3 s after calm to the end");
                // His senses fade with it: load is 0 from a tick past the fade's end (Sense reads the gain a tick late), and some of the load is still there a second in.
                Check(tail.Where(e => e.TimeMs >= calm.TimeMs + 3100).All(e => e.Load == 0.0), $"seed {seed}: load above 0 after the fade");
                if (tail.Any(e => e.TimeMs >= calm.TimeMs + 500 && e.TimeMs <= calm.TimeMs + 1500 && e.Load > 0.0)) loaded++;
                if (after.Any(e => e.Audio > 0.0 || e.Visual > 0.0 || e.Interrupt > 0.0)) sounding++;
            }
            // The stressors are periodic and some seeds have a quiet 5 s window after the fade; across the seeds the office does keep sounding, so calm silenced his senses and not the office.
            Check(sounding >= 19, $"stressors sounded after the fade on only {sounding} of 20 seeds");
            // A hard cut or a gain that goes to 0 at once leaves no load in the fade's first second; a fade carries it on 10 of seeds 1..20 (the rest have no stressor sounding then).
            Check(loaded >= 10, $"load was above 0 in the first 1.5 s of calm on only {loaded} of 20 seeds");
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
            Check(plain.Contains("stress (arousal)") && plain.Contains("time_ms kind audio visual interrupt load arousal resolve stress goal"), "no label or header");
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

        ("office golden [P]: the rows up to the break are rung 3's bytes, the 50-seed sweep ends in its captured line, and the run's own bytes are captured (re-captured with the arc, again for the 8 s settle, and again for the arousal bar)", () =>
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
            // The stress column was 1 - resolve then, and is arousal now: each row gets its old column back, in the same format.
            var rung3 = new List<string> { "Bad day at the office, rung 3 (stress = 1 - resolve, \"lost composure\")", lines[1] };
            for (int i = 2; i <= brk; i++)
            {
                var f = lines[i].Replace(" KeepComposure", i == brk ? " Withdraw" : " HoldHand").Split(' ');
                f[8] = (1.0 - double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture)).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                rung3.Add(string.Join(' ', f));
            }
            rung3.Add(lines[^1]);
            Check(Hash(string.Join('\n', rung3) + "\n") == "66098b69834c3c8b5e5b694f0059b8ed566e31d332f36be7e57baf70ae650823", "the office's rows up to the break differ from rung 3's");
            Check(Hash(string.Join('\n', lines) + "\n") == "f52e70b641cde4bcb5c5f6286ed022ccd31950425656b23f302a041218396db6", $"office --seed 1 changed: {Hash(string.Join('\n', lines) + "\n")}");
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

        ("office rage onset: the rage is involuntary for RageOnsetSeconds, so it costs no will in that first second and drains at RageEffort after it", () =>
        {
            double ResolveAfter(StressParameters p, long afterMs)
            {
                var r = Office(p with { SampleEverySeconds = 0.1 }, 1);
                long rageMs = r.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Rage).TimeMs;
                return r.Timeline.First(e => e.Kind == StressEntryKind.Sample && e.TimeMs >= rageMs + afterMs).Resolve;
            }
            var costly = new StressParameters { RageEffort = 0.5 };
            double onsetEnd = ResolveAfter(costly, 900), later = ResolveAfter(costly, 1900), noOnset = ResolveAfter(costly with { RageOnsetSeconds = 0.0 }, 900);
            Check(onsetEnd > 0.8, $"resolve {onsetEnd:F3} at 0.9 s into a rage with a 1 s onset: the onset cost will");
            Check(later < onsetEnd - 0.35, $"resolve {later:F3} at 1.9 s, {onsetEnd:F3} at 0.9 s: the rage did not start costing after the onset");
            Check(noOnset < 0.65, $"resolve {noOnset:F3} at 0.9 s into a rage with no onset: it should already be paying");
        }),

        ("office --body summary: the rage line names its span and how it ended (to calm, or clipped by --max), and a break clipped before the rage says so", () =>
        {
            string Out(params string[] a)
            {
                var w = new StringWriter();
                BadDay.Cli.Program.Execute(a, w);
                return w.ToString().Replace("\r\n", "\n");
            }
            string Rage(string o) => o.Split('\n').First(l => l.StartsWith("rage from ") || l.StartsWith("the will broke") || l.StartsWith("no rage"));
            string full = Rage(Out("office", "--seed", "1", "--body"));
            Check(full.StartsWith("rage from 83458 ms for 11.3 s (to calm at 94717 ms); strikes "), $"full run: {full}");
            string clipped = Rage(Out("office", "--seed", "1", "--body", "--max", "90"));
            Check(clipped.StartsWith("rage from 83458 ms for 6.5 s (clipped by --max, no calm); strikes "), $"clipped in the rage: {clipped}");
            string early = Rage(Out("office", "--seed", "1", "--body", "--max", "83.4"));
            Check(early.Contains("before the rage took over"), $"clipped before the rage: {early}");
        }),

        ("office calm is terminal: with the release moved off calm the run goes to MaxSeconds, and after the Calm row the goal never changes again, with the senses still pressing", () =>
        {
            // Calm's fade is longer than the run here, so his senses stay open and the load keeps pressing for all 180 s. A default calm silences them: since that ruling no public path
            // breaks the will a third time after calm (its mend needs arousal under 0.1, which only the quiet fade gives, and the quiet has no load), so "whatever the mending will does" is not yet reached.
            var Bursty = Cause.Office with { Stressors = new[] { new Stressor("printer", "printer", 5.0, 0.0, 100.0, 40.0, 1.0, 0.0) } };
            for (ulong seed = 1; seed <= 5; seed++)
            {
                var r = Office(new StressParameters { ReleaseGoalId = "keep-composure", Cause = Bursty, CalmFadeSeconds = 1.0e6 }, seed);
                var calm = r.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm);
                Check(r.EndMs == 180000, $"seed {seed}: ended at {r.EndMs}");
                Check(r.Timeline.Count(e => e.Kind == StressEntryKind.GoalChanged) == 2, $"seed {seed}: a goal changed after calm");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).All(e => e.Goal == OfficeGoal.Calm), $"seed {seed}: the goal column left Calm");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs + 5000).Any(e => e.Load > 0.0), $"seed {seed}: no load after calm; the test no longer presses on the goal");
            }
        }),

        ("office calm is terminal on the shipped calm: over a full-length run the goal never leaves Calm, though his will mends in the quiet", () =>
        {
            // The default fade, so the senses are silenced as shipped, and the release moved off calm so the run goes the 180 s. With no load his arousal stays 0 and resolve mends from 0:
            // a calm that gated on a mended will would be lost here, which the open-senses test above cannot reach.
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var r = Office(new StressParameters { ReleaseGoalId = "keep-composure" }, seed);
                var calm = r.Timeline.Single(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm);
                Check(r.EndMs == 180000, $"seed {seed}: ended at {r.EndMs}");
                Check(r.Timeline.Count(e => e.Kind == StressEntryKind.GoalChanged) == 2, $"seed {seed}: a goal changed after calm");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).All(e => e.Goal == OfficeGoal.Calm), $"seed {seed}: the goal column left Calm");
                Check(r.Timeline.Where(e => e.TimeMs >= calm.TimeMs).Max(e => e.Resolve) > 0.05, $"seed {seed}: his will never mended after calm; the test no longer reaches the mend");
            }
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
            // 6 s left seeds 55 and 100 at 0.0512 and 0.0521 m/s; at 8 s the worst of seeds 1..100 is 0.0160 m/s (seed 74; 0.0124 m/s, seed 100, before the hands). The full sweep takes about a minute:
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

        ("office session: stepping OfficeBodySession to Finished gives OfficeBody.Run's timeline, seeds 1 and 2, and the pelvis and hands are the ones Run recorded", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            foreach (ulong seed in new ulong[] { 1, 2 })
            {
                var run = OfficeBody.Run(p, seed);
                using var s = new OfficeBodySession(p, seed);
                int steps = 0;
                while (!s.Finished) { s.Step(); steps++; }
                Check(s.Result().Render() == run.Run.Render(), $"seed {seed}: the stepped session's timeline differs from Run's");
                Check(steps == run.HandL.Count && s.HandL == run.HandL[^1] && s.HandR == run.HandR[^1] && s.Pelvis == run.Pelvis[^1], $"seed {seed}: {steps} steps against {run.HandL.Count} recorded, or the last points differ");
                bool threw = false;
                try { new OfficeBodySession(p with { TickSeconds = 0.01 }, seed).Dispose(); } catch (ArgumentException) { threw = true; }
                Check(threw, "a tick rate other than 1/120 s was accepted");
            }
        }),

        ("office wound kills by blood loss: Wound(LowerArm L, Knife) at tick 0 gives exactly one Died event, DeathCause BloodLoss, Outcome Died, a death 30-60 s in (probe: 42.1 s with skin and muscle) that two runs agree on, and a run that ends ReleaseSeconds later", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var arm = SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left);
            long? diedAt = null;
            for (int run = 0; run < 2; run++)
            {
                using var s = new OfficeBodySession(p, 1);
                Check(s.Wound(Hit(s, arm), WoundTool.Knife), "the knife was refused on the lower arm");
                var buffer = new BodyEvent[16];
                int died = 0;
                bool deadBefore = false;
                while (!s.Finished)
                {
                    s.Step();
                    int n; while ((n = s.DrainEvents(buffer)) > 0) for (int i = 0; i < n; i++) if ((buffer[i].Events & Lodur.TickEvents.Died) != 0) died++;
                    deadBefore |= s.Dead && died == 0;
                }
                var r = s.Result();
                Check(died == 1 && !deadBefore, $"{died} Died events drained");
                Check(s.Dead && s.DeathCause == Lodur.DeathCause.BloodLoss && r.DeathCause == Lodur.DeathCause.BloodLoss, $"cause {s.DeathCause}");
                Check(r.Outcome == Outcome.Died && r.DiedAtMs is long ms && ms >= 30000 && ms <= 60000, $"outcome {r.Outcome}, died at {r.DiedAtMs} ms");
                Check(r.EndMs == r.DiedAtMs + (long)Math.Round(p.ReleaseSeconds * 1000.0), $"the run ended at {r.EndMs} ms, the death was at {r.DiedAtMs} ms");
                Check(r.Timeline.Count(e => e.Kind == StressEntryKind.Died) == 1 && r.Summary().Contains("BloodLoss"), "the Died row or the summary is missing");
                Check(s.Consciousness == 0.0 && s.BloodLoss >= 1.0, $"consciousness {s.Consciousness}, blood loss {s.BloodLoss}");
                if (diedAt is null) diedAt = r.DiedAtMs; else Check(diedAt == r.DiedAtMs, $"two runs died at {diedAt} and {r.DiedAtMs} ms");
            }
        }),

        ("office death is limp and ends the run: after the Died tick every strength the session wrote is 0 and the goal never changes again, the death is before the run's end by exactly ReleaseSeconds, and before it the body was driving muscles", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var plan = SeatedPuppet.Plan;
            var physiologyPlan = Lodur.PhysiologyPlans.Humanoid();
            var physiology = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
            var session = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), physiology, plan, physiologyPlan);
            Check(session.Wound(plan.PartIndex("LowerArm", Lodur.Side.Left), default, WoundTool.Knife), "the knife was refused");
            var pts = new float[plan.SensePoints.Count];
            var strengths = new float[plan.Joints.Count];
            var targets = new Quat[plan.Joints.Count];
            float drivenBefore = 0f;
            long diedTick = -1;
            int goalAtDeath = -1;
            while (!session.Finished)
            {
                var ev = session.Tick(pts, pts, pts, strengths, targets);
                if ((ev & Lodur.TickEvents.Died) != 0) { diedTick = session.TickIndex - 1; goalAtDeath = session.Actor.GoalIndex; }
                if (diedTick < 0) drivenBefore = Math.Max(drivenBefore, strengths.Max());
                else Check(strengths.All(v => v == 0f) && session.Actor.GoalIndex == goalAtDeath, $"tick {session.TickIndex - 1}: a strength or the goal moved after the death");
            }
            var r = session.Result();
            Check(diedTick >= 0 && r.Outcome == Outcome.Died, "the body did not die");
            Check(drivenBefore > 0f, "no muscle was driven before the death, so limp proves nothing");
            Check(session.TickIndex - 1 == diedTick + (long)Math.Round(p.ReleaseSeconds / p.TickSeconds), $"the run ended at tick {session.TickIndex - 1}, the death was at {diedTick}");
        }),

        ("office wound refusals: an out-of-range part and a headless session refuse, and a palm takes the knife on skin, muscle and metacarpals", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using (var s = new OfficeBodySession(p, 1))
            {
                Check(!s.Wound(new PartHit(-1, default, default), WoundTool.Knife) && !s.Wound(new PartHit(SeatedPuppet.Plan.Parts.Count, default, default), WoundTool.Knife), "an out-of-range part was wounded");
            }
            Check(!NewOffice(p, 1).Wound(0, default, WoundTool.Knife), "a headless session was wounded");

            var plan = SeatedPuppet.Plan;
            var physiologyPlan = Lodur.PhysiologyPlans.Humanoid();
            var physiology = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
            var session = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), physiology, plan, physiologyPlan);
            int palm = plan.PartIndex("Palm", Lodur.Side.Left);
            Check(session.Wound(palm, default, WoundTool.Knife), "the palm refused the knife");
            var pts = new float[plan.SensePoints.Count];
            session.Tick(pts, pts, pts, new float[plan.Joints.Count], new Quat[plan.Joints.Count]);
            Check(physiology.Integrity(palm, physiology.TissueIndex(palm, "Skin")) < 1.0 && physiology.Integrity(palm, physiology.TissueIndex(palm, "Muscle")) < 1.0, "the palm's skin or muscle is unhurt");
            Check(physiology.Integrity(palm, physiology.TissueIndex(palm, "Metacarpals")) < 1.0, "the palm's metacarpals were not hurt");
        }),

        ("office slow clock: SlowScale reaches the physiology, so a skin wound's bleed ends (it heals on the acute clock) and the blood regrows sooner the larger the scale: 1 > 20 > 1000 ticks to recover", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var arm = SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left);
            long Recovered(double scale)
            {
                using var s = new OfficeBodySession(p with { SlowScale = scale }, 1);
                Check(s.Wound(Hit(s, arm), new WoundTool(0.001, 0.006)), "the skin wound was refused");
                bool bled = false;
                while (!s.Finished)
                {
                    s.Step();
                    bled |= s.BloodLoss > 0.0;
                    if (bled && s.BloodLoss == 0.0) return s.TickIndex;
                }
                throw new InvalidOperationException($"scale {scale}: the blood never regrew (bled: {bled})");
            }
            long slow = Recovered(1.0), normal = Recovered(20.0), fast = Recovered(1000.0);
            Check(slow - normal > 100 && normal > fast, $"recovered at tick {slow} (scale 1), {normal} (20), {fast} (1000)");
        }),

        ("office read-outs: Will is Lodur's will (the resolve while unwounded, the lesser of resolve and consciousness wounded), Arousal, Resolve and Goal are the rows' at the same tick, AirHunger is 0 unwounded and rises with both lungs cut", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds, SampleEverySeconds = 1.0 };
            using (var s = new OfficeBodySession(p, 1))
            {
                var arousal = new List<double>(); var resolve = new List<double>(); var goal = new List<OfficeGoal>();
                double firstWill = -1, minWill = 1;
                while (!s.Finished)
                {
                    s.Step();
                    if (firstWill < 0) firstWill = s.Will;
                    minWill = Math.Min(minWill, s.Will);
                    Check(s.Will == s.Resolve && s.AirHunger == 0.0, $"tick {s.TickIndex}: will {s.Will}, resolve {s.Resolve}, air hunger {s.AirHunger}");
                    arousal.Add(s.Arousal); resolve.Add(s.Resolve); goal.Add(s.Goal);
                }
                Check(firstWill == 1.0 && minWill == 0.0, $"will began at {firstWill} and fell to {minWill}");
                foreach (var e in s.Result().Timeline)
                {
                    int i = (int)Math.Round(e.TimeMs * OfficeBody.Hz / 1000.0);
                    Check(e.Arousal == arousal[i] && e.Resolve == resolve[i] && e.Goal == goal[i], $"{e.Kind} row at {e.TimeMs} ms: arousal {e.Arousal}, resolve {e.Resolve}, goal {e.Goal}; read-outs {arousal[i]}, {resolve[i]}, {goal[i]}");
                }
                Check(goal.Distinct().Count() == 3 && arousal.Max() > 0.3, "the run did not go through all three goals, or arousal never rose");
            }
            using (var s = new OfficeBodySession(p, 1))
            {
                Check(s.Wound(HitAt(s, SeatedPuppet.Plan.PartIndex("Torso", Lodur.Side.Center), (0f, 0.07f, -0.09f), alongZ: true), new WoundTool(0.065, 15.0)), "the lungs were refused");
                double maxHunger = 0.0;
                while (!s.Finished)
                {
                    s.Step();
                    maxHunger = Math.Max(maxHunger, s.AirHunger);
                    Check(s.Will == Math.Min(s.Resolve, s.Consciousness), $"tick {s.TickIndex}: will {s.Will}, resolve {s.Resolve}, consciousness {s.Consciousness}");
                }
                Check(maxHunger > 0.5, $"air hunger peaked at {maxHunger}");
            }
        }),

        ("office after Finished: Step ticks nothing (the tick index, the result and the event queue stay) and the world still steps (the hands and pelvis keep moving)", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = new OfficeBodySession(p, 1);
            while (!s.Finished) s.Step();
            var buffer = new BodyEvent[64];
            while (s.DrainEvents(buffer) > 0) { }
            long tick = s.TickIndex;
            string result = s.Result().Render();
            double will = s.Will;
            var before = (s.HandL, s.HandR, s.Pelvis);
            for (int i = 0; i < 60; i++) s.Step();
            Check(s.TickIndex == tick && s.Result().Render() == result && s.Will == will && s.DrainEvents(buffer) == 0, "a step after Finished ticked the session");
            Check(s.HandL != before.HandL && s.HandR != before.HandR && s.Pelvis != before.Pelvis, "the world stopped stepping after Finished");
        }),

        ("office wound is refused once the body is dead or the run is finished, and a refused wound is whole or nothing: a tool the table lacks room for in full wounds no layer", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var plan = SeatedPuppet.Plan;
            var arm = plan.PartIndex("LowerArm", Lodur.Side.Left);
            var leg = plan.PartIndex("Shin", Lodur.Side.Left);
            using (var s = new OfficeBodySession(p, 1))
            {
                var legHit = Hit(s, leg);
                Check(s.Wound(Hit(s, arm), WoundTool.Knife), "the knife was refused");
                while (!s.Dead) s.Step();
                Check(!s.Finished, "the run finished at the death");
                Check(!s.Wound(legHit, WoundTool.Knife), "a dead body was wounded");
                while (!s.Finished) s.Step();
                Check(!s.Wound(legHit, WoundTool.Knife), "a finished run was wounded");
            }
            using (var s = new OfficeBodySession(p, 1))
            {
                var legHit = Hit(s, leg);
                while (!s.Finished) s.Step();
                Check(!s.Dead && !s.Wound(legHit, WoundTool.Knife), "a finished, living run was wounded");
            }

            var physiologyPlan = Lodur.PhysiologyPlans.Humanoid();
            var physiology = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
            var session = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), physiology, plan, physiologyPlan);
            var skin = new WoundTool(0.001, 1.0);
            int knives = physiologyPlan.AfflictionSlots / 3;
            for (int i = 0; i < knives; i++) Check(session.Wound(arm, default, WoundTool.Knife), $"knife {i} was refused with room left");
            int free = physiologyPlan.AfflictionSlots - 3 * knives;
            Check(free > 0 && free < 3, $"the table has {free} slots free; the test needs one or two");
            Check(!session.Wound(arm, default, WoundTool.Knife), "a knife that does not fit was accepted");
            for (int i = 0; i < free; i++) Check(session.Wound(arm, default, skin), $"the refused knife took a slot: skin wound {i} was refused");
            Check(!session.Wound(arm, default, skin), "the table took more wounds than it has slots");

            var foreign = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
            var shared = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), foreign, plan, physiologyPlan);
            for (int i = 0; i < physiologyPlan.AfflictionSlots; i++) Check(foreign.Afflict(physiologyPlan.AfflictionIndex("Cut"), arm, 0, 1.0), "the table filled early");
            int refused = foreign.Refused;
            Check(!shared.Wound(arm, default, skin), "a wound the physiology had no room for was accepted");
            Check(foreign.Refused == refused + 1, "the full table's refusal is the physiology's, counted once");

            var healing = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
            var healer = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), healing, plan, physiologyPlan);
            var graze = new WoundTool(0.001, 1e-3);
            for (int i = 0; i < physiologyPlan.AfflictionSlots; i++) Check(healer.Wound(arm, default, graze), $"graze {i} was refused with room left");
            Check(!healer.Wound(arm, default, graze), "the table took more wounds than it has slots");
            var sensed = new float[plan.SensePoints.Count];
            var strengths = new float[plan.Joints.Count];
            var targets = new Lodur.Quat[plan.Joints.Count];
            for (int t = 0; t < 2000 && !healer.Wound(arm, default, graze); t++) healer.Tick(sensed, sensed, sensed, strengths, targets);
            Check(!healer.Finished, "the run ended before the grazes healed");
            Check(healer.Wound(arm, default, graze), "healed wounds did not free their room: the session still counts them");
        }),

        ("SeatedBody hands out no ragdoll: nothing public returns or takes a Ragdoll, so DriveAndStep is the one driver", () =>
        {
            var members = typeof(SeatedBody).GetMembers().Where(m => m.DeclaringType == typeof(SeatedBody)).ToArray();
            Check(members.Length > 0, "no members found");
            foreach (var m in members)
            {
                var types = m switch
                {
                    System.Reflection.PropertyInfo pi => new[] { pi.PropertyType },
                    System.Reflection.MethodBase mb => mb.GetParameters().Select(x => x.ParameterType).Concat(m is System.Reflection.MethodInfo mi ? new[] { mi.ReturnType } : Array.Empty<Type>()).ToArray(),
                    _ => Array.Empty<Type>(),
                };
                Check(!types.Contains(typeof(Lodur.Body.Ymir.Ragdoll)), $"SeatedBody.{m.Name} exposes the Ragdoll");
            }
        }),

        ("unwounded office keeps its will: with no wound the body never dies, DeathCause is None and consciousness is 1 at the end, seeds 1-3", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            for (ulong seed = 1; seed <= 3; seed++)
            {
                using var s = new OfficeBodySession(p, seed);
                while (!s.Finished) s.Step();
                var r = s.Result();
                Check(!s.Dead && s.DeathCause == Lodur.DeathCause.None && r.DiedAtMs is null && r.Outcome != Outcome.Died && s.Consciousness == 1.0, $"seed {seed}: dead {s.Dead}, cause {s.DeathCause}, consciousness {s.Consciousness}");
            }
        }),

        ("office blood is the gain times the spill: one Knife at Hit(s, LowerArm L), 10 s; Emitted + Backlog == 10 x (nominal - BloodVolume) within 1e-9 m^3, the backlog is under one drop, and two runs are equal", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var blood = Lodur.PhysiologyPlans.Humanoid().Blood;
            var bloodParameters = new BloodParameters();
            var arm = SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left);
            var seen = new List<(double Emitted, double Backlog, int Live)>();
            for (int run = 0; run < 2; run++)
            {
                using var s = new OfficeBodySession(p, 1);
                Check(s.Wound(Hit(s, arm), WoundTool.Knife), "the knife was refused");
                for (int t = 0; t < 10 * Hz; t++) s.Step();
                double spilled = blood.NominalVolume * s.BloodLoss * blood.LethalLossFraction; // nominal - BloodVolume
                double visible = bloodParameters.Gain * spilled;
                Check(visible > 0.005, $"only {visible * 1000.0:F1} L was spilled in 10 s");
                Check(Math.Abs(s.BloodEmitted + s.BloodBacklog - visible) < 1e-9, $"emitted {s.BloodEmitted} + backlog {s.BloodBacklog} is not {visible} m^3");
                Check(s.BloodBacklog >= 0.0 && s.BloodBacklog < bloodParameters.DropVolume, $"backlog {s.BloodBacklog} m^3 is not under one drop");
                Console.WriteLine($"  blood: {visible * 1000.0:F2} L visible in 10 s, {s.BloodEmitted / bloodParameters.DropVolume:F0} drops, backlog {s.BloodBacklog * 1e6:F2} mL, live {s.LiveDrops}");
                seen.Add((s.BloodEmitted, s.BloodBacklog, s.LiveDrops));
            }
            Check(seen[0] == seen[1], "two runs shed different blood");
        }),

        ("office blood settles under the cap: six Knife cuts; LiveDrops <= LiveCap - MaxDropsPerTick after every Step, no refused CreateParticles, WetCount > 0 by 15 s, and wet volume + live drops == Emitted within 1e-6 m^3, and the peak live is at most 438 (the geometric mean of the shipped 381 and the cap-only mutant 504)", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var bloodParameters = new BloodParameters();
            using var s = SixCuts(p);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int wetAt15 = -1, peak = 0;
            for (int t = 0; t < 40 * Hz; t++)
            {
                s.Step(); // a refused CreateParticles or Settle throws here
                Check(s.LiveDrops <= bloodParameters.LiveCap - bloodParameters.MaxDropsPerTick, $"tick {t}: {s.LiveDrops} drops live");
                peak = Math.Max(peak, s.LiveDrops);
                if (t == 15 * Hz) wetAt15 = s.View.WetCount;
            }
            clock.Stop();
            Check(wetAt15 > 0, "no wet patch by 15 s");
            // Measured: shipped peak 381, the cap-only-settle mutant 504 (SettleTicks 32 gives 456, not claimed). The bound is their geometric mean, sqrt(381 x 504) = 438, the same factor 1.15 either side.
            Check(peak <= 438, $"{peak} drops were live at once: the cap, not the slow-settle rule, is carrying the blood (probe: 256 live at 300 drops/s)");
            double wet = 0.0;
            var volume = s.View.WetPatchVolume;
            var live = s.View.WetLive;
            for (int i = 0; i < volume.Length; i++) if (live[i] != 0u) wet += volume[i];
            double held = wet + s.LiveDrops * bloodParameters.DropVolume;
            Check(Math.Abs(held - s.BloodEmitted) < 1e-6, $"wet {wet} + live {s.LiveDrops} x drop is {held}, emitted {s.BloodEmitted} m^3");
            Console.WriteLine($"  blood: {s.BloodEmitted * 1000.0:F1} L shed, backlog {s.BloodBacklog * 1000.0:F2} L, peak live {peak}, {s.View.WetCount} wet patches, {clock.Elapsed.TotalMilliseconds / (40 * Hz):F2} ms a tick, the whole session (Soul measured the blood alone at +1.03 ms over the idle particle system at 300 drops/s)");
        }),

        ("office blood lands where it falls: every patch's support is the ground, the stool or a Fella lane, and six cuts give a ground patch (stable id 1) by 40 s", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = SixCuts(p);
            for (int t = 0; t < 40 * Hz; t++) s.Step();
            var view = s.View;
            var ids = view.StableId;
            var patches = new SortedDictionary<ulong, int>();
            for (int i = 0; i < view.WetCount; i++)
            {
                if (view.WetLive[i] == 0u) continue;
                Check(view.WetParticle[i] < 0 && view.WetBody[i] >= 0, $"patch {i} is held by no body ({view.WetBody[i]}, particle {view.WetParticle[i]})");
                ulong id = ids[view.WetBody[i]];
                Check(id == 1 || id == 2 || (id >= SeatedPuppet.FirstStableId && id < SeatedPuppet.FirstStableId + (ulong)SeatedPuppet.Plan.Parts.Count), $"patch {i} sits on stable id {id}");
                patches[id] = patches.GetValueOrDefault(id) + 1;
            }
            Check(patches.GetValueOrDefault(1UL) > 0, "no patch reached the ground");
            Console.WriteLine("  patches per support (stable id): " + string.Join(", ", patches.Select(kv => $"{kv.Key}: {kv.Value}")));
        }),

        ("office blood starts outside the body: the first drop of a cut is, after the step it was shed in, 14.5-30 mm from the picked point: outside the surface by its radius and the lift, and not thrown out of the body (a drop shed on the surface is ejected 50 mm)", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = new OfficeBodySession(p, 1);
            var hit = Hit(s, SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left));
            Check(s.Wound(hit, WoundTool.Knife), "the knife was refused");
            int tick = 0;
            while (s.LiveDrops == 0 && tick++ < Hz) s.Step();
            Check(s.LiveDrops > 0, "no drop in the first second");
            var v = s.View;
            int lane = 0;
            while (v.ParticleLive[lane] == 0u) lane++;
            double d = Math.Sqrt(Math.Pow(v.ParticleX[lane] - hit.Point.X, 2) + Math.Pow(v.ParticleY[lane] - hit.Point.Y, 2) + Math.Pow(v.ParticleZ[lane] - hit.Point.Z, 2));
            Console.WriteLine($"  first drop {d * 1000.0:F1} mm from the picked point after its step (tick {tick}); radius {new BloodParameters().DropRadius * 1000.0:F1} mm");
            Check(d > new BloodParameters().DropRadius + 0.002 && d < 0.03, $"the first drop is {d * 1000.0:F1} mm from the picked point: it started inside the body or was thrown");
        }),

        ("office airway reads the blood: Wound(MouthHit(s), Knife) at tick 0; within 1 s LiquidAtAirway is true on some tick and AirwayLiquid follows, per Lodur's airway", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = new OfficeBodySession(p, 1);
            Check(s.Wound(MouthHit(s), WoundTool.Knife), "the knife was refused at the mouth");
            int atAirway = -1, liquid = -1;
            double maxLiquid = 0.0, maxOcclusion = 0.0;
            for (int t = 0; t < Hz; t++)
            {
                s.Step();
                if (atAirway < 0 && s.LiquidAtAirway) atAirway = t;
                if (liquid < 0 && s.AirwayLiquid > 0.0) liquid = t;
                maxLiquid = Math.Max(maxLiquid, s.AirwayLiquid);
                maxOcclusion = Math.Max(maxOcclusion, s.Occlusion);
            }
            Check(atAirway >= 0, "no liquid was ever at the airway in the first second");
            Check(liquid >= 0, "liquid was at the airway but none reached it");
            Console.WriteLine($"  airway: liquid at the opening from tick {atAirway}, in the airway from tick {liquid}, peak {maxLiquid * 1e6:F2} mL, occlusion {maxOcclusion:F3}");
        }),

        ("office throat click reaches the larynx and fills the airway: HitAt(Head, the front of the throat), Wound(hit, Knife) at tick 0 puts more than 20 mL in the airway within 5 s, occludes it fully before the death, and the death is BloodLoss", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = new OfficeBodySession(p, 1);
            var head = SeatedPuppet.Plan.PartIndex("Head", Lodur.Side.Center);
            Check(s.Wound(HitAt(s, head, (0f, -0.0778f, 0.0778f)), WoundTool.Knife), "the knife was refused at the throat");
            double liquidAt5 = 0.0, maxOcclusion = 0.0, occludedAt = -1.0;
            while (!s.Dead && !s.Finished)
            {
                s.Step();
                if (s.TickIndex <= 5 * Hz) liquidAt5 = Math.Max(liquidAt5, s.AirwayLiquid);
                maxOcclusion = Math.Max(maxOcclusion, s.Occlusion);
                if (occludedAt < 0.0 && s.Occlusion >= 0.99) occludedAt = s.TickIndex / (double)Hz;
            }
            Console.WriteLine($"  throat: {liquidAt5 * 1e6:F1} mL in the airway by 5 s, full at {occludedAt:F1} s, died at {s.TickIndex / (double)Hz:F1} s");
            Check(liquidAt5 > 20e-6, $"the airway held {liquidAt5 * 1e6:F2} mL by 5 s");
            Check(occludedAt >= 0.0 && maxOcclusion >= 0.99, $"the airway never filled: occlusion peaked at {maxOcclusion:F3}");
            Check(s.Dead && s.DeathCause == Lodur.DeathCause.BloodLoss, $"cause {s.DeathCause}");
        }),

        ("seated frame on a turned head: ToPart returns the point's part-frame coordinates and an opened wound sheds along the normal in that frame, with the neck driven so the head is 0.2+ rad off identity", () =>
        {
            var plan = SeatedPuppet.Plan;
            int head = plan.PartIndex("Head", Lodur.Side.Center);
            int neck = Array.FindIndex(plan.Joints.ToArray(), j => j.Label == "Neck");
            Check(neck >= 0, "no neck joint");
            using var body = new SeatedBody(new BloodParameters { SettleSpeed = 100f, SettleTicks = 1 });
            body.Strengths[neck] = 1f;
            body.Targets[neck] = Quat.FromAxisAngle(1f, 0f, 0f, 0.6f);
            int lane = body.BodyLane(head);
            float turn = 0f;
            for (int t = 0; t < 4 * Hz && turn < 0.5f; t++)
            {
                body.DriveAndStep(1.0 / Hz);
                turn = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(body.View.RotationW[lane])));
            }
            Console.WriteLine($"  head turned {turn:F3} rad");
            Check(turn >= 0.2f, $"the head only turned {turn:F3} rad: the test would not see the frame");
            var v = body.View;
            var q = new Quat(v.RotationX[lane], v.RotationY[lane], v.RotationZ[lane], v.RotationW[lane]);
            var local = (X: 0.03f, Y: 0.03f, Z: 0.1f); // on the face, clear of the torso the turned chin meets
            q.Rotate(local.X, local.Y, local.Z, out var ox, out var oy, out var oz);
            var world = new BodyPoint(v.PositionX[lane] + ox, v.PositionY[lane] + oy, v.PositionZ[lane] + oz);
            var back = body.ToPart(head, world);
            Check(Math.Abs(back.X - local.X) < 1e-4f && Math.Abs(back.Y - local.Y) < 1e-4f && Math.Abs(back.Z - local.Z) < 1e-4f, $"ToPart gave ({back.X}, {back.Y}, {back.Z}) for the part-frame point ({local.X}, {local.Y}, {local.Z})");

            // The wound: picked from outside the throat, opened with its world normal; the first drop leaves along that normal (in the world), not along the normal read as a part-frame vector.
            q.Rotate(0f, 0f, -1f, out var dx, out var dy, out var dz); // the head's own front, travelling in
            var hit = body.Pick(new BodyPoint(world.X - 0.5f * dx, world.Y - 0.5f * dy, world.Z - 0.5f * dz), new BodyPoint(dx, dy, dz));
            Check(hit is { } h && h.Part == head, $"the ray at the throat met part {hit?.Part} at {hit?.Point} first, not the head");
            var pick = hit!.Value;
            body.OpenWound(head, pick.Point, pick.Normal);
            var bleeds = new double[plan.Parts.Count];
            bleeds[head] = 0.0003;
            int tick = 0;
            while (body.LiveDrops == 0 && tick++ < Hz) body.DriveAndStep(1.0 / Hz, bleeds);
            Check(body.LiveDrops > 0, "no drop in the first second");
            var w = body.View;
            int drop = 0;
            while (w.ParticleLive[drop] == 0u) drop++;
            float ex = w.ParticleX[drop] - pick.Point.X, ey = w.ParticleY[drop] - pick.Point.Y, ez = w.ParticleZ[drop] - pick.Point.Z;
            float cos = (ex * pick.Normal.X + ey * pick.Normal.Y + ez * pick.Normal.Z) / MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            Console.WriteLine($"  first drop leaves at cos {cos:F3} to the surface normal");
            Check(cos > 0.95f, $"the first drop left at cos {cos:F3} to the normal: the wound normal was not taken into the part's frame");
        }),

        ("office reach by point: the throat front cuts the larynx and not the neck muscle; the chin cuts the muscle and not the larynx", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var plan = SeatedPuppet.Plan;
            var physiologyPlan = Lodur.PhysiologyPlans.Humanoid();
            int head = plan.PartIndex("Head", Lodur.Side.Center);
            foreach (var (point, larynx, muscle) in new[] { ((0f, -0.0778f, 0.0778f), true, false), ((0f, -0.11f, 0f), false, true) })
            {
                var physiology = new Lodur.Physiology(plan, physiologyPlan, p.SlowScale);
                var session = new StressSession(p, 1, plan.SensePoints.Count, OfficeBody.Poses, plan.SenseIndex("Head"), physiology, plan, physiologyPlan);
                Check(session.Wound(head, point, WoundTool.Knife), "the knife was refused");
                var pts = new float[plan.SensePoints.Count];
                session.Tick(pts, pts, pts, new float[plan.Joints.Count], new Quat[plan.Joints.Count]);
                bool cutLarynx = physiology.Integrity(head, physiology.TissueIndex(head, "Larynx")) < 1.0;
                bool cutMuscle = physiology.Integrity(head, physiology.TissueIndex(head, "Muscle")) < 1.0;
                Check(cutLarynx == larynx && cutMuscle == muscle, $"at {point}: larynx cut {cutLarynx}, muscle cut {cutMuscle}");
            }
        }),

        ("WoundTool refuses a depth that is 0, negative, NaN or infinite, and the knife is 2 cm", () =>
        {
            foreach (double depth in new[] { 0.0, -0.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                bool threw = false;
                try { _ = new WoundTool(depth, 60.0); } catch (ArgumentException) { threw = true; }
                Check(threw, $"a depth of {depth} was accepted");
            }
            Check(WoundTool.Knife.Depth == 0.02 && WoundTool.Knife.Severity == 60.0, "the knife changed");
        }),

        ("office blood is conserved to death and the death tick's blood is shed: one Knife (and three on one point), run to Finished, Emitted + Backlog == Gain x the real spill within 1e-9 m^3 (the dying step bleeds, though Bleed reads 0 once dead)", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var blood = Lodur.PhysiologyPlans.Humanoid().Blood;
            foreach (int cuts in new[] { 1, 3 })
            {
                using var s = new OfficeBodySession(p, 1);
                var hit = Hit(s, SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left));
                for (int i = 0; i < cuts; i++) Check(s.Wound(hit, WoundTool.Knife), "the knife was refused");
                while (!s.Finished) s.Step();
                Check(s.Dead, "the body lived");
                double visible = new BloodParameters().Gain * blood.NominalVolume * s.BloodLoss * blood.LethalLossFraction;
                double shed = s.BloodEmitted + s.BloodBacklog;
                Check(Math.Abs(shed - visible) < 1e-9, $"{cuts} cut(s): shed {shed * 1000.0:F4} L of {visible * 1000.0:F4} L");
            }
        }),

        ("office blood stops when the run does: a living body still bleeding at Finished sheds nothing in the next 300 steps (the physiology no longer steps, so its last rates would run forever)", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            using var s = new OfficeBodySession(p, 1);
            Check(s.Wound(Hit(s, SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left)), new WoundTool(WoundTool.Knife.Depth, 3.0)), "the wound was refused");
            double before = 0.0;
            while (!s.Finished) { s.Step(); if (s.TickIndex == 100) before = s.BloodEmitted; }
            Check(!s.Dead && s.BloodEmitted > before + 1e-3, "the wound was not bleeding at the end of the run");
            double shed = s.BloodEmitted + s.BloodBacklog;
            for (int t = 0; t < 300; t++) s.Step();
            Check(s.BloodEmitted + s.BloodBacklog == shed, $"{(s.BloodEmitted + s.BloodBacklog - shed) * 1e6:F3} mL more blood after Finished");
        }),

        ("office blood never throws or flies at repeated clicks: the same PartHit cut five or six times (lower arm, upper arm, thigh) and six coincident cuts on a shin run 20 s with every drop under 12 m/s, no refused Settle, and wet volume + live drops == Emitted within 1e-6 m^3", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var drop = new BloodParameters().DropVolume;
            foreach (var (label, cuts) in new[] { ("LowerArm", 5), ("UpperArm", 6), ("Thigh", 6), ("Shin", 6) })
            {
                using var s = new OfficeBodySession(p, 1);
                var hit = Hit(s, SeatedPuppet.Plan.PartIndex(label, Lodur.Side.Left));
                for (int i = 0; i < cuts; i++) Check(s.Wound(hit, WoundTool.Knife), $"{label} cut {i} was refused");
                double fastest = 0.0;
                int peakWet = 0;
                for (int t = 0; t < 20 * Hz; t++)
                {
                    s.Step(); // a refused Settle or CreateParticles throws here
                    var v = s.View;
                    for (int i = 0; i < v.ParticleCount; i++)
                    {
                        if (v.ParticleLive[i] == 0u) continue;
                        fastest = Math.Max(fastest, Math.Sqrt(v.ParticleVx[i] * v.ParticleVx[i] + v.ParticleVy[i] * v.ParticleVy[i] + v.ParticleVz[i] * v.ParticleVz[i]));
                    }
                    int wets = 0;
                    for (int i = 0; i < v.WetCount; i++) if (v.WetLive[i] != 0u) wets++;
                    peakWet = Math.Max(peakWet, wets);
                }
                double wet = 0.0;
                for (int i = 0; i < s.View.WetCount; i++) if (s.View.WetLive[i] != 0u) wet += s.View.WetPatchVolume[i];
                Console.WriteLine($"  {label} x{cuts}: fastest drop {fastest:F2} m/s, peak {peakWet} wet patches, {s.BloodEmitted * 1000.0:F1} L shed, backlog {s.BloodBacklog * 1000.0:F2} L, live {s.LiveDrops}");
                Check(fastest < 12.0, $"{label} x{cuts}: a drop flew at {fastest:F1} m/s");
                Check(Math.Abs(wet + s.LiveDrops * drop - s.BloodEmitted) < 1e-6, $"{label} x{cuts}: wet {wet} + live {s.LiveDrops} drops is not emitted {s.BloodEmitted} m^3");
            }
        }),

        ("blood never asks for a lane the world has not freed: LiveCap 24 and no calm settling (SettleTicks 100000), six wounds shedding 6 drops a tick for 1200 ticks, neither CreateParticles nor Settle refuses and the live drops stay at most 24", () =>
        {
            var parameters = new BloodParameters { LiveCap = 24, SettleTicks = 100000 };
            using var body = new SeatedBody(parameters);
            var plan = SeatedPuppet.Plan;
            var bleeds = new double[plan.Parts.Count];
            foreach (var label in new[] { "LowerArm", "Thigh", "Shin" })
            {
                foreach (var side in new[] { Lodur.Side.Left, Lodur.Side.Right })
                {
                    int part = plan.PartIndex(label, side);
                    var c = plan.Parts[part].Position;
                    PartHit? hit = null;
                    foreach (var (dx, dy, dz) in new[] { (1f, 0f, 0f), (-1f, 0f, 0f), (0f, 1f, 0f), (0f, -1f, 0f), (0f, 0f, 1f), (0f, 0f, -1f) })
                    {
                        if (body.Pick(new BodyPoint(c.X - dx, c.Y - dy, c.Z - dz), new BodyPoint(dx, dy, dz)) is { } h && h.Part == part) { hit = h; break; }
                    }
                    Check(hit is not null, $"no hit on {label} {side}");
                    body.OpenWound(part, hit!.Value.Point, hit.Value.Normal);
                    bleeds[part] = 6.0 * parameters.DropVolume * Hz / parameters.Gain / 6.0;
                }
            }
            int peak = 0;
            for (int t = 0; t < 1200; t++)
            {
                body.DriveAndStep(1.0 / Hz, bleeds); // a refused CreateParticles or Settle throws here
                peak = Math.Max(peak, body.LiveDrops);
            }
            Check(peak <= 24, $"{peak} drops were live under a cap of 24");
            Check(body.BloodEmitted > 24 * parameters.DropVolume, "the cap was never reached: the test pins nothing");
        }),

        ("office blood keeps pace with the bleed at coincident and near-coincident cuts: five clicks on one PartHit and six points 1 cm apart on a lower arm; the unshed backlog never passes 0.42 L (shipped 0.08-0.09 L, a degraded spread 1.9 L and more) and, two ticks after the death, nothing more than a drop a point is left to shed", () =>
        {
            var p = new StressParameters { TickSeconds = OfficeBody.TickSeconds };
            var parameters = new BloodParameters();
            var arm = SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left);
            foreach (var spread in new[] { 0.0, 0.01 })
            {
                using var s = new OfficeBodySession(p, 1);
                var hits = new List<PartHit> { Hit(s, arm) };
                if (spread == 0.0) { while (hits.Count < 5) hits.Add(hits[0]); }
                else
                {
                    var c = SeatedPuppet.Plan.Parts[arm].Position;
                    for (float u = -0.1f; u <= 0.1f && hits.Count < 6; u += 0.0025f)
                    {
                        // Rays along -Z... from the front, sliding up the arm: a point on the arm at least 1 cm from each chosen one.
                        if (s.Pick(new BodyPoint(c.X, c.Y + u, c.Z + 1f), new BodyPoint(0f, 0f, -1f)) is { } h && h.Part == arm &&
                            hits.All(o => Math.Sqrt(Math.Pow(o.Point.X - h.Point.X, 2) + Math.Pow(o.Point.Y - h.Point.Y, 2) + Math.Pow(o.Point.Z - h.Point.Z, 2)) >= 0.01)) hits.Add(h);
                    }
                    Check(hits.Count == 6, $"only {hits.Count} points 1 cm apart");
                }
                foreach (var h in hits) Check(s.Wound(h, WoundTool.Knife), "the knife was refused");
                // Measured peak backlog: shipped 0.078 L (5 coincident clicks) and 0.094 L (6 points 1 cm apart); with the spread cut to 2 and 3 spots, 4.24 and 1.86 L at 1 cm; with no spread 14.84 and 7.36 L.
                // One bound for both, 0.42 L = sqrt(0.094 x 1.86), the geometric mean of the shipped worst (0.094 L) and the least degraded claimed reading (3 spots at 1 cm, 1.86 L); 2 spots gave 4.24 L, no spread 14.84 and 7.36 L.
                double bound = 0.42e-3, worst = 0.0, atDeath = -1.0;
                int deadAt = -1;
                for (int t = 0; !s.Finished; t++)
                {
                    s.Step();
                    worst = Math.Max(worst, s.BloodBacklog);
                    if (s.Dead && deadAt < 0) deadAt = t;
                    if (deadAt >= 0 && t == deadAt + 2) atDeath = s.BloodEmitted;
                }
                Console.WriteLine($"  {hits.Count} points, spread {spread}: peak backlog {worst * 1000.0:F2} L (bound {bound * 1000.0:F2}), shed {s.BloodEmitted * 1000.0:F1} L, backlog at the end {s.BloodBacklog * 1e6:F1} mL");
                Check(worst <= bound, $"{hits.Count} points, spread {spread}: the backlog peaked at {worst * 1000.0:F2} L, over {bound * 1000.0:F2} L");
                Check(atDeath >= 0.0 && s.BloodEmitted - atDeath < 2.0 * parameters.DropVolume, $"{hits.Count} points: {(s.BloodEmitted - atDeath) * 1e6:F1} mL was still being shed after the death");
                Check(s.BloodBacklog < hits.Count * parameters.DropVolume, $"{hits.Count} points: {s.BloodBacklog * 1e6:F1} mL was never shed");
            }
        }),

        ("blood makes a surface slippery: the seated body's blood material (BloodParameters.ApplyTo, the one SeatedBody uses) lets a pushed box slide at least 5 cm further over a settled drop than over a dry floor", () =>
        {
            var parameters = new BloodParameters();
            double Slide(bool patch)
            {
                float drop = 0.03f, scale = drop / parameters.DropRadius;
                using var world = new global::Ymir.Box3D.Box3DSoaWorld(capacity: 8, gravityY: -9.81f,
                    particles: new global::Ymir.Box3D.Box3DSoaParticleCapacities(8, 0, 0, 0, 0, parameters.Material + 1, Fluid: 8, Wets: 4));
                world.AddStaticBox(1, (0f, -0.5f, 0f), (4f, 0.5f, 2f), friction: 0.6f);
                world.SetMaterial(0, 0.6f);
                parameters.ApplyTo(world);
                world.CreateParticles(new[] { new global::Ymir.Box3D.Box3DSoaParticleDesc((0f, drop, 0f), (float)(parameters.Density * Math.Pow(2.0 * drop, 3.0)), drop,
                    global::Ymir.Box3D.Box3DSoaPhase.Of(1, fluid: true), (uint)parameters.Material) });
                for (int t = 0; t < 20; t++) world.Step(1f / 60f, 4);
                var handle = new global::Ymir.Box3D.Box3DSoaParticleHandle(0, world.View.ParticleGeneration[0]);
                if (patch) world.Settle(new[] { handle }); else world.DestroyParticles(new[] { handle });
                int box = world.AddBox(10, (0f, 0.1f, 0f), (0.1f, 0.1f, 0.1f), 5f / 0.008f, friction: 0.6f);
                double x0 = world.View.PositionX[box];
                for (int t = 0; t < 40; t++) { world.View.ForceX[box] = 20f; world.Step(1f / 60f, 4); } // below the dry friction (29 N), above the wet one
                return world.View.PositionX[box] - x0;
            }
            double dry = Slide(false), wet = Slide(true);
            Console.WriteLine($"  slide over 40 ticks: dry {dry * 100.0:F1} cm, over blood {wet * 100.0:F1} cm");
            Check(wet - dry >= 0.05, $"the box slid {wet * 100.0:F1} cm over blood and {dry * 100.0:F1} cm dry");
        }),

        ("seated airway pins: the pooled liquid reaches Lodur (a wound above the mouth settling on the face reads Pooled), down is the world's gravity (an upright mouth has no drop), and the blood material is slippery (wet friction 0.1, friction 0.3)", () =>
        {
            var plan = SeatedPuppet.Plan;
            var mouth = plan.AirwayOpenings.First(a => a.Label == "Mouth");
            var c = plan.Parts[mouth.Part].Position;
            using var body = new SeatedBody(new BloodParameters { SettleSpeed = 100f, SettleTicks = 1 }); // drops settle on first touch: they pool where they land
            Check(body.ReadAirway().Drop == 0.0, $"an upright seated mouth has a drop of {body.ReadAirway().Drop} m");
            Check(body.View.MaterialWetFriction[1] == 0.1f && body.View.MaterialFriction[1] == 0.3f, $"material 1: friction {body.View.MaterialFriction[1]}, wet friction {body.View.MaterialWetFriction[1]}");
            var hit = body.Pick(new BodyPoint(c.X, c.Y + mouth.Offset.Y + 0.04f, c.Z + 1f), new BodyPoint(0f, 0f, -1.5f));
            Check(hit is { } h && h.Part == mouth.Part, "no hit on the face above the mouth");
            body.OpenWound(hit!.Value.Part, hit.Value.Point, hit.Value.Normal);
            var bleeds = new double[plan.Parts.Count];
            bleeds[hit.Value.Part] = 0.0003;
            double pooled = 0.0;
            for (int t = 0; t < 300; t++)
            {
                body.DriveAndStep(1.0 / Hz, bleeds);
                pooled = Math.Max(pooled, body.ReadAirway().Pooled);
            }
            Check(pooled > 1e-5, $"the pooled liquid at the mouth peaked at {pooled} m^3");
            Console.WriteLine($"  airway: pooled peak {pooled * 1e6:F1} mL");
        }),

        ("seated world identity: a SeatedBody (a particle system, no wound) steps to the same positions and rotations as the same world with no particle system, 1200 driven ticks", () =>
        {
            using var body = new SeatedBody();
            var needs = SeatedPuppet.Needs;
            using var control = new global::Ymir.Box3D.Box3DSoaWorld(capacity: needs.Bodies, jointCapacity: needs.Joints);
            var ragdoll = SeatedPuppet.Build(control);
            var strengths = new float[SeatedPuppet.Plan.Joints.Count];
            var targets = new Quat[strengths.Length];
            for (int i = 0; i < strengths.Length; i++) { strengths[i] = 0.4f; targets[i] = Quat.FromAxisAngle(0f, 0f, 1f, 0.05f * (i % 5)); body.Strengths[i] = strengths[i]; body.Targets[i] = targets[i]; }
            for (int t = 0; t < 1200; t++)
            {
                ragdoll.Drive(strengths, targets);
                control.Step(1f / Hz, SeatedPuppet.Substeps);
                body.DriveAndStep(1.0 / Hz);
                var a = body.View;
                var b = control.View;
                Check(a.Count == b.Count, "the lane counts differ");
                for (int i = 0; i < a.Count; i++)
                {
                    Check(a.PositionX[i] == b.PositionX[i] && a.PositionY[i] == b.PositionY[i] && a.PositionZ[i] == b.PositionZ[i] &&
                          a.RotationX[i] == b.RotationX[i] && a.RotationY[i] == b.RotationY[i] && a.RotationZ[i] == b.RotationZ[i] && a.RotationW[i] == b.RotationW[i],
                        $"tick {t}, lane {i}: the particle world moved a body");
                }
            }
        }),
    };

    private static readonly float[] Point = new float[1], Strengths = new float[1];
    private static readonly Quat[] Targets = new Quat[1];

    /// <summary>An office session with six Knife cuts at tick 0: both lower arms, both thighs, both shins.</summary>
    private static OfficeBodySession SixCuts(StressParameters p)
    {
        var plan = SeatedPuppet.Plan;
        var s = new OfficeBodySession(p, 1);
        try
        {
            foreach (var label in new[] { "LowerArm", "Thigh", "Shin" })
            {
                foreach (var side in new[] { Lodur.Side.Left, Lodur.Side.Right })
                {
                    Check(s.Wound(Hit(s, plan.PartIndex(label, side)), WoundTool.Knife), $"the knife was refused on {label} {side}");
                }
            }
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A hit on <paramref name="part"/>: rays from 1 m outside the part's rest centre along -X, +X, -Y, +Y, -Z, +Z toward it, the first that meets that part first.
    /// Taken at the start of a run, while the body is where the plan puts it.
    /// </summary>
    private static PartHit Hit(OfficeBodySession s, int part)
    {
        var c = SeatedPuppet.Plan.Parts[part].Position;
        foreach (var (dx, dy, dz) in new[] { (1f, 0f, 0f), (-1f, 0f, 0f), (0f, 1f, 0f), (0f, -1f, 0f), (0f, 0f, 1f), (0f, 0f, -1f) })
        {
            var hit = s.Pick(new BodyPoint(c.X - dx, c.Y - dy, c.Z - dz), new BodyPoint(dx, dy, dz));
            if (hit is { } h && h.Part == part) return h;
        }
        throw new InvalidOperationException($"no ray from outside meets part {part} first");
    }

    /// <summary>
    /// A hit on <paramref name="part"/> at <paramref name="local"/> (the part's frame) as it now stands: a ray from 0.5 m outside that point, in along the
    /// outward direction (or along +z from behind when <paramref name="alongZ"/>), on through it; the ray must meet the part first.
    /// </summary>
    private static PartHit HitAt(OfficeBodySession s, int part, (float X, float Y, float Z) local, bool alongZ = false)
    {
        var v = s.View;
        ulong id = SeatedPuppet.FirstStableId + (ulong)part;
        int lane = -1;
        for (int i = 0; i < v.StableId.Length; i++) if (v.StableId[i] == id) { lane = i; break; }
        Check(lane >= 0, $"part {part} has no lane");
        var q = new Quat(v.RotationX[lane], v.RotationY[lane], v.RotationZ[lane], v.RotationW[lane]);
        q.Rotate(local.X, local.Y, local.Z, out var ox, out var oy, out var oz);
        float wx = v.PositionX[lane] + ox, wy = v.PositionY[lane] + oy, wz = v.PositionZ[lane] + oz;
        float dx = 0f, dy = 0f, dz = 1f;
        if (!alongZ)
        {
            float len = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
            dx = -ox / len; dy = -oy / len; dz = -oz / len; // travelling inward
        }
        var hit = s.Pick(new BodyPoint(wx - 0.5f * dx, wy - 0.5f * dy, wz - 0.5f * dz), new BodyPoint(dx, dy, dz));
        Check(hit is { } h && h.Part == part, $"a ray at part {part} met something else first");
        return hit!.Value;
    }

    /// <summary>A hit on the head at the plan's Mouth opening: a ray from 1 m in front of it, straight back and on through it.</summary>
    private static PartHit MouthHit(OfficeBodySession s)
    {
        var plan = SeatedPuppet.Plan;
        var mouth = plan.AirwayOpenings.First(a => a.Label == "Mouth");
        var c = plan.Parts[mouth.Part].Position;
        var at = new BodyPoint(c.X + mouth.Offset.X, c.Y + mouth.Offset.Y, c.Z + mouth.Offset.Z);
        var ray = s.Pick(new BodyPoint(at.X, at.Y, at.Z + 1f), new BodyPoint(0f, 0f, -1.5f));
        Check(ray is { } h && h.Part == mouth.Part, "a ray at the mouth did not meet the head first");
        return ray!.Value;
    }

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
