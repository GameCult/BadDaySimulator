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
            Check(broke.Resolve == 0.0 && broke.Goal == Goal.Withdraw, "break entry should show resolve 0, goal Withdraw");
            Check(out_.TimeMs - broke.TimeMs == 100, $"latency {out_.TimeMs - broke.TimeMs} ms");
            Check(r.Timeline.Where(e => e.TimeMs < broke.TimeMs).All(e => e.Resolve > 0.0 && e.Goal == Goal.HoldHand),
                "resolve hit zero or goal changed before the recorded break");
        }),

        ("a stronger box breaks the will sooner", () =>
        {
            long Break(double intensity) =>
                PainBoxSimulation.Run(Default with { BoxIntensity = intensity }, 5).TimeToBreakMs ?? long.MaxValue;
            Check(Break(1.0) < Break(0.8) && Break(0.8) < Break(0.6), "break times not ordered by intensity");
        }),

        ("a stronger objective pull holds the will longer", () =>
        {
            long Break(double pull) =>
                PainBoxSimulation.Run(Default with { Drive = Default.Drive with { ObjectivePull = pull } }, 5).TimeToBreakMs ?? long.MaxValue;
            Check(Break(0.0) < Break(0.5) && Break(0.5) < Break(0.8), "break times not ordered by pull");
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
                var full = BodyPainBox.Run(OnBody, seed);
                var control = BodyPainBox.Run(OnBody with { Hold = new Lodur.HoldParameters { FlinchGain = 0.0 } }, seed);
                Check(full.Run.Flinches == control.Run.Flinches, $"seed {seed}: the nervous state changed with the arm");
                var flinches = full.Run.Timeline.Where(e => e.Kind == EntryKind.Flinch).Select(e => Tick(e.TimeMs)).ToList();
                float Gap(int i) => MathF.Sqrt(MathF.Pow(full.Hand[i].X - control.Hand[i].X, 2) + MathF.Pow(full.Hand[i].Y - control.Hand[i].Y, 2) + MathF.Pow(full.Hand[i].Z - control.Hand[i].Z, 2));
                float after = flinches.Max(f => Enumerable.Range(f, Hz / 5 + 1).Max(Gap));
                float before = Enumerable.Range(0, flinches[0]).Max(Gap);
                Check(after > 0.023f, $"seed {seed}: the largest gap from the control after a flinch is {after:F4} m");
                Check(before < after / 2, $"seed {seed}: the gap before the first flinch is {before:F4} m, after {after:F4} m");
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
            Check(Cause.Office.Senses.Threshold == 0.05 && Cause.Office.Senses.SensitisationGrowth == 0.0 && Cause.Office.Senses.ConductionDelaySeconds == 0.0,
                "the office does not pass its own sense parameters");
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
            long lodurDefault = MedianBreak(new StressParameters { Cause = Cause.Office with { Senses = new Lodur.NerveParameters() } }, 1, 20);
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
            }
            Check(controlSounded, "the control printer never sounded, so the check proved nothing");
            var rows = silenced.Result().Timeline.Where(e => e.Kind == StressEntryKind.Silenced).ToArray();
            Check(rows.Length == 1 && rows[0].TimeMs == 20000, "expected exactly one Silenced row at 20000 ms");
            bool threw = false;
            try { silenced.Silence("no-such-stressor"); } catch (ArgumentException) { threw = true; }
            Check(threw, "an unknown stressor id was accepted");
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

        ("the pain box is untouched: run --seed 1 and sweep --seed 1 --count 20 hash to the output captured before the office existed", () =>
        {
            string Hash(params string[] a)
            {
                var w = new StringWriter();
                BadDay.Cli.Program.Execute(a, w);
                var bytes = System.Text.Encoding.UTF8.GetBytes(w.ToString().Replace("\r\n", "\n"));
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            }
            Check(Hash("run", "--seed", "1") == "a3a800612f1e610deb8c648ea74a0a3e2f4639541a46d92af0d1bcfbdd8cfb45", "run --seed 1 changed");
            Check(Hash("sweep", "--seed", "1", "--count", "20") == "2cc88f9ffbbf301b8264c0be600edb34b21a3b572f927ed96ea49eda20dc73db", "sweep --seed 1 --count 20 changed");
        }),
    };

    private static readonly Quat[] Pose = { Quat.Identity };
    private static readonly float[] Point = new float[1], Strengths = new float[1];
    private static readonly Quat[] Targets = new Quat[1];

    private static StressSession NewOffice(StressParameters p, ulong seed) => new(p, seed, 1, Pose, Pose);

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
