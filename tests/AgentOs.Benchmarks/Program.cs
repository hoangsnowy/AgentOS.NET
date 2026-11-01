// Entry point. Two modes:
//   dotnet run -c Release --project tests/AgentOs.Benchmarks -- [BenchmarkDotNet args, e.g. --filter '*Cost*']
//   dotnet run -c Release --project tests/AgentOs.Benchmarks -- compare <baselineDir> <resultsDir>
//       [--threshold 0.20] [--noise-floor-ns 2] [--summary <file>]
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

BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, new BenchmarkConfig());
return 0;

static int Compare(string[] args)
{
    var positional = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal)
        && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal))).ToArray();
    if (positional.Length != 2)
    {
        Console.Error.WriteLine("usage: compare <baselineDir> <resultsDir> [--threshold 0.20] [--noise-floor-ns 2] [--summary <file>]");
        return 2;
    }

    var options = new ComparisonOptions(
        MaxRegression: Option(args, "--threshold") is { } t ? double.Parse(t, CultureInfo.InvariantCulture) : 0.20,
        NoiseFloorNs: Option(args, "--noise-floor-ns") is { } f ? double.Parse(f, CultureInfo.InvariantCulture) : 2.0);

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
    return rows.Any(r => r.IsFailure) ? 1 : 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
