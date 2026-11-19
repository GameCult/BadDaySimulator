using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BadDay.Core;
using BadDay.Ymir;

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
            Check(a.Run.Render() == b.Run.Render() && a.HandOutMs == b.HandOutMs && a.Excursions == b.Excursions, "body runs differ");
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

            Check(r.HandOutMs is { } o && o - broke <= 300, $"hand out at {r.HandOutMs}, will broke at {broke}");
            Check(r.Run.EndMs == r.HandOutMs, "the run should end when the hand is out");
        }),

        ("body: switching the box off at 5 s ends the run with the will intact and the hand still in the box", () =>
        {
            var r = BodyPainBox.Run(OnBody with { BoxDisableAtSeconds = 5.0 }, 1);
            Check(r.Run.Outcome == Outcome.BoxDisabled && r.Run.TimeToBreakMs is null, $"outcome {r.Run.Outcome}");
            Check(r.HandOutMs is null && r.Hand[^1].InBox, "the hand left the box");
            Check(r.Run.Timeline.Last().Resolve > 0.0, "resolve should be above zero");
        }),

        ("body: holding costs strain and strain shows: hand speed before the break exceeds hand speed after the ramp", () =>
        {
            var r = BodyPainBox.Run(OnBody, 1);
            int breakTick = Tick(r.Run.TimeToBreakMs!.Value);
            double early = SpeedRms(r, 2 * Hz, 4 * Hz), late = SpeedRms(r, breakTick - 2 * Hz, breakTick);
            Check(late > early, $"speed RMS {late:F4} m/s in the 2 s before the break, {early:F4} m/s in the 2 s after the ramp");
        }),

        ("body: a box at intensity 0 for 10 s: no flinch, no break, the hand holds still", () =>
        {
            var r = BodyPainBox.Run(OnBody with { BoxIntensity = 0.0, MaxSeconds = 10.0 }, 1);
            Check(r.Run.Outcome == Outcome.Endured && r.Run.Flinches == 0 && r.Run.TimeToBreakMs is null, r.Run.Summary());
            for (int i = 3 * Hz; i < r.Hand.Count; i++) Check(Dist(r.Hand[i]) <= 0.023f, $"drift {Dist(r.Hand[i]):F3} m at tick {i}");
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
                Check(r.Run.Outcome == Outcome.WillBroke, $"seed {seed}: outcome {r.Run.Outcome}, break {r.Run.TimeToBreakMs}, hand out {r.HandOutMs}");
                var withdrew = r.Run.Timeline.Single(e => e.Kind == EntryKind.Withdrew);
                Check(withdrew.TimeMs - r.Run.TimeToBreakMs == 100, $"seed {seed}: Withdrew row {withdrew.TimeMs - r.Run.TimeToBreakMs} ms after the break");
                Check(r.Run.EndMs >= withdrew.TimeMs && (r.HandOutMs is null || r.Run.EndMs >= r.HandOutMs), $"seed {seed}: ended before the withdrawal or the hand-out");
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
    };

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

    public static int Main()
    {
        int failed = 0;
        foreach (var (name, body) in Tests)
        {
            try { body(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        Console.WriteLine($"{Tests.Length - failed}/{Tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
