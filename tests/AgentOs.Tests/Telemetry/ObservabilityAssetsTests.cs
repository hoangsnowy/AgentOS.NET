// E1.10 — guards the operator assets in deploy/observability against drift from the code: every AgentOS.NET
// metric a Grafana dashboard, a Prometheus rule or the Azure workbook queries must be one the code actually emits
// (an instrument constant, translated the way OTLP → Prometheus translates it). Renaming an instrument without
// updating the dashboards fails here, not silently on an operator's empty panel.
// PromQL syntax + alert behaviour are checked by `promtool check rules` / `promtool test rules` (docs/OPERATIONS.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.ServiceDefaults;
using AgentOs.SharedKernel.Telemetry;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Telemetry;

public sealed partial class ObservabilityAssetsTests
{
    // OTel instrument → the Prometheus series names it produces (unit suffix, _total on counters, histogram parts).
    private static readonly (string Instrument, string[] Series)[] Emitted =
    [
        (LlmTelemetry.Instruments.TokenUsage, Histogram("gen_ai_client_token_usage")),
        (LlmTelemetry.Instruments.OperationDuration, Histogram("gen_ai_client_operation_duration_seconds")),
        (LlmTelemetry.Instruments.CostUsd, Histogram("agentos_llm_cost_usd")),
        (LlmTelemetry.Instruments.Calls, ["agentos_llm_calls_total"]),
        (LlmTelemetry.Instruments.Tokens, ["agentos_llm_tokens_total"]),
        (LlmTelemetry.Instruments.Errors, ["agentos_llm_errors_total"]),
        (PipelineTelemetry.Instruments.Runs, ["agentos_pipeline_runs_total"]),
        (PipelineTelemetry.Instruments.RunDuration, Histogram("agentos_pipeline_run_duration_seconds")),
        (PipelineTelemetry.Instruments.BudgetBlocked, ["agentos_budget_blocked_total"]),
        (PipelineTelemetry.Instruments.BudgetUtilization, ["agentos_budget_utilization_ratio"]),
        (HealthMetricsPublisher.InstrumentName, ["agentos_health_status"]),
        ("agentos.evidence.inline_writes", ["agentos_evidence_inline_writes_total"]),
        ("agentos.evidence.lost", ["agentos_evidence_lost_total"]),
    ];

    private static string[] Histogram(string name) => [$"{name}_bucket", $"{name}_sum", $"{name}_count"];

    private static readonly HashSet<string> KnownSeries =
        Emitted.SelectMany(e => e.Series).ToHashSet(StringComparer.Ordinal);

    // Tag keys the instruments carry, as Prometheus labels (dots → underscores).
    private static readonly HashSet<string> KnownLabels = new(StringComparer.Ordinal)
    {
        "gen_ai_system", "gen_ai_request_model", "gen_ai_agent_name", "gen_ai_token_type", "agentos_token_class",
        "agentos_run_kind", "agentos_run_outcome", "agentos_budget_scope", "agentos_budget_enforced", "agentos_tenant_id",
        "agentos_health_check",
    };

    private static readonly HashSet<string> KnownInstruments =
        Emitted.Select(e => e.Instrument).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\b(?:agentos|gen_ai)_[a-z0-9_]+\b")]
    private static partial Regex OwnSeries();

    [GeneratedRegex(@"name\s*(?:==|in\s*\()\s*""((?:agentos|gen_ai)\.[a-z0-9_.]+)""")]
    private static partial Regex OwnInstrumentInKql();

    [Fact]
    public void GrafanaDashboards_AreValidJson_AndQueryOnlyEmittedMetrics()
    {
        var files = Directory.GetFiles(Path.Join(RepoRoot(), "deploy", "observability", "grafana"), "*.json");
        files.Length.ShouldBeGreaterThanOrEqualTo(3);

        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            root.GetProperty("uid").GetString().ShouldNotBeNullOrWhiteSpace();
            root.GetProperty("title").GetString()!.ShouldStartWith("AgentOS.NET");

            var ids = new HashSet<int>();
            foreach (var panel in root.GetProperty("panels").EnumerateArray())
            {
                ids.Add(panel.GetProperty("id").GetInt32()).ShouldBeTrue($"{Path.GetFileName(file)}: duplicate panel id");
                if (!panel.TryGetProperty("targets", out var targets)) { continue; }
                foreach (var target in targets.EnumerateArray())
                {
                    AssertOnlyEmitted(target.GetProperty("expr").GetString()!, file);
                }
            }
        }
    }

    [Fact]
    public void PrometheusRules_QueryOnlyEmittedMetrics_AndLinkARunbookThatExists()
    {
        var root = RepoRoot();
        var rules = File.ReadAllText(Path.Join(root, "deploy", "observability", "prometheus", "alerts.yml"));
        var operations = File.ReadAllText(Path.Join(root, "docs", "OPERATIONS.md"));

        AssertOnlyEmitted(rules.Replace("agentos:", "recorded:", StringComparison.Ordinal), "alerts.yml");

        var anchors = Regex.Matches(rules, @"OPERATIONS\.md#(runbook-[a-z0-9]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        anchors.Count.ShouldBeGreaterThanOrEqualTo(15);
        foreach (var anchor in anchors)
        {
            operations.ShouldContain($"<a id=\"{anchor}\"></a>", customMessage: $"docs/OPERATIONS.md has no runbook anchor {anchor}");
        }
    }

    [Fact]
    public void AzureWorkbook_IsValidJson_AndQueriesOnlyEmittedInstruments()
    {
        var path = Path.Join(RepoRoot(), "deploy", "observability", "azure", "agentos-workbook.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("version").GetString().ShouldBe("Notebook/1.0");

        var bicep = File.ReadAllText(Path.Join(RepoRoot(), "infra", "observability", "observability.module.bicep"));
        foreach (var source in new[] { File.ReadAllText(path), bicep })
        {
            foreach (Match m in OwnInstrumentInKql().Matches(source))
            {
                KnownInstruments.ShouldContain(m.Groups[1].Value, $"query references unknown instrument {m.Groups[1].Value}");
            }
        }
    }

    private static void AssertOnlyEmitted(string text, string where)
    {
        foreach (Match m in OwnSeries().Matches(text))
        {
            (KnownSeries.Contains(m.Value) || KnownLabels.Contains(m.Value))
                .ShouldBeTrue($"{Path.GetFileName(where)} queries {m.Value}, which no instrument emits");
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }
}
