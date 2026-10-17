using AgentOs.SharedKernel.Modularity;
// AgentOs.Tests/Smoke/KcLiveBenchTests.cs
// Horizon 0.1 — live KC1–KC5 reproducibility bench (thesis Table 2.6 / 2.7).
// Runs the full hybrid pipeline n times against a REAL LLM, then derives KC1–KC5 metrics from the
// per-agent metric rows + PipelineResult. Skipped unless RUN_LIVE_LLM=1. Never runs in CI.
//
// See docs/KC_REPRODUCIBILITY.md for how to run + interpret. Core capture (latency / cost / tokens /
// completion / iterations / score-delta) needs no extra deps; compile-pass (KC2) and tests-build (KC3)
// are opt-in via KC_LIVE_BUILD=1 because they shell out to `dotnet build` on generated code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Domain.Code;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Testing;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Orchestration;
using AgentOs.Modules.Pipeline.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Smoke;

[Trait("Category", "LiveBench")]
public sealed class KcLiveBenchTests
{
    private const string RunFlag = "RUN_LIVE_LLM";
    private const string AnthropicKey = "ANTHROPIC_API_KEY";
    private const string AzureKey = "AZURE_OPENAI_API_KEY";
    private const string AzureEndpoint = "AZURE_OPENAI_ENDPOINT";

    // The thesis sample problem (§2.5, para 903) — Product REST API CRUD.
    private const string ProductStory =
        "A Product management REST API for e-commerce: an admin can create, read, update and delete a product; "
        + "an end user can search and filter products by category. A product has a name, description, price, "
        + "category and stock quantity. Include validation and error handling.";

    [Fact]
    public async Task Kc1ToKc5_Live_ReproducesTable26()
    {
        var mode = (Env("KC_LIVE_MODE") ?? "hybrid").ToLowerInvariant();
        var n = ParseInt(Env("KC_LIVE_N"), 10);
        var maxUsd = ParseDecimal(Env("KC_LIVE_MAX_USD"), 25m);
        var temp = Env("KC_LIVE_TEMP") ?? "0.2";
        var doBuild = Env("KC_LIVE_BUILD") == "1";

        var agents = AgentPlan(mode);
        SkipIfNotEnabled(agents);

        var csvPath = OutPath("kc_metrics_live.csv");
        var csv = new CsvMetricsCollector(csvPath);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var cfg = BuildConfig(agents, temp);
        sc.AddSingleton<IConfiguration>(cfg);
        sc.AddModulesFromAssemblies(cfg, typeof(AgentOs.Modules.Llm.LlmModule).Assembly, typeof(AgentOs.Modules.AppConfig.AppConfigModule).Assembly);
        sc.AddValidation();
        sc.AddSingleton<IMetricsCollector>(csv);
        sc.AddAgents(cfg);
        await using var sp = sc.BuildServiceProvider();

        // Build the orchestrator directly from the 4 specialists (avoids the persistence-decorated
        // IOrchestratorAgent, which would need a repo). Mirrors KcBenchHarness.BuildOrchestrator.
        var orch = new PipelineOrchestrator(
            sp.GetRequiredService<IRequirementAgent>(),
            sp.GetRequiredService<ICodingAgent>(),
            sp.GetRequiredService<ITestingAgent>(),
            sp.GetRequiredService<IQaAgent>(),
            Options.Create(new PipelineOptions { MaxIterations = 3 }),
            NullLogger<PipelineOrchestrator>.Instance);

        var runs = new List<RunRow>();
        for (var i = 1; i <= n; i++)
        {
            var runId = $"kc-live-{i}";
            PipelineResult? result = null;
            string? error = null;
            var sw = Stopwatch.StartNew();
            using (MetricsContext.BeginScope(runId, "PIPELINE", i))
            {
                try
                {
                    result = await orch.RunAsync(new UserStory(ProductStory, NMax: 3));
                }
                catch (System.Net.Http.HttpRequestException ex) { error = ex.Message; }
                catch (System.Text.Json.JsonException ex) { error = ex.Message; }
                catch (TimeoutException ex) { error = ex.Message; }
                catch (InvalidOperationException ex) { error = ex.Message; }
            }
            sw.Stop();

            var row = new RunRow(
                Index: i,
                RunId: runId,
                Ok: result is not null && result.Status == PipelineStatus.Done,
                Status: result?.Status.ToString() ?? "Exception",
                Iterations: result?.IterationCount ?? 0,
                FirstScore: result is { QaHistory.Count: > 0 } ? result.QaHistory[0].Score : null,
                FinalScore: result is { QaHistory.Count: > 0 } ? result.QaHistory[^1].Score : null,
                FinalConsistent: result is { QaHistory.Count: > 0 } && result.QaHistory[^1].IsConsistent,
                AcCount: result?.Spec?.AcceptanceCriteria.Count ?? 0,
                EntityCount: result?.Spec?.Entities.Count ?? 0,
                EndpointCount: result?.Spec?.Endpoints.Count ?? 0,
                CodeFileCount: result?.Code?.Files.Count ?? 0,
                TestFileCount: result?.Tests?.Files.Count ?? 0,
                WallMs: sw.Elapsed.TotalMilliseconds,
                Error: error,
                CodeCompiles: doBuild && result is { Code: { } cc } && TryBuild(cc, "code"),
                TestsBuild: doBuild && result is { Code: { } tc, Tests: { } tt } && TryBuild(tc, "tests", tt));

            runs.Add(row);
            WriteJson(OutPath($"kc_live/run-{i:D2}.json"), row);

            if (csv.Snapshot().Sum(m => m.CostUsd) > maxUsd)
            {
                break; // budget guard — summary + assertion below report the overrun
            }
        }

        var snapshot = csv.Snapshot();
        var summary = BuildSummary(mode, runs, snapshot);
        File.WriteAllText(OutPath("kc_live_summary.md"), summary);

        var totalCost = snapshot.Sum(m => m.CostUsd);
        totalCost.ShouldBeLessThanOrEqualTo(maxUsd, summary);
    }

    // ---- aggregation ----------------------------------------------------------------------------

    private static string BuildSummary(string mode, List<RunRow> runs, IReadOnlyList<RunMetric> snapshot)
    {
        var done = runs.Count(r => r.Ok);
        var consistent = runs.Count(r => r.FinalConsistent);
        var looped = runs.Where(r => r.Iterations >= 2 && r is { FirstScore: not null, FinalScore: not null }).ToList();
        var avgIters = runs.Count > 0 ? runs.Average(r => (double)r.Iterations) : 0;
        var deltas = looped.Select(r => (r.FinalScore!.Value - r.FirstScore!.Value) / (r.Iterations - 1) * 100).ToList();

        string lat(string agentPrefix)
        {
            var rows = snapshot.Where(m => m.AgentName.StartsWith(agentPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return rows.Count == 0 ? "n/a" : (rows.Average(m => m.LatencyMs) / 1000).ToString("F1", CultureInfo.InvariantCulture) + " s";
        }
        decimal cost(string agentPrefix)
        {
            var rows = snapshot.Where(m => m.AgentName.StartsWith(agentPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return rows.Count == 0 ? 0m : rows.Average(m => m.CostUsd);
        }
        string pct(int num, int den) => den == 0 ? "n/a" : (100.0 * num / den).ToString("F0", CultureInfo.InvariantCulture) + "%";
        string f1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

        var n = runs.Count;
        var wallAvg = runs.Count > 0 ? runs.Average(r => r.WallMs) / 1000 : 0;
        var totalCost = snapshot.Sum(m => m.CostUsd);
        var inTok = snapshot.Sum(m => (long)m.TokensIn);
        var outTok = snapshot.Sum(m => (long)m.TokensOut);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# KC live bench — measured ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Mode: **{mode}** · runs: **{n}** · total cost: **${totalCost:F2}** · tokens in/out: {inTok}/{outTok}");
        sb.AppendLine();
        sb.AppendLine("| KC | Measured | Thesis Table 2.6 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| KC1 Requirement | latency {lat("Requirement")}, AC avg {f1(Avg(runs, r => r.AcCount))}, entities avg {f1(Avg(runs, r => r.EntityCount))} | 3.4 s, AC 92%, schema 100% |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| KC2 Coding | latency {lat("Coding")}, compile-pass {(runs.Any(r => r.CodeCompiles) ? pct(runs.Count(r => r.CodeCompiles), n) : "off (KC_LIVE_BUILD!=1)")} | 5.2 s, compile 9/10 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| KC3 Testing | latency {lat("Testing")}, test files avg {f1(Avg(runs, r => r.TestFileCount))}, build {(runs.Any(r => r.TestsBuild) ? pct(runs.Count(r => r.TestsBuild), n) : "off")} | 3.1 s, runnable 87% |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| KC4 E2E | completion {pct(done, n)}, consistency {pct(consistent, n)}, wall {f1(wallAvg)} s | 9/10, consistency 90%, 15.0 s |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| KC5 Quality Loop | avg iters {f1(avgIters)}, score Δ/iter {(deltas.Count > 0 ? "+" + f1(deltas.Average()) : "n/a")}, recovery {pct(looped.Count(r => r.FinalConsistent), Math.Max(looped.Count, 1))} | 1.8 iters, +18/iter, recovery 100% |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Per-agent avg cost (USD): Requirement {cost("Requirement"):F4} · Coding {cost("Coding"):F4} · Testing {cost("Testing"):F4} · Qa {cost("Qa"):F4}");
        sb.AppendLine();
        sb.AppendLine("Per-run:");
        sb.AppendLine("| # | status | iters | finalScore | consistent | codeFiles | testFiles | wall(s) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Index} | {r.Status} | {r.Iterations} | {(r.FinalScore.HasValue ? f1(r.FinalScore.Value) : "—")} | {r.FinalConsistent} | {r.CodeFileCount} | {r.TestFileCount} | {f1(r.WallMs / 1000)} |");
        }
        return sb.ToString();
    }

    private static double Avg(List<RunRow> runs, Func<RunRow, int> sel)
        => runs.Count == 0 ? 0 : runs.Average(r => (double)sel(r));

    // ---- live build probe (opt-in) --------------------------------------------------------------

    private static bool TryBuild(CodeArtifact code, string label, TestArtifact? tests = null)
    {
        try
        {
            var dir = Path.Join(Path.GetTempPath(), $"kc-build-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            foreach (var f in code.Files)
            {
                WriteFile(dir, f);
            }
            if (tests is not null)
            {
                foreach (var f in tests.Files)
                {
                    WriteFile(dir, f);
                }
            }
            File.WriteAllText(Path.Join(dir, "Probe.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                + "<TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable>"
                + "<TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>"
                + "<ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\"/></ItemGroup></Project>");

            var psi = new ProcessStartInfo("dotnet", "build -c Debug --nologo")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(120_000) && p.ExitCode == 0;
        }
        // Probe build failed to even launch / run — treat as "does not compile".
        catch (System.ComponentModel.Win32Exception ex) { _ = ex.Message; return false; }
        catch (IOException ex) { _ = ex.Message; return false; }
        catch (InvalidOperationException ex) { _ = ex.Message; return false; }
        catch (TimeoutException ex) { _ = ex.Message; return false; }
    }

    private static void WriteFile(string root, CodeFile f)
    {
        var rel = f.Path.Replace('\\', '/').TrimStart('/');
        var full = Path.Join(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, f.Content);
    }

    // ---- config ---------------------------------------------------------------------------------

    private sealed record AgentCfg(string Name, string Provider, string Model);

    private static AgentCfg[] AgentPlan(string mode)
    {
        var sonnet = Env("ANTHROPIC_SONNET_MODEL") ?? "claude-sonnet-5";
        var haiku = Env("ANTHROPIC_HAIKU_MODEL") ?? "claude-haiku-4-5";
        var gpt41 = Env("AZURE_GPT41_DEPLOYMENT") ?? "gpt-4.1";
        var mini = Env("AZURE_GPT4OMINI_DEPLOYMENT") ?? "gpt-4o-mini";

        return mode switch
        {
            "azure" =>
            [
                new("Requirement", "AzureOpenAI", gpt41),
                new("Coding", "AzureOpenAI", gpt41),
                new("Testing", "AzureOpenAI", mini),
                new("Qa", "AzureOpenAI", mini),
            ],
            "claude" =>
            [
                new("Requirement", "Claude", sonnet),
                new("Coding", "Claude", sonnet),
                new("Testing", "Claude", haiku),
                new("Qa", "Claude", haiku),
            ],
            // hybrid (matches thesis Bảng 2.3)
            _ =>
            [
                new("Requirement", "Claude", sonnet),
                new("Coding", "AzureOpenAI", gpt41),
                new("Testing", "AzureOpenAI", mini),
                new("Qa", "Claude", haiku),
            ],
        };
    }

    private static IConfiguration BuildConfig(AgentCfg[] agents, string temp)
    {
        var s = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "Claude",
            ["Pipeline:MaxIterations"] = "3",
        };
        if (agents.Any(a => a.Provider == "Claude"))
        {
            s["Llm:Claude:ApiKey"] = Env(AnthropicKey);
            s["Llm:Claude:Model"] = Env("ANTHROPIC_SONNET_MODEL") ?? "claude-sonnet-5";
        }
        if (agents.Any(a => a.Provider == "AzureOpenAI"))
        {
            s["Llm:AzureOpenAi:ApiKey"] = Env(AzureKey);
            s["Llm:AzureOpenAi:Endpoint"] = Env(AzureEndpoint);
            s["Llm:AzureOpenAi:Model"] = Env("AZURE_GPT41_DEPLOYMENT") ?? "gpt-4.1";
        }
        foreach (var a in agents)
        {
            s[$"Agents:{a.Name}:Provider"] = a.Provider;
            s[$"Agents:{a.Name}:Model"] = a.Model;
            s[$"Agents:{a.Name}:Temperature"] = temp;
            s[$"Agents:{a.Name}:MaxTokens"] = a.Name == "Coding" ? "3000" : "1500";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(s).Build();
    }

    private static void SkipIfNotEnabled(AgentCfg[] agents)
    {
        if (Env(RunFlag) != "1")
        {
            Assert.Skip($"{RunFlag} != 1 — set to 1 to run the live KC bench (cost-bounded by KC_LIVE_MAX_USD).");
        }
        if (agents.Any(a => a.Provider == "Claude") && string.IsNullOrWhiteSpace(Env(AnthropicKey)))
        {
            Assert.Skip($"{AnthropicKey} not set (needed for the Claude agents in this mode).");
        }
        if (agents.Any(a => a.Provider == "AzureOpenAI")
            && (string.IsNullOrWhiteSpace(Env(AzureKey)) || string.IsNullOrWhiteSpace(Env(AzureEndpoint))))
        {
            Assert.Skip($"{AzureKey} / {AzureEndpoint} not set (needed for the Azure agents in this mode).");
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private sealed record RunRow(
        int Index, string RunId, bool Ok, string Status, int Iterations,
        double? FirstScore, double? FinalScore, bool FinalConsistent,
        int AcCount, int EntityCount, int EndpointCount, int CodeFileCount, int TestFileCount,
        double WallMs, string? Error, bool CodeCompiles, bool TestsBuild);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static int ParseInt(string? v, int fallback)
        => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : fallback;

    private static decimal ParseDecimal(string? v, decimal fallback)
        => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : fallback;

    private static string OutPath(string rel)
    {
        var full = Path.Join(AppContext.BaseDirectory, "TestResults", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static void WriteJson(string path, object value)
        => File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, JsonOpts));

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
