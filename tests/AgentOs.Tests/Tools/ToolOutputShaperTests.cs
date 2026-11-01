// 2.7 — tool-output shaping. Real MSBuild / dotnet test logs (Fixtures/ToolOutput, captured from the .NET 10 SDK)
// keep their diagnostics, failed tests and summaries and lose restore/progress noise — identically for LF, CRLF and
// mixed line endings; a result within the budget is never rewritten; anything over the cap is cut head + tail
// around an explicit marker without splitting a surrogate pair; JSON stays valid JSON; only a MODEL-FACING gateway
// shapes (data consumers get the full output); the evidence keeps the full output, both sizes and the cap whatever
// goes wrong after the tool ran; the metric tag is bounded; the setting is bounded.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Tools;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Tools;
using AgentOs.Modules.Tools.Evidence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tools;

public sealed class ToolOutputShaperTests
{
    // The smallest budget (2 000 chars): every fixture is above it, so Shape() actually shapes them.
    private const int Small = ToolOutputShaper.MinMaxOutputTokens;

    private static readonly string[] FixtureNames = ["build-errors", "build-success", "test-failures"];

    private static string Fixture(string name)
    {
        using var stream = typeof(ToolOutputShaperTests).Assembly.GetManifestResourceStream($"ToolOutput.{name}.txt")
            ?? throw new InvalidOperationException($"fixture {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // .gitattributes forces LF on checked-in files, so the CRLF / mixed variants are built here.
    private static string Crlf(string lf) => lf.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Mixed(string lf)
    {
        var lines = lf.Split('\n');
        var sb = new StringBuilder(lf.Length + lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append(i % 2 == 0 ? "\r\n" : "\n");
            }
        }
        return sb.ToString();
    }

    // A test log well over the DEFAULT budget (16 000 chars): hundreds of passed tests + the failing fixture.
    private static string BigTestLog()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 700; i++)
        {
            sb.Append("  Passed Sample.Tests.Bulk.Passes_").Append(i).Append(" [< 1 ms]\n");
        }
        return sb.Append(Fixture("test-failures")).ToString();
    }

    // --- 1. build/test logs ---------------------------------------------------------------------------------

    [Fact]
    public void Shape_CompileErrorLog_KeepsEveryDiagnosticOnce_WithRepeatCount_AndTheSummary()
    {
        var log = Fixture("build-errors");

        var shaped = ToolOutputShaper.Shape(log, Small);

        shaped.LogShaped.ShouldBeTrue();
        shaped.Capped.ShouldBeFalse();
        shaped.ShapedChars.ShouldBeLessThan(log.Length);
        foreach (var code in new[] { "error CS0029", "error CS0103", "error CS1061", "warning CS0219", "warning SYSLIB0014" })
        {
            shaped.Text.ShouldContain(code);
        }
        // MSBuild repeats every diagnostic in its closing summary: each appears once, with a count.
        CountOf(shaped.Text, "error CS1061").ShouldBe(1);
        shaped.Text.ShouldContain("(×2)");
        shaped.Text.ShouldContain("Build FAILED.");
        shaped.Text.ShouldContain("5 Error(s)");
        shaped.Text.ShouldContain("2 Warning(s)");
        shaped.Text.ShouldContain("Time Elapsed");
        shaped.Text.ShouldNotContain("Determining projects to restore");
        shaped.Text.ShouldNotContain("up-to-date for restore");
    }

    [Fact]
    public void Shape_SuccessfulDetailedBuild_DropsRestoreNoise_KeepsTheVerdict()
    {
        var log = Fixture("build-success");

        var shaped = ToolOutputShaper.Shape(log, Small);

        shaped.LogShaped.ShouldBeTrue();
        shaped.Text.ShouldContain("Build succeeded.");
        shaped.Text.ShouldContain("0 Error(s)");
        shaped.Text.ShouldNotContain("Feeds used");
        shaped.Text.ShouldNotContain("Restored /work");
        shaped.Text.ShouldContain("lines omitted …]");
        // A successful build says little the model needs: well over half of it is gone.
        shaped.ShapedChars.ShouldBeLessThan(log.Length / 2);
    }

    [Fact]
    public void TryShapeLog_FailingTests_KeepsFailedBlocks_WithFirstFrames_AndTheTotals_DropsPassedTests()
    {
        var log = Fixture("test-failures");

        ToolOutputShaper.TryShapeLog(log, out var shaped).ShouldBeTrue();

        shaped.ShouldContain("Failed Sample.Tests.CalculatorTests.Compare_Values_AreEqual(a: 1, b: 2)");
        shaped.ShouldContain("Failed Sample.Tests.CalculatorTests.Divide_ByZero_Throws");
        shaped.ShouldContain("Assert.Equal() Failure: Values differ");
        shaped.ShouldContain("System.DivideByZeroException");
        shaped.ShouldContain("at Sample.Tests.Helper.Deep(Int32 a, Int32 b)");
        shaped.ShouldContain("Test Run Failed.");
        shaped.ShouldContain("Total tests: 35");
        shaped.ShouldContain("Failed: 2");
        shaped.ShouldNotContain("Passed Sample.Tests.T1.");
        shaped.ShouldNotContain("Starting test execution");
        shaped.Length.ShouldBeLessThan(log.Length);
    }

    [Fact]
    public void Shape_FailingTestsOverTheDefaultBudget_KeepsTheFailures()
    {
        var log = BigTestLog();
        log.Length.ShouldBeGreaterThan(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);

        var shaped = ToolOutputShaper.Shape(log, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.LogShaped.ShouldBeTrue();
        shaped.Capped.ShouldBeFalse();
        shaped.Text.ShouldContain("Failed Sample.Tests.CalculatorTests.Divide_ByZero_Throws");
        shaped.Text.ShouldContain("Total tests: 35");
        shaped.Text.ShouldNotContain("Passes_350");
    }

    // Line endings: CRLF and mixed logs shape to exactly what the LF log shapes to — no shifted slices, no lost lines.
    [Theory]
    [InlineData("build-errors")]
    [InlineData("build-success")]
    [InlineData("test-failures")]
    public void TryShapeLog_CrlfAndMixedEndings_ShapeExactlyLikeLf(string name)
    {
        var lf = Fixture(name);
        ToolOutputShaper.TryShapeLog(lf, out var expected).ShouldBeTrue();

        ToolOutputShaper.TryShapeLog(Crlf(lf), out var crlf).ShouldBeTrue();
        ToolOutputShaper.TryShapeLog(Mixed(lf), out var mixed).ShouldBeTrue();

        crlf.ShouldBe(expected);
        mixed.ShouldBe(expected);
    }

    [Fact]
    public void Shape_CrlfFixtures_KeepEveryDiagnosticAndSummary()
    {
        foreach (var name in FixtureNames)
        {
            var crlf = Crlf(Fixture(name));

            var shaped = ToolOutputShaper.Shape(crlf, Small);

            shaped.LogShaped.ShouldBeTrue(name);
            shaped.Text.ShouldNotContain("\r", customMessage: name);
        }
        var errors = ToolOutputShaper.Shape(Crlf(Fixture("build-errors")), Small).Text;
        errors.ShouldContain("/work/bad/Class1.cs(7,49): error CS1061");
        errors.ShouldContain("Build FAILED.");
        errors.ShouldContain("5 Error(s)");
    }

    [Fact]
    public void TryShapeLog_LoneCarriageReturnProgress_BeforeTheErrors_KeepsEveryErrorAndTheVerdict()
    {
        // Progress output redraws one line with bare '\r'; it must neither end the scan nor shift the line count.
        var progress = string.Join('\r', Enumerable.Range(0, 30).Select(i => $"Progress {i}%"));
        var log = "  Determining projects to restore...\n" + progress + "\n" + Fixture("build-errors");

        ToolOutputShaper.TryShapeLog(log, out var shaped).ShouldBeTrue();

        foreach (var code in new[] { "error CS0029", "error CS0103", "error CS1061" })
        {
            shaped.ShouldContain(code);
        }
        shaped.ShouldContain("Build FAILED.");
        shaped.ShouldContain("5 Error(s)");
        shaped.ShouldContain("Time Elapsed");
    }

    // U+2028 LINE SEPARATOR, built at run time (a literal one would end the C# string literal).
    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);

    [Fact]
    public void TryShapeLog_UnicodeLineSeparatorInsideALine_IsAnOrdinaryCharacter()
    {
        var lf = Fixture("build-errors");
        var withSeparator = lf.Replace("Cannot implicitly", "Cannot" + LineSeparator + "implicitly", StringComparison.Ordinal);
        ToolOutputShaper.TryShapeLog(lf, out var expected).ShouldBeTrue();

        ToolOutputShaper.TryShapeLog(withSeparator, out var shaped).ShouldBeTrue();

        shaped.ShouldBe(expected.Replace("Cannot implicitly", "Cannot" + LineSeparator + "implicitly", StringComparison.Ordinal));
    }

    [Fact]
    public void TryShapeLog_DeepStackTrace_KeepsTheFirstFramesAndCountsTheRest()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 25; i++)
        {
            sb.Append("  Passed Ns.Ok.Test").Append(i).Append(" [1 ms]\n");
        }
        sb.Append("  Failed Ns.Broken.Test [4 ms]\n  Error Message:\n   boom\n  Stack Trace:\n");
        for (var i = 0; i < 30; i++)
        {
            sb.Append("     at Ns.Frame").Append(i).Append("() in /src/F.cs:line ").Append(i).Append('\n');
        }
        sb.Append("\nFailed!  - Failed:     1, Passed:    25, Skipped:     0, Total:    26\n");

        ToolOutputShaper.TryShapeLog(sb.ToString(), out var shaped).ShouldBeTrue();

        for (var i = 0; i < ToolOutputShaper.StackFramesPerFailure; i++)
        {
            shaped.ShouldContain($"Ns.Frame{i}()");
        }
        shaped.ShouldNotContain($"Ns.Frame{ToolOutputShaper.StackFramesPerFailure}()");
        shaped.ShouldContain($"[… {30 - ToolOutputShaper.StackFramesPerFailure} more stack frames …]");
        shaped.ShouldContain("Failed!  - Failed:     1");
        shaped.ShouldContain("boom");
    }

    [Fact]
    public void TryShapeLog_AssertionMessageMentioningTotals_DoesNotEndTheFailedTestBlock()
    {
        // "total:" / "failed:" inside an assertion message are not a summary: the block (and its frames) continues.
        var sb = new StringBuilder();
        for (var i = 0; i < 25; i++)
        {
            sb.Append("  Passed Ns.Ok.Test").Append(i).Append(" [1 ms]\n");
        }
        sb.Append("  Failed Ns.Broken.Totals [4 ms]\n  Error Message:\n   Expected row total: 3, rows failed: 1\n  Stack Trace:\n");
        sb.Append("     at Ns.Report.Sum() in /src/R.cs:line 9\n");
        for (var i = 0; i < 20; i++)
        {
            sb.Append("  Passed Ns.Ok.After").Append(i).Append(" [1 ms]\n");
        }
        sb.Append("\nFailed!  - Failed:     1, Passed:    45, Skipped:     0, Total:    46\n");

        ToolOutputShaper.TryShapeLog(sb.ToString(), out var shaped).ShouldBeTrue();

        shaped.ShouldContain("Expected row total: 3, rows failed: 1");
        shaped.ShouldContain("at Ns.Report.Sum() in /src/R.cs:line 9");
    }

    [Fact]
    public void TryShapeLog_ProseStartingWithFailed_DoesNotOpenAFailedTestBlock()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            sb.Append("  Determining projects to restore...\n");
        }
        sb.Append("         Failed to load prune package data from PrunePackageData folder, loading from targeting packs instead\n");
        sb.Append("         Looking for targeting packs in /usr/share/dotnet/packs\n");
        for (var i = 0; i < 20; i++)
        {
            sb.Append("       Skipping target \"T").Append(i).Append("\" because all output files are up-to-date.\n");
        }
        sb.Append("Build succeeded.\n");

        ToolOutputShaper.TryShapeLog(sb.ToString(), out var shaped).ShouldBeTrue();

        shaped.ShouldNotContain("Looking for targeting packs");
        shaped.ShouldContain("Build succeeded.");
    }

    [Fact]
    public void TryShapeLog_RepeatedProgressLines_CollapseIntoOneWithACount()
    {
        var sb = new StringBuilder("Build started 11/01/2026\n");
        for (var i = 0; i < 40; i++)
        {
            sb.Append("Build succeeded.\n");
        }

        ToolOutputShaper.TryShapeLog(sb.ToString(), out var shaped).ShouldBeTrue();

        CountOf(shaped, "Build succeeded.").ShouldBe(1);
        shaped.ShouldContain("Build succeeded. (×40)");
    }

    [Fact]
    public void TryShapeLog_ShortLog_IsLeftAlone()
    {
        const string log = "  Determining projects to restore...\nBuild succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

        ToolOutputShaper.TryShapeLog(log, out var shaped).ShouldBeFalse();
        shaped.ShouldBeSameAs(log);
    }

    [Fact]
    public void Shape_SmallSourceFileMentioningLogKeywords_IsUnchanged()
    {
        // A file the model asked to read, well within budget, that merely talks about build output.
        var src = new StringBuilder("// Parser for build output: looks for \"Build FAILED\" and \"error CS\" lines\n");
        for (var i = 0; i < 60; i++)
        {
            src.Append("    var x").Append(i).Append(" = Compute(").Append(i).Append("); // step ").Append(i).Append('\n');
        }
        var text = src.ToString();

        var shaped = ToolOutputShaper.Shape(text, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.Text.ShouldBeSameAs(text);
        shaped.LogShaped.ShouldBeFalse();
        shaped.Capped.ShouldBeFalse();
    }

    [Fact]
    public void TryShapeLog_KeywordsWithoutLogStructure_AreNotALog()
    {
        // Over any budget, a text whose only "signal" is a keyword inside prose/comments is cut as text, not
        // log-shaped (which would keep head/tail + summaries and drop everything else).
        var src = new StringBuilder("// Parser for build output: looks for \"Build FAILED\" lines\n");
        for (var i = 0; i < 400; i++)
        {
            src.Append("    var x").Append(i).Append(" = Compute(").Append(i).Append("); // total: ").Append(i).Append('\n');
        }

        ToolOutputShaper.TryShapeLog(src.ToString(), out _).ShouldBeFalse();
        var shaped = ToolOutputShaper.Shape(src.ToString(), Small);
        shaped.LogShaped.ShouldBeFalse();
        shaped.Capped.ShouldBeTrue();
    }

    [Fact]
    public void Shape_PlainTextThatIsNotALog_IsUntouchedUnderTheCap()
    {
        var text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"row {i}: value"));

        var shaped = ToolOutputShaper.Shape(text, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.Text.ShouldBeSameAs(text);
        shaped.LogShaped.ShouldBeFalse();
        shaped.Capped.ShouldBeFalse();
        shaped.SavedChars.ShouldBe(0);
    }

    // --- 2. JSON --------------------------------------------------------------------------------------------

    [Fact]
    public void Shape_BuildVerifierJson_ShapesTheEmbeddedLog_AndStaysValidJson()
    {
        var log = Fixture("build-errors") + string.Concat(Enumerable.Repeat("  Restored /work/x.csproj (in 79 ms).\n", 500));
        var json = JsonSerializer.Serialize(new { success = false, exit_code = 1, output = log });
        json.Length.ShouldBeGreaterThan(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);

        var shaped = ToolOutputShaper.Shape(json, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.LogShaped.ShouldBeTrue();
        shaped.ShapedChars.ShouldBeLessThan(json.Length);
        using var doc = JsonDocument.Parse(shaped.Text);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("exit_code").GetInt32().ShouldBe(1);
        var output = doc.RootElement.GetProperty("output").GetString()!;
        output.ShouldContain("error CS1061");
        output.ShouldNotContain("Restored /work/x.csproj");
    }

    [Fact]
    public void Shape_JsonArrayOverTheCap_DropsTrailingItems_BehindAMarker_AndStaysValidJson()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":{i},\"name\":\"file{i}.cs\"}}")) + "]";

        var shaped = ToolOutputShaper.Shape(json, Small);

        shaped.ShapedChars.ShouldBeLessThanOrEqualTo(Small * ToolOutputShaper.CharsPerToken);
        shaped.Capped.ShouldBeFalse();
        using var doc = JsonDocument.Parse(shaped.Text);
        var items = doc.RootElement.EnumerateArray().ToList();
        items[0].GetProperty("id").GetInt32().ShouldBe(0);
        var marker = items[^1].GetString()!;
        marker.ShouldStartWith("[… ");
        marker.ShouldEndWith(" more items omitted …]");
        (items.Count - 1 + int.Parse(marker.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(2000);
    }

    [Fact]
    public void Shape_NestedMcpJsonOverTheCap_CapsTheNestedText_AndStaysValidJson()
    {
        var text = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"row {i}"));
        var json = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, isError = false });

        var shaped = ToolOutputShaper.Shape(json, Small);

        shaped.ShapedChars.ShouldBeLessThanOrEqualTo(Small * ToolOutputShaper.CharsPerToken);
        using var doc = JsonDocument.Parse(shaped.Text);
        var inner = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
        inner.ShouldStartWith("row 0\n");
        inner.ShouldEndWith("row 2999");
        inner.ShouldContain(" lines omitted …]");
        doc.RootElement.GetProperty("isError").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Shape_JsonWithDuplicateKeys_DoesNotThrow_AndIsCutAsText()
    {
        var dupe = "{\"output\":\"" + string.Join("\\n", Enumerable.Range(0, 400).Select(i => $"line {i} Build FAILED")) + "\",\"output\":\"x\"}";

        var shaped = ToolOutputShaper.Shape(dupe, Small);

        shaped.Capped.ShouldBeTrue();
        shaped.ShapedChars.ShouldBeLessThanOrEqualTo(Small * ToolOutputShaper.CharsPerToken);
        shaped.Text.ShouldStartWith("{\"output\":\"line 0 Build FAILED");
    }

    // --- 3. the cap -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(4000)]
    public void Shape_OverTheCap_CutsHeadAndTail_WithAnExplicitMarker_WithinBudget(int tokens)
    {
        var text = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"line {i:D5} payload payload payload"));

        var shaped = ToolOutputShaper.Shape(text, tokens);

        shaped.Capped.ShouldBeTrue();
        shaped.ShapedChars.ShouldBeLessThanOrEqualTo(tokens * ToolOutputShaper.CharsPerToken);
        shaped.Text.ShouldStartWith("line 00000 ");
        shaped.Text.ShouldEndWith("line 19999 payload payload payload");
        shaped.Text.ShouldContain(" lines omitted …]");
        // Cut on line boundaries: every kept line except the marker is a whole input line.
        shaped.Text.Split('\n').Where(l => !l.StartsWith("[…", StringComparison.Ordinal))
            .ShouldAllBe(l => l.Length == "line 00000 payload payload payload".Length);
        shaped.SavedChars.ShouldBe(text.Length - shaped.ShapedChars);
    }

    [Fact]
    public void Cap_ExactlyAtTheCap_IsUntouched_OneOverIsCut()
    {
        var max = ToolOutputShaper.MinMaxOutputTokens * ToolOutputShaper.CharsPerToken;
        var exact = new string('a', max);

        ToolOutputShaper.Shape(exact, ToolOutputShaper.MinMaxOutputTokens).Capped.ShouldBeFalse();
        var over = ToolOutputShaper.Shape(exact + "b", ToolOutputShaper.MinMaxOutputTokens);
        over.Capped.ShouldBeTrue();
        over.ShapedChars.ShouldBeLessThanOrEqualTo(max);
        over.Text.ShouldContain("[… 1 line omitted …]");
        over.Text.ShouldEndWith("b");
    }

    [Fact]
    public void Cap_SingleHugeLine_IsCutMidLine_AndCountsOneOmittedLine()
    {
        var text = "{\"data\":\"" + new string('x', 100_000) + "\"}";

        var capped = ToolOutputShaper.Cap(text, 2000);

        capped.Length.ShouldBeLessThanOrEqualTo(2000);
        capped.ShouldStartWith("{\"data\":\"xxx");
        capped.ShouldEndWith("xxx\"}");
        capped.ShouldContain("[… 1 line omitted …]");
    }

    [Fact]
    public void Cap_NeverSplitsASurrogatePair_AtEitherCut()
    {
        // 😀 is a surrogate pair; at every offset one of the two cuts lands next to (or inside) a pair.
        for (var shift = 0; shift < 4; shift++)
        {
            var text = new string('a', shift) + string.Concat(Enumerable.Repeat("😀", 5000));

            var capped = ToolOutputShaper.Cap(text, 2001);

            capped.Length.ShouldBeLessThanOrEqualTo(2001);
            for (var i = 0; i < capped.Length; i++)
            {
                if (char.IsHighSurrogate(capped[i]))
                {
                    (i + 1 < capped.Length && char.IsLowSurrogate(capped[i + 1])).ShouldBeTrue($"lone high surrogate at {i} (shift {shift})");
                }
                else if (char.IsLowSurrogate(capped[i]))
                {
                    (i > 0 && char.IsHighSurrogate(capped[i - 1])).ShouldBeTrue($"lone low surrogate at {i} (shift {shift})");
                }
            }
        }
    }

    [Theory]
    [InlineData(0, ToolOutputShaper.MinMaxOutputTokens)]
    [InlineData(499, ToolOutputShaper.MinMaxOutputTokens)]
    [InlineData(500, 500)]
    [InlineData(4000, 4000)]
    [InlineData(50_000, 50_000)]
    [InlineData(1_000_000, ToolOutputShaper.MaxMaxOutputTokens)]
    public void ClampTokens_KeepsTheBudgetInRange(int tokens, int expected)
        => ToolOutputShaper.ClampTokens(tokens).ShouldBe(expected);

    [Fact]
    public void Shape_NullOrEmpty_IsEmpty()
    {
        ToolOutputShaper.Shape(null, 4000).Text.ShouldBe(string.Empty);
        ToolOutputShaper.Shape("", 4000).SavedChars.ShouldBe(0);
    }

    // --- 4. the gateway + evidence + metric ------------------------------------------------------------------

    [Fact]
    public async Task Gateway_ModelFacing_ShapesForTheModel_OutputAndEvidenceKeepTheFullResult()
    {
        var log = BigTestLog();
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence, shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool(log), Request("tenant-a"), CancellationToken.None);

        result.Output.ShouldBe(log);
        var forModel = result.ModelOutput.ShouldNotBeNull();
        result.ForModel.ShouldBeSameAs(forModel);
        forModel.Length.ShouldBeLessThan(log.Length);
        forModel.ShouldContain("Failed Sample.Tests.CalculatorTests.Divide_ByZero_Throws");
        var row = (await evidence.ListRecentAsync("tenant-a")).ShouldHaveSingleItem();
        row.Output.ShouldBe(log);
        row.OutputChars.ShouldBe(log.Length);
        row.ShapedOutputChars.ShouldBe(forModel.Length);
        row.SavedChars.ShouldBe(log.Length - forModel.Length);
        row.MaxOutputTokens.ShouldBe(ToolOutputShaper.DefaultMaxOutputTokens);
    }

    [Fact]
    public async Task Gateway_NotModelFacing_NeverShapes_EqualSizes_NoCap()
    {
        var log = BigTestLog();
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence);

        var result = await gateway.InvokeAsync(new FixedTool(log), Request("tenant-data"), CancellationToken.None);

        result.Output.ShouldBe(log);
        result.ModelOutput.ShouldBeNull();
        result.ForModel.ShouldBe(log);
        var row = (await evidence.ListRecentAsync("tenant-data")).ShouldHaveSingleItem();
        row.ShapedOutputChars.ShouldBe(log.Length);
        row.SavedChars.ShouldBe(0);
        row.MaxOutputTokens.ShouldBeNull();
    }

    [Fact]
    public async Task Gateway_SmallOutput_IsPassedThrough_WithEqualSizes()
    {
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence, shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool("happy"), Request("tenant-b"), CancellationToken.None);

        result.ForModel.ShouldBe("happy");
        var row = (await evidence.ListRecentAsync("tenant-b")).ShouldHaveSingleItem();
        row.OutputChars.ShouldBe(5);
        row.ShapedOutputChars.ShouldBe(5);
        row.SavedChars.ShouldBe(0);
    }

    [Fact]
    public async Task Gateway_ReadsTheCapPerCall_ForTheCallsTenant()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"row {i:D5}"));
        var settings = Settings();
        (await settings.SetAsync(ToolsSettings.MaxOutputTokens, "500", SettingScope.Tenant, "small", "admin")).Ok.ShouldBeTrue();
        var gateway = new DefaultToolGateway(policy: null, log: null, settings: settings, shapeForModel: true);

        var small = await gateway.InvokeAsync(new FixedTool(text), Request("small"), CancellationToken.None);
        var other = await gateway.InvokeAsync(new FixedTool(text), Request("other"), CancellationToken.None);

        small.ForModel.Length.ShouldBeLessThanOrEqualTo(500 * ToolOutputShaper.CharsPerToken);
        other.ForModel.Length.ShouldBeGreaterThan(500 * ToolOutputShaper.CharsPerToken);
        other.ForModel.Length.ShouldBeLessThanOrEqualTo(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
    }

    [Fact]
    public async Task Gateway_ToolError_IsShapedToo()
    {
        var log = BigTestLog();
        var gateway = new DefaultToolGateway(shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool(log, isError: true), Request("tenant-c"), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.ForModel.Length.ShouldBeLessThan(log.Length);
        result.ForModel.ShouldContain("System.DivideByZeroException");
    }

    [Fact]
    public async Task Gateway_DuplicateKeyJson_StillAnswers_AndRecordsEvidence()
    {
        var dupe = "{\"output\":\"" + string.Join("\\n", Enumerable.Range(0, 2000).Select(i => $"line {i} Build FAILED")) + "\",\"output\":\"x\"}";
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence, shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool(dupe), Request("tenant-dupe"), CancellationToken.None);

        result.Output.ShouldBe(dupe);
        result.ForModel.Length.ShouldBeLessThanOrEqualTo(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
        (await evidence.ListRecentAsync("tenant-dupe")).ShouldHaveSingleItem().Output.ShouldBe(dupe);
    }

    [Fact]
    public async Task Gateway_SettingsStoreThrows_FallsBackToTheDefaultCap_AndRecordsEvidence()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"row {i:D5}"));
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(
            policy: null, log: evidence, settings: new ThrowingSettings(new KeyNotFoundException("no such key")), shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool(text), Request("tenant-flaky"), CancellationToken.None);

        result.ForModel.Length.ShouldBeLessThanOrEqualTo(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
        var row = (await evidence.ListRecentAsync("tenant-flaky")).ShouldHaveSingleItem();
        row.Output.ShouldBe(text);
        row.MaxOutputTokens.ShouldBe(ToolOutputShaper.DefaultMaxOutputTokens);
    }

    [Fact]
    public async Task Gateway_SettingsStoreTimesOutOnItsOwn_IsAHiccup_NotACancellation()
    {
        // A store's own timeout surfaces as TaskCanceledException while the CALLER's token is fine.
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(
            policy: null, log: evidence, settings: new ThrowingSettings(new TaskCanceledException("store timeout")), shapeForModel: true);

        var result = await gateway.InvokeAsync(new FixedTool("ok"), Request("tenant-slow"), CancellationToken.None);

        result.ForModel.ShouldBe("ok");
        (await evidence.ListRecentAsync("tenant-slow")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Gateway_CancelledAfterTheToolRan_StillRecordsTheEvidence()
    {
        using var cts = new CancellationTokenSource();
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence, settings: Settings(), shapeForModel: true);
        var log = BigTestLog();

        // The tool completes its side effect, and the caller cancels right as it returns.
        var result = await gateway.InvokeAsync(new FixedTool(log, onInvoke: cts.Cancel), Request("tenant-cancel"), cts.Token);

        cts.IsCancellationRequested.ShouldBeTrue();
        result.Output.ShouldBe(log);
        var row = (await evidence.ListRecentAsync("tenant-cancel")).ShouldHaveSingleItem();
        row.Output.ShouldBe(log);
        row.ShapedOutputChars.ShouldBe(result.ForModel.Length);
    }

    private static readonly AsyncLocal<bool> MeterProbe = new();

    [Fact]
    public async Task Gateway_RecordsSavedChars_OnTheToolsMeter_WithABoundedKindTag()
    {
        long saved = 0;
        var kinds = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ToolTelemetry.MeterName && instrument.Name == ToolTelemetry.SavedCharsName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            // Only this test's async flow counts (other tests shape in parallel on the same static meter).
            if (!MeterProbe.Value)
            {
                return;
            }
            Interlocked.Add(ref saved, value);
            lock (kinds)
            {
                kinds.AddRange(tags.ToArray().Select(t => $"{t.Key}={t.Value}"));
            }
        });
        listener.Start();
        var log = BigTestLog();

        MeterProbe.Value = true;
        var result = await new DefaultToolGateway(shapeForModel: true).InvokeAsync(
            new FixedTool(log, name: "github.search_code"), Request("tenant-d", "github.search_code"), CancellationToken.None);
        MeterProbe.Value = false;

        Interlocked.Read(ref saved).ShouldBe(log.Length - result.ForModel.Length);
        kinds.ShouldHaveSingleItem().ShouldBe("kind=mcp");
    }

    [Theory]
    [InlineData("build_verifier", "builtin")]
    [InlineData("github.search_code", "mcp")]
    [InlineData("runner_shell", "remote")]
    [InlineData("", "builtin")]
    [InlineData(null, "builtin")]
    public void KindOf_IsBounded(string? tool, string expected) => ToolTelemetry.KindOf(tool).ShouldBe(expected);

    // --- the setting -----------------------------------------------------------------------------------------

    [Fact]
    public void Setting_IsDeclaredWithItsDefaultAndBounds()
    {
        var descriptor = new ToolsSettings(enforceByDefault: false).Describe().Single(d => d.Key == "Tools:MaxOutputTokens");

        descriptor.Kind.ShouldBe(SettingKind.WholeNumber);
        descriptor.DefaultValue.ShouldBe("4000");
        descriptor.Min.ShouldBe(500);
        descriptor.Max.ShouldBe(50_000);
        descriptor.Scope.ShouldBe(SettingScope.Tenant);
    }

    [Theory]
    [InlineData("499", false)]
    [InlineData("500", true)]
    [InlineData("50000", true)]
    [InlineData("50001", false)]
    [InlineData("lots", false)]
    public async Task Setting_WritesOutsideTheRange_AreRejected(string value, bool ok)
        => (await Settings().SetAsync(ToolsSettings.MaxOutputTokens, value, SettingScope.Platform, null, "root")).Ok.ShouldBe(ok);

    // --- helpers ---------------------------------------------------------------------------------------------

    private static SettingsService Settings() => new(
        new SettingsCatalog([new ToolsSettings(enforceByDefault: false)]), new InMemoryAppConfigStore(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
        new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    private static ToolInvocationRequest Request(string tenantId, string tool = "fixed") =>
        new(ToolName: tool, CallId: Guid.NewGuid().ToString("N"), Input: "{}", TenantId: tenantId, RunId: null);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    // A small fake (not NSubstitute) so no ValueTask/Task member is configured on a substitute.
    private sealed class FixedTool(string output, bool isError = false, string name = "fixed", Action? onInvoke = null) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "returns a fixed output", """{"type":"object"}""");

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default)
        {
            onInvoke?.Invoke();
            return Task.FromResult(isError
                ? ToolInvocationResult.Error(request.CallId, output)
                : ToolInvocationResult.Success(request.CallId, output));
        }
    }

    // ISettings whose reads fail (a hand-written fake: ISettings is ValueTask-returning, never substituted).
    private sealed class ThrowingSettings(Exception error) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<SettingValue>(error);

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<SettingView>>(error);

        public ValueTask<SettingWriteResult> SetAsync(
            string key, string? value, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<SettingWriteResult>(error);
    }
}
