using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BadDay.Core;
using BadDay.Ymir;

namespace BadDay.Cli;

public static class Program
{
    private const string Usage = """
        baddaysim: the Synaptic Induction Chamber (the pain box)

        baddaysim run   --seed N [options]        one run, full timeline
        baddaysim run   --seed N --body [options] the same run on the Ymir ragdoll, with the hand-out time
        baddaysim sweep --seed N --count M [options]   seeds N..N+M-1, break-time summary

        baddaysim office       --seed N [office options]   the office: stressors fill the stress bar, the will breaks, the rage follows
        baddaysim office       --seed N --body [office options]   the same run on the Ymir ragdoll: rage time and strikes
        baddaysim office-sweep --seed N --count M [office options]

        options (all numbers, seconds unless noted):
          --intensity X   box intensity 0..1            (default 0.8)
          --pay DOLLARS   what the volunteer is paid: the pull is pay / (pay + 100)  (default 100, pull 0.5)
          --disable-at S  box switches off at S         (default never)
          --max S         give up after S               (default 120)
          --every S       timeline sample interval      (default 1)

        office options:
          --silence ID@S  silence stressor ID from S seconds (repeatable; ids: printer, ceiling-light, desk-phone, desktop-interrupt)
          --scale X       every stressor's intensity times X  (default 1)
          --pull X        the job's pull 0..1           (default 0.6)
          --max S         give up after S               (default 180)
          --every S       timeline sample interval      (default 1)
        """;

    private const string Title = "Synaptic Induction Chamber (the pain box)";

    private static readonly string OfficeTitle = $"Bad day at the office, rung 4 (stress = arousal, \"{StressResult.StressBarLabel}\")";

    public static int Main(string[] args)
    {
        try { return Execute(args, Console.Out); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    public static int Execute(string[] args, System.IO.TextWriter output)
    {
        if (args.Length == 0 || (args[0] != "run" && args[0] != "sweep" && args[0] != "office" && args[0] != "office-sweep"))
            throw new ArgumentException("expected 'run', 'sweep', 'office' or 'office-sweep'");
        var opts = ParseOptions(args);
        ulong seed = opts.TryGetValue("seed", out var s) ? ulong.Parse(s, CultureInfo.InvariantCulture)
            : throw new ArgumentException("--seed is required");
        if (args[0].StartsWith("office")) return ExecuteOffice(args[0] == "office-sweep", seed, opts, output);
        if (opts.ContainsKey("pull")) throw new ArgumentException("--pull applies to office; the chamber takes --pay");
        var p = new PainBoxParameters
        {
            BoxIntensity = Num(opts, "intensity", 0.8),
            Payment = Num(opts, "pay", 100),
            BoxDisableAtSeconds = opts.ContainsKey("disable-at") ? Num(opts, "disable-at", 0) : null,
            MaxSeconds = Num(opts, "max", 120),
            SampleEverySeconds = Num(opts, "every", 1),
        };

        if (args[0] == "run" && opts.ContainsKey("body"))
        {
            var b = BodyPainBox.Run(p with { TickSeconds = BodyPainBox.TickSeconds }, seed);
            output.WriteLine(Title);
            output.Write(b.Run.Render());
            output.WriteLine(b.Run.Summary());
            output.WriteLine(b.Run.HandOutMs is { } out_
                ? $"hand out at {out_} ms, {out_ - b.Run.TimeToBreakMs} ms after the will broke; {b.Excursions} excursions from the box before the break"
                : $"hand not out for good by {b.Run.EndMs} ms; {b.Excursions} excursions from the box before the break");
            return 0;
        }
        if (opts.ContainsKey("body")) throw new ArgumentException("--body applies to run");

        if (args[0] == "run")
        {
            var r = PainBoxSimulation.Run(p, seed);
            output.WriteLine(Title);
            output.Write(r.Render());
            output.WriteLine(r.Summary());
            return 0;
        }

        int count = (int)Num(opts, "count", 20);
        var sweep = SweepSummary.Run(p, seed, count);
        foreach (var r in sweep.Results) output.WriteLine(r.Summary());
        output.WriteLine($"runs {sweep.Runs}: broke {sweep.Broke}, box disabled {sweep.BoxDisabled}, endured {sweep.Endured}; " +
                         $"time to break min/median/max ms: {Fmt(sweep.MinBreakMs)}/{Fmt(sweep.MedianBreakMs)}/{Fmt(sweep.MaxBreakMs)}");
        return 0;
    }

    private static int ExecuteOffice(bool sweep, ulong seed, Dictionary<string, string> opts, System.IO.TextWriter output)
    {
        if (opts.ContainsKey("body") && sweep) throw new ArgumentException("--body applies to run and office");
        if (opts.ContainsKey("pay")) throw new ArgumentException("--pay applies to run and sweep; the office takes --pull");
        var defaults = new StressParameters();
        var p = defaults with
        {
            Cause = (defaults.Cause with { ObjectivePull = Num(opts, "pull", defaults.Cause.ObjectivePull) }).Scaled(Num(opts, "scale", 1.0)),
            SampleEverySeconds = Num(opts, "every", defaults.SampleEverySeconds),
        };
        double seconds = Num(opts, "max", 180); // the headless run's horizon: the office session has no end of its own
        if (opts.ContainsKey("body") && opts.ContainsKey("silence")) throw new ArgumentException("--silence does not apply to --body");
        var silences = new List<SilenceCommand>();
        if (opts.TryGetValue("silence", out var list))
        {
            foreach (var item in list.Split(','))
            {
                int at = item.LastIndexOf('@');
                if (at <= 0) throw new ArgumentException($"--silence wants ID@SECONDS, got '{item}'");
                silences.Add(new SilenceCommand(item[..at], double.Parse(item[(at + 1)..], CultureInfo.InvariantCulture)));
            }
        }

        if (opts.ContainsKey("body"))
        {
            var b = OfficeBody.Run(p with { TickSeconds = OfficeBody.TickSeconds }, seed, seconds);
            output.WriteLine(OfficeTitle);
            output.Write(b.Run.Render());
            output.WriteLine(b.Run.Summary());
            if (b.RageTick is { } rage)
            {
                int end = b.HandL.Count;
                StressEntry? calm = b.Run.Timeline.Any(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm)
                    ? b.Run.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Calm) : null;
                int rageEnd = calm is null ? end : (int)Math.Round(calm.Value.TimeMs * OfficeBody.Hz / 1000.0);
                string how = calm is null ? "clipped by --max, no calm" : $"to calm at {calm.Value.TimeMs} ms";
                output.WriteLine($"rage from {b.Run.Timeline.First(e => e.Kind == StressEntryKind.GoalChanged && e.Goal == OfficeGoal.Rage).TimeMs} ms for " +
                                 $"{((rageEnd - rage) / (double)OfficeBody.Hz).ToString("F1", CultureInfo.InvariantCulture)} s ({how}); strikes {b.Strikes(rage, end)} " +
                                 $"(left {OfficeBody.CountStrikes(b.HandL, rage, end)}, right {OfficeBody.CountStrikes(b.HandR, rage, end)}), " +
                                 $"{b.Strikes(0, rage)} before it");
            }
            else if (b.Run.Outcome == Outcome.WillBroke) output.WriteLine($"the will broke but --max stopped the run at {b.Run.EndMs} ms, before the rage took over");
            else output.WriteLine($"no rage: the will held to {b.Run.EndMs} ms");
            return 0;
        }

        if (!sweep)
        {
            var r = StressSimulation.Run(p, seed, seconds, silences);
            output.WriteLine(OfficeTitle);
            output.Write(r.Render());
            output.WriteLine(r.Summary());
            return 0;
        }

        int count = (int)Num(opts, "count", 20);
        var summary = StressSweep.Run(p, seed, count, seconds, silences);
        foreach (var r in summary.Results) output.WriteLine(r.Summary());
        output.WriteLine($"runs {summary.Runs}: broke {summary.Broke}, endured {summary.Endured}; " +
                         $"time to break min/median/max ms: {Fmt(summary.MinBreakMs)}/{Fmt(summary.MedianBreakMs)}/{Fmt(summary.MaxBreakMs)}; " +
                         $"min resolve of endured runs: {(summary.MinEnduredResolve?.ToString("F3", CultureInfo.InvariantCulture) ?? "-")}");
        return 0;
    }

    private static string Fmt(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static double Num(Dictionary<string, string> o, string key, double fallback) =>
        o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string>();
        for (int i = 1; i < args.Length;)
        {
            if (args[i] == "--body") { o["body"] = ""; i++; continue; } // the one flag
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException($"bad option near '{args[i]}'");
            // --silence repeats; its values are joined, since a stressor id holds no comma.
            o[args[i][2..]] = args[i] == "--silence" && o.TryGetValue("silence", out var prior) ? prior + "," + args[i + 1] : args[i + 1];
            i += 2;
        }
        return o;
    }
}
