using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BadDay.Core;

namespace BadDay.Tests;

public static class Program
{
    private static readonly PainBoxParameters Default = new();

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
                PainBoxSimulation.Run(Default with { ObjectivePull = pull }, 5).TimeToBreakMs ?? long.MaxValue;
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
