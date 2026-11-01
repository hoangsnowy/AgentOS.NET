// The perf gate (.github/workflows/perf.yml) fails a run on > 20 % Mean regression or a new allocation on a
// zero-allocation benchmark. These tests pin that contract and check the committed baseline stays readable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentOs.Benchmarks.Comparison;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Benchmarks;

public class BaselineComparerTests
{
    private const string Report = """
        {
          "Title": "AgentOs.Benchmarks.Llm.CostCalculatorBenchmarks-20261101",
          "HostEnvironmentInfo": { "ProcessorName": "Test CPU" },
          "Benchmarks": [
            {
              "FullName": "AgentOs.Benchmarks.Llm.CostCalculatorBenchmarks.ExactId",
              "Statistics": { "Mean": 41.5, "Median": 41.0 },
              "Memory": { "Gen0Collections": 0, "BytesAllocatedPerOperation": 0 }
            },
            {
              "FullName": "AgentOs.Benchmarks.Text.UntrustedTextBenchmarks.Body4K",
              "Statistics": { "Mean": 5200.25 },
              "Memory": { "BytesAllocatedPerOperation": 16440 }
            },
            {
              "FullName": "AgentOs.Benchmarks.Broken.DidNotRun",
              "Statistics": null
            }
          ]
        }
        """;

    private static Dictionary<string, BenchmarkMeasurement> Set(params BenchmarkMeasurement[] rows) =>
        rows.ToDictionary(r => r.FullName, StringComparer.Ordinal);

    [Fact]
    public void ReadReport_BriefJson_ReadsMeanAndAllocation()
    {
        var rows = BaselineComparer.ReadReport(Report);

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new BenchmarkMeasurement("AgentOs.Benchmarks.Llm.CostCalculatorBenchmarks.ExactId", 41.5, 0));
        rows[1].MeanNs.ShouldBe(5200.25);
        rows[1].AllocatedBytes.ShouldBe(16440);
    }

    [Fact]
    public void ReadReport_NotABenchmarkReport_ReturnsEmpty()
    {
        BaselineComparer.ReadReport("""{ "name": "package.json" }""").ShouldBeEmpty();
        BaselineComparer.ReadReport("[1, 2, 3]").ShouldBeEmpty();
    }

    [Fact]
    public void ReadReport_NoMemoryDiagnoser_AllocationIsNull()
    {
        var rows = BaselineComparer.ReadReport("""{ "Benchmarks": [ { "FullName": "A.B", "Statistics": { "Mean": 3.0 } } ] }""");

        rows.ShouldHaveSingleItem().AllocatedBytes.ShouldBeNull();
    }

    [Fact]
    public void Compare_WithinThreshold_IsOk()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 1_000, 120)), Set(new BenchmarkMeasurement("A.B", 1_150, 120)));

        rows.ShouldHaveSingleItem().Verdict.ShouldBe(ComparisonVerdict.Ok);
    }

    [Fact]
    public void Compare_QuadraticRegression_FailsAsSlower()
    {
        // A deliberate O(n²) rescan turns a 1 µs fold into 50 µs.
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("Fold.ByRole", 1_000, 2_048)), Set(new BenchmarkMeasurement("Fold.ByRole", 50_000, 2_048)));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(ComparisonVerdict.Slower);
        row.IsFailure.ShouldBeTrue();
        row.Ratio.ShouldBe(50.0);
    }

    [Fact]
    public void Compare_JustOverTwentyPercent_FailsAsSlower()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 1_000, null)), Set(new BenchmarkMeasurement("A.B", 1_201, null)));

        rows.ShouldHaveSingleItem().Verdict.ShouldBe(ComparisonVerdict.Slower);
    }

    [Fact]
    public void Compare_LargeRatioButBelowNoiseFloor_IsOk()
    {
        // 3 ns → 4.5 ns is +50 % but within timer/JIT noise.
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 3.0, 0)), Set(new BenchmarkMeasurement("A.B", 4.5, 0)));

        rows.ShouldHaveSingleItem().Verdict.ShouldBe(ComparisonVerdict.Ok);
    }

    [Fact]
    public void Compare_ZeroAllocationBaselineStartsAllocating_FailsAsNewAllocation()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 40, 0)), Set(new BenchmarkMeasurement("A.B", 40, 24)));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(ComparisonVerdict.NewAllocation);
        row.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Compare_AllocatingBaselineAllocatesMore_IsNotAnAllocationFailure()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 40, 100)), Set(new BenchmarkMeasurement("A.B", 40, 200)));

        rows.ShouldHaveSingleItem().Verdict.ShouldBe(ComparisonVerdict.Ok);
    }

    [Fact]
    public void Compare_FasterBeyondThreshold_IsImprovedNotFailure()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 1_000, 64)), Set(new BenchmarkMeasurement("A.B", 400, 0)));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(ComparisonVerdict.Improved);
        row.IsFailure.ShouldBeFalse();
    }

    [Fact]
    public void Compare_NewAndMissingBenchmarks_AreInformational()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("Old.Gone", 10, 0)), Set(new BenchmarkMeasurement("New.One", 10, 0)));

        rows.Select(r => (r.FullName, r.Verdict)).ShouldBe([("New.One", ComparisonVerdict.New), ("Old.Gone", ComparisonVerdict.Missing)]);
        rows.ShouldAllBe(r => !r.IsFailure);
    }

    [Fact]
    public void Compare_CustomThreshold_IsHonoured()
    {
        var options = new ComparisonOptions(MaxRegression: 0.50);

        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 1_000, null)), Set(new BenchmarkMeasurement("A.B", 1_400, null)), options);

        rows.ShouldHaveSingleItem().Verdict.ShouldBe(ComparisonVerdict.Ok);
    }

    [Fact]
    public void ToMarkdown_WithFailure_SaysFailAndListsTheFailureFirst()
    {
        var options = new ComparisonOptions();
        var rows = BaselineComparer.Compare(
            Set(new BenchmarkMeasurement("A.Fast", 10_000, 0), new("Z.Slow", 1_000, 0)),
            Set(new BenchmarkMeasurement("A.Fast", 10_000, 0), new("Z.Slow", 2_000, 0)),
            options);

        var md = BaselineComparer.ToMarkdown(rows, options);

        md.ShouldContain("FAIL — 1 regression(s)");
        md.IndexOf("`Z.Slow`", StringComparison.Ordinal).ShouldBeLessThan(md.IndexOf("`A.Fast`", StringComparison.Ordinal));
        md.ShouldContain("2.00×");
    }

    [Theory]
    [InlineData(12.34, "12.3 ns")]
    [InlineData(4_560.0, "4.56 µs")]
    [InlineData(7_890_000.0, "7.89 ms")]
    public void Time_FormatsByMagnitude(double ns, string expected) => BaselineComparer.Time(ns).ShouldBe(expected);

    [Fact]
    public void ReadDirectory_CommittedBaseline_HasEveryBenchmarkWithAllocations()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Join(root, "AgentOs.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }
        root.ShouldNotBeNull("repository root (AgentOs.slnx) not found above the test output");

        var baseline = BaselineComparer.ReadDirectory(Path.Join(root, "tests", "AgentOs.Benchmarks", "baseline"));

        baseline.Count.ShouldBeGreaterThan(30);
        baseline.Values.ShouldAllBe(m => m.MeanNs > 0 && m.AllocatedBytes != null);
    }
}
