// The perf gate (.github/workflows/perf.yml) fails a run on allocation growth (0 B → anything, or > 20 % + 64 B), a
// crashed benchmark, a missing one on a full run, or — unless timing is advisory — a > 20 % Mean regression.
// These tests pin that contract and check the committed baseline stays readable.

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

        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(new BenchmarkMeasurement("AgentOs.Benchmarks.Llm.CostCalculatorBenchmarks.ExactId", 41.5, 0));
        rows[1].MeanNs.ShouldBe(5200.25);
        rows[1].AllocatedBytes.ShouldBe(16440);
        rows[2].FullName.ShouldBe("AgentOs.Benchmarks.Broken.DidNotRun");
        rows[2].Failed.ShouldBeTrue();
    }

    [Fact]
    public void Compare_BenchmarkWithoutStatistics_FailsAsFailed()
    {
        var current = BaselineComparer.ReadReport(Report).ToDictionary(r => r.FullName, StringComparer.Ordinal);
        var baseline = Set(new BenchmarkMeasurement("AgentOs.Benchmarks.Broken.DidNotRun", 100, 0));

        var row = BaselineComparer.Compare(baseline, current).Single(r => r.FullName == "AgentOs.Benchmarks.Broken.DidNotRun");

        row.Verdict.ShouldBe(ComparisonVerdict.Failed);
        row.IsFailure.ShouldBeTrue();
        row.Ratio.ShouldBeNull();
        BaselineComparer.ToMarkdown([row], new ComparisonOptions()).ShouldContain("crashed");
    }

    [Fact]
    public void Compare_NewBenchmarkThatCrashed_FailsAsFailed()
    {
        var rows = BaselineComparer.Compare(Set(), Set(BenchmarkMeasurement.FailedRun("New.Broken")));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(ComparisonVerdict.Failed);
        row.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Compare_MissingOnAFullRun_Fails()
    {
        var rows = BaselineComparer.Compare(
            Set(new BenchmarkMeasurement("Old.Gone", 10, 0)), Set(new BenchmarkMeasurement("Kept.One", 10, 0)), new ComparisonOptions(RequireAll: true));

        var missing = rows.Single(r => r.FullName == "Old.Gone");
        missing.Verdict.ShouldBe(ComparisonVerdict.Missing);
        missing.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Compare_TimingAdvisory_SlowerIsAWarningButAllocationsStillGate()
    {
        var options = new ComparisonOptions(TimingAdvisory: true);

        var rows = BaselineComparer.Compare(
            Set(new BenchmarkMeasurement("A.Slow", 1_000, 100), new("B.Alloc", 1_000, 0)),
            Set(new BenchmarkMeasurement("A.Slow", 5_000, 100), new("B.Alloc", 1_000, 32)),
            options);

        rows[0].Verdict.ShouldBe(ComparisonVerdict.SlowerAdvisory);
        rows[0].IsFailure.ShouldBeFalse();
        rows[1].Verdict.ShouldBe(ComparisonVerdict.NewAllocation);
        rows[1].IsFailure.ShouldBeTrue();
        BaselineComparer.ToMarkdown(rows, options).ShouldContain("advisory");
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

    [Theory]
    [InlineData(100, 184, false)]          // +84 B = 20 % + 64 B floor exactly: tolerated
    [InlineData(100, 185, true)]           // one byte past the threshold + floor
    [InlineData(16_440, 19_700, false)]    // +19.8 %
    [InlineData(16_440, 32_880, true)]
    [InlineData(200, 100, false)]          // fewer allocations never fail
    public void Compare_AllocatingBaselineAllocatesMore_FailsBeyondThresholdPlusFloor(long was, long now, bool fails)
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 40, was)), Set(new BenchmarkMeasurement("A.B", 40, now)));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(fails ? ComparisonVerdict.MoreAllocation : ComparisonVerdict.Ok);
        row.IsFailure.ShouldBe(fails);
    }

    [Theory]
    [InlineData(null, 0.20)]
    [InlineData("0.20", 0.20)]
    [InlineData("1", 1.0)]
    [InlineData("15%", 0.15)]
    [InlineData(" 100 % ", 1.0)]
    public void TryParseThreshold_FractionOrPercent_Parses(string? text, double expected)
    {
        ComparerArguments.TryParseThreshold(text, out var fraction, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        fraction.ShouldBe(expected, 1e-12);
    }

    [Theory]
    [InlineData("20")]      // would be 2000 %
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("150%")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void TryParseThreshold_OutOfRangeOrGarbage_IsRejected(string text)
    {
        ComparerArguments.TryParseThreshold(text, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Positional_SkipsOptionValuesButNotFlags()
    {
        ComparerArguments.Positional(["--require-all", "base", "--threshold", "0.2", "results", "--summary", "s.md"])
            .ShouldBe(["base", "results"]);
    }

    [Fact]
    public void EscapeAnnotation_EscapesWorkflowCommandCharacters() =>
        ComparerArguments.EscapeAnnotation("a%b\r\nc").ShouldBe("a%25b%0D%0Ac");

    [Fact]
    public void Compare_FasterBeyondThreshold_IsImprovedNotFailure()
    {
        var rows = BaselineComparer.Compare(Set(new BenchmarkMeasurement("A.B", 1_000, 64)), Set(new BenchmarkMeasurement("A.B", 400, 0)));

        var row = rows.ShouldHaveSingleItem();
        row.Verdict.ShouldBe(ComparisonVerdict.Improved);
        row.IsFailure.ShouldBeFalse();
    }

    [Fact]
    public void Compare_NewAndMissingBenchmarksOnAFilteredRun_AreInformational()
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
