// The perf gate behind .github/workflows/perf.yml: compares a BenchmarkDotNet run against the committed baseline
// (tests/AgentOs.Benchmarks/baseline/*.json — the BenchmarkDotNet "brief" JSON reports) and fails when a benchmark
// allocates more than its baseline (0 B → anything, or growth beyond the threshold plus a byte floor), crashed, or —
// unless timing is advisory (the nightly) — got slower than the threshold (default 20 % on Mean).
// Pure BCL + System.Text.Json so the unit tests compile this file directly (AgentOs.Tests links it) without
// pulling BenchmarkDotNet into the test run.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AgentOs.Benchmarks.Comparison;

/// <summary>One benchmark's numbers as read from a BenchmarkDotNet JSON report.</summary>
/// <param name="FullName">BenchmarkDotNet's stable id: <c>Namespace.Type.Method(Param: value)</c>.</param>
/// <param name="MeanNs">Mean time per operation, nanoseconds.</param>
/// <param name="AllocatedBytes">Bytes allocated per operation, or null when the run had no memory diagnoser.</param>
/// <param name="Failed">True when the report lists the benchmark but it produced no statistics (it crashed or was
/// rejected by a validator); <paramref name="MeanNs"/> is then <see cref="double.NaN"/>.</param>
internal sealed record BenchmarkMeasurement(string FullName, double MeanNs, long? AllocatedBytes, bool Failed = false)
{
    /// <summary>A benchmark the report lists without statistics.</summary>
    public static BenchmarkMeasurement FailedRun(string fullName) => new(fullName, double.NaN, null, Failed: true);
}

/// <summary>Why a benchmark row is flagged.</summary>
internal enum ComparisonVerdict
{
    /// <summary>Within the threshold.</summary>
    Ok,

    /// <summary>Faster than the baseline by more than the threshold (informational — consider re-recording).</summary>
    Improved,

    /// <summary>Mean grew by more than the threshold.</summary>
    Slower,

    /// <summary>Mean grew by more than the threshold, but timing is advisory for this run (a warning, not a failure).</summary>
    SlowerAdvisory,

    /// <summary>The baseline allocated 0 B per operation and this run allocates.</summary>
    NewAllocation,

    /// <summary>Allocated bytes per operation grew by more than the threshold plus the byte floor.</summary>
    MoreAllocation,

    /// <summary>The benchmark is in this run's report but has no statistics — it crashed or failed validation.</summary>
    Failed,

    /// <summary>In this run but not in the baseline (informational).</summary>
    New,

    /// <summary>In the baseline but not in this run (filtered out or removed). Informational for a filtered run; a
    /// failure when <see cref="ComparisonOptions.RequireAll"/> is set (the full suite must cover the baseline).</summary>
    Missing,
}

/// <summary>One compared row.</summary>
internal sealed record ComparisonRow(
    string FullName, BenchmarkMeasurement? Baseline, BenchmarkMeasurement? Current, ComparisonVerdict Verdict)
{
    /// <summary>Current / baseline mean, or null when either side is missing.</summary>
    public double? Ratio => Baseline is { MeanNs: > 0 } b && Current is { Failed: false } c ? c.MeanNs / b.MeanNs : null;

    /// <summary>True when this row fails the gate. <see cref="ComparisonVerdict.Missing"/> rows are only produced as
    /// failures under <see cref="ComparisonOptions.RequireAll"/>; see <see cref="IsMissingFailure"/>.</summary>
    public bool IsFailure => Verdict is ComparisonVerdict.Slower or ComparisonVerdict.NewAllocation
        or ComparisonVerdict.MoreAllocation or ComparisonVerdict.Failed || IsMissingFailure;

    /// <summary>True for a <see cref="ComparisonVerdict.Missing"/> row of a run that had to cover the whole baseline.</summary>
    public bool IsMissingFailure { get; init; }
}

/// <summary>Gate settings.</summary>
/// <param name="MaxRegression">Allowed relative Mean growth (0.20 = 20 %).</param>
/// <param name="NoiseFloorNs">Absolute Mean growth always tolerated — a 3 ns → 4 ns move is timer/JIT noise, not a
/// regression, even though it is +33 %.</param>
/// <param name="AllocationFloorBytes">Absolute allocation growth always tolerated on a benchmark that already allocates
/// (an extra small object from a runtime update is not a regression). A 0 B baseline has no floor.</param>
/// <param name="TimingAdvisory">When true a Mean regression is reported as <see cref="ComparisonVerdict.SlowerAdvisory"/>
/// (a warning) instead of failing — for runs on hardware that differs from the baseline's (the nightly on a hosted
/// runner). Allocations are machine-independent and always gate.</param>
/// <param name="RequireAll">When true a baseline benchmark absent from the run fails the gate (an unfiltered run must
/// cover the whole baseline, so a benchmark that crashed before writing its report cannot pass silently).</param>
internal sealed record ComparisonOptions(
    double MaxRegression = 0.20,
    double NoiseFloorNs = 2.0,
    long AllocationFloorBytes = 64,
    bool TimingAdvisory = false,
    bool RequireAll = false);

/// <summary>Reads BenchmarkDotNet JSON reports and applies the regression gate.</summary>
internal static class BaselineComparer
{
    /// <summary>Read every benchmark from the BenchmarkDotNet JSON reports in <paramref name="directory"/>
    /// (<c>*.json</c>, recursively). Files that are not BenchmarkDotNet reports are ignored.</summary>
    public static IReadOnlyDictionary<string, BenchmarkMeasurement> ReadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var result = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (var m in ReadReport(File.ReadAllText(file)))
            {
                result[m.FullName] = m;
            }
        }
        return result;
    }

    /// <summary>Parse one BenchmarkDotNet JSON report (brief or full). Non-report JSON yields nothing.</summary>
    public static IReadOnlyList<BenchmarkMeasurement> ReadReport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks)
            || benchmarks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<BenchmarkMeasurement>(benchmarks.GetArrayLength());
        foreach (var b in benchmarks.EnumerateArray())
        {
            if (!b.TryGetProperty("FullName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || nameEl.GetString() is not { Length: > 0 } name)
            {
                continue;
            }
            if (!b.TryGetProperty("Statistics", out var stats) || stats.ValueKind != JsonValueKind.Object
                || !stats.TryGetProperty("Mean", out var meanEl) || !meanEl.TryGetDouble(out var mean))
            {
                list.Add(BenchmarkMeasurement.FailedRun(name));   // listed but no statistics: it crashed or was rejected
                continue;
            }
            long? allocated = b.TryGetProperty("Memory", out var memory) && memory.ValueKind == JsonValueKind.Object
                && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytesEl) && bytesEl.TryGetInt64(out var bytes)
                ? bytes
                : null;
            list.Add(new BenchmarkMeasurement(name, mean, allocated));
        }
        return list;
    }

    /// <summary>Compare <paramref name="current"/> to <paramref name="baseline"/>. Rows come back sorted by name.</summary>
    public static IReadOnlyList<ComparisonRow> Compare(
        IReadOnlyDictionary<string, BenchmarkMeasurement> baseline,
        IReadOnlyDictionary<string, BenchmarkMeasurement> current,
        ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        options ??= new ComparisonOptions();

        var rows = new List<ComparisonRow>(baseline.Count + current.Count);
        foreach (var (name, now) in current)
        {
            if (!baseline.TryGetValue(name, out var was))
            {
                rows.Add(new ComparisonRow(name, null, now, now.Failed ? ComparisonVerdict.Failed : ComparisonVerdict.New));
                continue;
            }
            rows.Add(new ComparisonRow(name, was, now, Judge(was, now, options)));
        }
        foreach (var (name, was) in baseline)
        {
            if (!current.ContainsKey(name))
            {
                rows.Add(new ComparisonRow(name, was, null, ComparisonVerdict.Missing) { IsMissingFailure = options.RequireAll });
            }
        }
        rows.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return rows;
    }

    private static ComparisonVerdict Judge(BenchmarkMeasurement was, BenchmarkMeasurement now, ComparisonOptions options)
    {
        if (now.Failed)
        {
            return ComparisonVerdict.Failed;
        }
        if (was.AllocatedBytes is { } wasBytes && now.AllocatedBytes is { } nowBytes)
        {
            if (wasBytes == 0 && nowBytes > 0)
            {
                return ComparisonVerdict.NewAllocation;
            }
            if (nowBytes - wasBytes > wasBytes * options.MaxRegression + options.AllocationFloorBytes)
            {
                return ComparisonVerdict.MoreAllocation;
            }
        }
        var delta = now.MeanNs - was.MeanNs;
        if (delta > was.MeanNs * options.MaxRegression && delta > options.NoiseFloorNs)
        {
            return options.TimingAdvisory ? ComparisonVerdict.SlowerAdvisory : ComparisonVerdict.Slower;
        }
        if (-delta > was.MeanNs * options.MaxRegression && -delta > options.NoiseFloorNs)
        {
            return ComparisonVerdict.Improved;
        }
        return ComparisonVerdict.Ok;
    }

    /// <summary>A GitHub-flavoured markdown report: failures first, then everything else.</summary>
    public static string ToMarkdown(IReadOnlyList<ComparisonRow> rows, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        var ic = CultureInfo.InvariantCulture;
        var failures = rows.Count(r => r.IsFailure);
        var sb = new StringBuilder();
        sb.Append("## Benchmark comparison\n\n");
        sb.Append(ic, $"Gate: allocated bytes may grow at most {options.MaxRegression:P0} + {options.AllocationFloorBytes} B ")
          .Append("(a benchmark whose baseline allocates 0 B must stay at 0 B); ")
          .Append(ic, $"Mean may grow at most {options.MaxRegression:P0} (and more than {options.NoiseFloorNs:0.#} ns)")
          .Append(options.TimingAdvisory ? " — **advisory on this run** (warning only, the hardware differs from the baseline's)" : "")
          .Append(options.RequireAll ? "; every baseline benchmark must run" : "")
          .Append("; a benchmark that crashes fails.\n\n");
        sb.Append(failures == 0 ? "**Result: pass**" : string.Create(ic, $"**Result: FAIL — {failures} regression(s)**")).Append("\n\n");
        sb.Append("| Verdict | Benchmark | Baseline mean | Current mean | Ratio | Baseline alloc | Current alloc |\n");
        sb.Append("|---|---|---:|---:|---:|---:|---:|\n");
        foreach (var r in rows.OrderBy(r => r.IsFailure ? 0 : 1).ThenBy(r => r.FullName, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(r.IsMissingFailure ? "❌ not run" : Label(r.Verdict))
              .Append(" | `").Append(r.FullName.Replace("|", "\\|", StringComparison.Ordinal)).Append('`')
              .Append(" | ").Append(Time(r.Baseline?.MeanNs))
              .Append(" | ").Append(r.Current is { Failed: true } ? "crashed" : Time(r.Current?.MeanNs))
              .Append(" | ").Append(r.Ratio is { } ratio ? ratio.ToString("0.00", ic) + "×" : "—")
              .Append(" | ").Append(Bytes(r.Baseline?.AllocatedBytes))
              .Append(" | ").Append(Bytes(r.Current?.AllocatedBytes))
              .Append(" |\n");
        }
        return sb.ToString();
    }

    private static string Label(ComparisonVerdict v) => v switch
    {
        ComparisonVerdict.Slower => "❌ slower",
        ComparisonVerdict.SlowerAdvisory => "⚠️ slower (advisory)",
        ComparisonVerdict.NewAllocation => "❌ allocates",
        ComparisonVerdict.MoreAllocation => "❌ allocates more",
        ComparisonVerdict.Failed => "❌ failed to run",
        ComparisonVerdict.Improved => "✅ faster",
        ComparisonVerdict.New => "🆕 new",
        ComparisonVerdict.Missing => "— not run",
        _ => "ok",
    };

    internal static string Time(double? ns) => ns switch
    {
        null => "—",
        < 1_000 => ns.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ns",
        < 1_000_000 => (ns.Value / 1_000).ToString("0.00", CultureInfo.InvariantCulture) + " µs",
        _ => (ns.Value / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " ms",
    };

    internal static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => bytes.Value.ToString(CultureInfo.InvariantCulture) + " B",
        _ => (bytes.Value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
    };
}
