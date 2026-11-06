using System;
using System.Collections.Generic;
using System.Globalization;
using BadDay.Core;

namespace BadDay.Cli;

public static class Program
{
    private const string Usage = """
        baddaysim run   --seed N [options]        one run, full timeline
        baddaysim sweep --seed N --count M [options]   seeds N..N+M-1, break-time summary

        options (all numbers, seconds unless noted):
          --intensity X   box intensity 0..1            (default 0.8)
          --pull X        objective pull 0..1           (default 0.5)
          --disable-at S  box switches off at S         (default never)
          --max S         give up after S               (default 120)
          --every S       timeline sample interval      (default 1)
        """;

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
        if (args.Length == 0 || (args[0] != "run" && args[0] != "sweep")) throw new ArgumentException("expected 'run' or 'sweep'");
        var opts = ParseOptions(args);
        ulong seed = opts.TryGetValue("seed", out var s) ? ulong.Parse(s, CultureInfo.InvariantCulture)
            : throw new ArgumentException("--seed is required");
        var p = new PainBoxParameters
        {
            BoxIntensity = Num(opts, "intensity", 0.8),
            ObjectivePull = Num(opts, "pull", 0.5),
            BoxDisableAtSeconds = opts.ContainsKey("disable-at") ? Num(opts, "disable-at", 0) : null,
            MaxSeconds = Num(opts, "max", 120),
            SampleEverySeconds = Num(opts, "every", 1),
        };

        if (args[0] == "run")
        {
            var r = PainBoxSimulation.Run(p, seed);
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

    private static string Fmt(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static double Num(Dictionary<string, string> o, string key, double fallback) =>
        o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException($"bad option near '{args[i]}'");
            o[args[i][2..]] = args[i + 1];
        }
        return o;
    }
}
