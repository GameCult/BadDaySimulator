using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BadDay.Core;

// Grid sweep: stimulus intensity (rows) x withdrawal-reflex suppression strength
// (columns, PainBoxParameters.MaxSuppression). Each cell runs Seeds consecutive
// seeds and prints the median time-to-break in seconds over the runs that broke,
// "never" if none broke within MaxSeconds, and "(k/N)" when only k of N broke.
// Engine-free and deterministic: same build, same table, byte for byte.
const int Seeds = 9;
const ulong FirstSeed = 1;
double[] intensities = { 0.2, 0.3, 0.4, 0.5, 0.6, 0.8, 1.0 };
double[] suppressions = { 0.0, 0.25, 0.5, 0.75, 0.95, 1.0 };

var inv = CultureInfo.InvariantCulture;
var sb = new StringBuilder();
sb.Append($"time to break, s (median over {Seeds} seeds from {FirstSeed}; max {new PainBoxParameters().MaxSeconds:F0} s; pull {new PainBoxParameters().ObjectivePull:F2})\n");
sb.Append("intensity\\suppr ");
foreach (var s in suppressions) sb.Append(s.ToString("F2", inv).PadLeft(10));
sb.Append('\n');
foreach (var i in intensities)
{
    sb.Append(i.ToString("F2", inv).PadRight(16));
    foreach (var s in suppressions)
    {
        var p = new PainBoxParameters { BoxIntensity = i, MaxSuppression = s };
        var sweep = SweepSummary.Run(p, FirstSeed, Seeds);
        string cell = sweep.MedianBreakMs is { } ms
            ? (ms / 1000.0).ToString("F1", inv) + (sweep.Broke < Seeds ? $"({sweep.Broke}/{Seeds})" : "")
            : "never";
        sb.Append(cell.PadLeft(10));
    }
    sb.Append('\n');
}
Console.Write(sb.ToString());
