// Entry point. Two modes:
//   dotnet run -c Release --project tests/AgentOs.Benchmarks -- [BenchmarkDotNet args, e.g. --filter '*Cost*']
//   dotnet run -c Release --project tests/AgentOs.Benchmarks -- compare <baselineDir> <resultsDir>
//       [--threshold 0.20|20%] [--noise-floor-ns 2] [--alloc-floor-bytes 64] [--timing gate|advisory] [--require-all]
//       [--summary <file>]
// Run mode exits 1 when any benchmark failed to build/run or a validator raised a critical error.
// The compare mode is the perf gate (.github/workflows/perf.yml): exit 0 = pass, 1 = regression, 2 = usage/no data.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AgentOs.Benchmarks;
using AgentOs.Benchmarks.Comparison;
using BenchmarkDotNet.Running;

if (args.Length > 0 && string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase))
{
    return Compare(args[1..]);
}

var summaries = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, new BenchmarkConfig()).ToArray();
var failed = summaries.Where(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)).Select(s => s.Title).ToArray();
if (failed.Length > 0)
{
    Console.Error.WriteLine($"{failed.Length} benchmark run(s) had a failed benchmark or a critical validation error: {string.Join(", ", failed)}");
    return 1;
}
return 0;

static int Compare(string[] args)
{
    const string Usage = "usage: compare <baselineDir> <resultsDir> [--threshold 0.20|20%] [--noise-floor-ns 2] "
        + "[--alloc-floor-bytes 64] [--timing gate|advisory] [--require-all] [--summary <file>]";
    var positional = ComparerArguments.Positional(args);
    if (positional.Length != 2)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    if (!ComparerArguments.TryParseThreshold(Option(args, "--threshold"), out var threshold, out var thresholdError))
    {
        Console.Error.WriteLine(thresholdError);
        return 2;
    }
    var noiseFloor = 2.0;
    if (Option(args, "--noise-floor-ns") is { } f
        && (!double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out noiseFloor) || !double.IsFinite(noiseFloor) || noiseFloor < 0))
    {
        Console.Error.WriteLine($"--noise-floor-ns must be a non-negative number of nanoseconds, got '{f}'.");
        return 2;
    }
    var allocFloor = 64L;
    if (Option(args, "--alloc-floor-bytes") is { } a
        && (!long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out allocFloor) || allocFloor < 0))
    {
        Console.Error.WriteLine($"--alloc-floor-bytes must be a non-negative whole number of bytes, got '{a}'.");
        return 2;
    }
    var timing = Option(args, "--timing") ?? "gate";
    if (timing is not ("gate" or "advisory"))
    {
        Console.Error.WriteLine($"--timing must be 'gate' or 'advisory', got '{timing}'.");
        return 2;
    }

    var options = new ComparisonOptions(
        MaxRegression: threshold,
        NoiseFloorNs: noiseFloor,
        AllocationFloorBytes: allocFloor,
        TimingAdvisory: timing == "advisory",
        RequireAll: args.Contains("--require-all", StringComparer.Ordinal));

    var baseline = BaselineComparer.ReadDirectory(positional[0]);
    var current = BaselineComparer.ReadDirectory(positional[1]);
    if (baseline.Count == 0 || current.Count == 0)
    {
        Console.Error.WriteLine($"No benchmark data: baseline {baseline.Count} row(s) in '{positional[0]}', current {current.Count} row(s) in '{positional[1]}'.");
        return 2;
    }

    var rows = BaselineComparer.Compare(baseline, current, options);
    var markdown = BaselineComparer.ToMarkdown(rows, options);
    Console.WriteLine(markdown);
    if (Option(args, "--summary") is { Length: > 0 } summary)
    {
        File.AppendAllText(summary, markdown);
    }
    if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
    {
        // Workflow-command annotations: advisory timing regressions surface as warnings on the run, failures as errors.
        foreach (var r in rows.Where(r => r.IsFailure || r.Verdict == ComparisonVerdict.SlowerAdvisory))
        {
            var level = r.IsFailure ? "error" : "warning";
            var ratio = r.Ratio is { } x ? x.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "n/a";
            Console.WriteLine($"::{level}::{ComparerArguments.EscapeAnnotation($"{r.FullName}: {r.Verdict} (mean ratio {ratio})")}");
        }
    }
    return rows.Any(r => r.IsFailure) ? 1 : 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
