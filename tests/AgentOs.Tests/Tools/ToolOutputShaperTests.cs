// 2.7 — tool-output shaping. Real MSBuild / dotnet test logs (Fixtures/ToolOutput, captured from the .NET 10 SDK)
// keep their diagnostics, failed tests and summaries and lose restore/progress noise; anything over the cap is cut
// head + tail around an explicit marker without splitting a surrogate pair; the gateway hands the model the shaped
// text while the evidence keeps the full output and both sizes; the setting is bounded.

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
    private static string Fixture(string name)
    {
        using var stream = typeof(ToolOutputShaperTests).Assembly.GetManifestResourceStream($"ToolOutput.{name}.txt")
            ?? throw new InvalidOperationException($"fixture {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // --- 1. build/test logs ---------------------------------------------------------------------------------

    [Fact]
    public void Shape_CompileErrorLog_KeepsEveryDiagnosticOnce_WithRepeatCount_AndTheSummary()
    {
        var log = Fixture("build-errors");

        var shaped = ToolOutputShaper.Shape(log, ToolOutputShaper.DefaultMaxOutputTokens);

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

        var shaped = ToolOutputShaper.Shape(log, ToolOutputShaper.DefaultMaxOutputTokens);

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
    public void Shape_FailingTests_KeepsFailedBlocks_WithFirstFrames_AndTheTotals_DropsPassedTests()
    {
        var log = Fixture("test-failures");

        var shaped = ToolOutputShaper.Shape(log, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.LogShaped.ShouldBeTrue();
        shaped.Text.ShouldContain("Failed Sample.Tests.CalculatorTests.Compare_Values_AreEqual(a: 1, b: 2)");
        shaped.Text.ShouldContain("Failed Sample.Tests.CalculatorTests.Divide_ByZero_Throws");
        shaped.Text.ShouldContain("Assert.Equal() Failure: Values differ");
        shaped.Text.ShouldContain("System.DivideByZeroException");
        shaped.Text.ShouldContain("at Sample.Tests.Helper.Deep(Int32 a, Int32 b)");
        shaped.Text.ShouldContain("Test Run Failed.");
        shaped.Text.ShouldContain("Total tests: 35");
        shaped.Text.ShouldContain("Failed: 2");
        shaped.Text.ShouldNotContain("Passed Sample.Tests.T1.");
        shaped.Text.ShouldNotContain("Starting test execution");
        shaped.ShapedChars.ShouldBeLessThan(log.Length);
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
    public void Shape_PlainTextThatIsNotALog_IsUntouchedUnderTheCap()
    {
        var text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"row {i}: value"));

        var shaped = ToolOutputShaper.Shape(text, ToolOutputShaper.DefaultMaxOutputTokens);

        shaped.Text.ShouldBeSameAs(text);
        shaped.LogShaped.ShouldBeFalse();
        shaped.Capped.ShouldBeFalse();
        shaped.SavedChars.ShouldBe(0);
    }

    [Fact]
    public void Shape_BuildVerifierJson_ShapesTheEmbeddedLog_AndStaysValidJson()
    {
        var log = Fixture("build-errors") + string.Concat(Enumerable.Repeat("  Restored /work/x.csproj (in 79 ms).\n", 200));
        var json = JsonSerializer.Serialize(new { success = false, exit_code = 1, output = log });

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

    // --- 2. the cap -----------------------------------------------------------------------------------------

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

    // --- 3. the gateway + evidence + metric ------------------------------------------------------------------

    [Fact]
    public async Task Gateway_ShapesWhatTheModelSees_EvidenceKeepsFullOutputAndBothSizes()
    {
        var log = Fixture("test-failures");
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence);

        var result = await gateway.InvokeAsync(new FixedTool(log), Request("tenant-a"), CancellationToken.None);

        result.Output.Length.ShouldBeLessThan(log.Length);
        result.Output.ShouldContain("Failed Sample.Tests.CalculatorTests.Divide_ByZero_Throws");
        var row = (await evidence.ListRecentAsync("tenant-a")).ShouldHaveSingleItem();
        row.Output.ShouldBe(log);
        row.OutputChars.ShouldBe(log.Length);
        row.ShapedOutputChars.ShouldBe(result.Output.Length);
        row.SavedChars.ShouldBe(log.Length - result.Output.Length);
    }

    [Fact]
    public async Task Gateway_SmallOutput_IsPassedThrough_WithEqualSizes()
    {
        var evidence = new InMemoryToolInvocationLog();
        var gateway = new DefaultToolGateway(policy: null, log: evidence);

        var result = await gateway.InvokeAsync(new FixedTool("happy"), Request("tenant-b"), CancellationToken.None);

        result.Output.ShouldBe("happy");
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
        var gateway = new DefaultToolGateway(policy: null, log: null, settings: settings);

        var small = await gateway.InvokeAsync(new FixedTool(text), Request("small"), CancellationToken.None);
        var other = await gateway.InvokeAsync(new FixedTool(text), Request("other"), CancellationToken.None);

        small.Output.Length.ShouldBeLessThanOrEqualTo(500 * ToolOutputShaper.CharsPerToken);
        other.Output.Length.ShouldBeGreaterThan(500 * ToolOutputShaper.CharsPerToken);
        other.Output.Length.ShouldBeLessThanOrEqualTo(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
    }

    [Fact]
    public async Task Gateway_ToolError_IsShapedToo()
    {
        var log = Fixture("build-errors");
        var gateway = new DefaultToolGateway();

        var result = await gateway.InvokeAsync(new FixedTool(log, isError: true), Request("tenant-c"), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Output.Length.ShouldBeLessThan(log.Length);
        result.Output.ShouldContain("error CS0103");
    }

    [Fact]
    public async Task Gateway_RecordsSavedChars_OnTheToolsMeter()
    {
        long saved = 0;
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
            foreach (var tag in tags)
            {
                if (tag.Key == "tool" && Equals(tag.Value, "fixed_meter_probe"))
                {
                    Interlocked.Add(ref saved, value);
                }
            }
        });
        listener.Start();
        var log = Fixture("build-success");

        var result = await new DefaultToolGateway().InvokeAsync(
            new FixedTool(log, name: "fixed_meter_probe"), Request("tenant-d", "fixed_meter_probe"), CancellationToken.None);

        Interlocked.Read(ref saved).ShouldBe(log.Length - result.Output.Length);
    }

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
    private sealed class FixedTool(string output, bool isError = false, string name = "fixed") : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "returns a fixed output", """{"type":"object"}""");

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(isError
                ? ToolInvocationResult.Error(request.CallId, output)
                : ToolInvocationResult.Success(request.CallId, output));
    }
}
