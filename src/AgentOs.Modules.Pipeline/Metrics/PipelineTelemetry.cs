// E1.10 — run- and budget-level OpenTelemetry metrics, so operators can alert on what an owner actually cares
// about (docs/OPERATIONS.md): how runs END (done / max iterations / failed / blocked by budget / refused by
// admission), how long they take (the run-latency SLO), and how close each tenant is to its monthly cap.
// One Meter "AgentOs.Pipeline", registered in ServiceDefaults. Static + process-lifetime like LlmTelemetry.

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;

namespace AgentOs.Modules.Pipeline.Metrics;

/// <summary>Emits the <c>agentos.pipeline.*</c> + <c>agentos.budget.*</c> instruments.</summary>
public static class PipelineTelemetry
{
    /// <summary>The Meter name. Register with <c>.AddMeter("AgentOs.Pipeline")</c> (done in ServiceDefaults).</summary>
    public const string MeterName = "AgentOs.Pipeline";

    /// <summary>Instrument names (Prometheus: dots → underscores, counters gain <c>_total</c>).</summary>
    public static class Instruments
    {
        /// <summary>Counter: runs finished, by <c>agentos.run.kind</c> + <c>agentos.run.outcome</c>.</summary>
        public const string Runs = "agentos.pipeline.runs";
        /// <summary>Histogram (s): wall time of runs that started, by kind + outcome.</summary>
        public const string RunDuration = "agentos.pipeline.run.duration";
        /// <summary>Counter: runs / agent calls refused because the tenant is over its enforced cap.</summary>
        public const string BudgetBlocked = "agentos.budget.blocked";
        /// <summary>Gauge (ratio): month-to-date spend ÷ cap per tenant, refreshed on every budget evaluation.</summary>
        public const string BudgetUtilization = "agentos.budget.utilization";
    }

    /// <summary><c>agentos.run.kind</c> values.</summary>
    public static class Kinds
    {
        /// <summary>The 5-agent pipeline (Api /pipeline, Agents window).</summary>
        public const string Pipeline = "pipeline";
        /// <summary>A Workflow studio graph run.</summary>
        public const string Workflow = "workflow";
    }

    /// <summary><c>agentos.run.outcome</c> values.</summary>
    public static class Outcomes
    {
        /// <summary>QA passed / the graph reached End.</summary>
        public const string Done = "done";
        /// <summary>The QA loop hit NMax while QA still failed.</summary>
        public const string MaxIterations = "max_iterations";
        /// <summary>The run started and failed (an agent error, malformed output, a node failure).</summary>
        public const string Failed = "failed";
        /// <summary>Refused before any agent ran: the tenant is over its enforced monthly cap.</summary>
        public const string BudgetBlocked = "budget_blocked";
        /// <summary>Refused before any agent ran: per-tenant admission (concurrency / burst).</summary>
        public const string Refused = "refused";
        /// <summary>The caller cancelled the run.</summary>
        public const string Cancelled = "cancelled";
    }

    /// <summary><c>agentos.budget.scope</c> values on <see cref="Instruments.BudgetBlocked"/>.</summary>
    public static class BudgetScopes
    {
        /// <summary>A whole pipeline / workflow run was refused.</summary>
        public const string Run = "run";
        /// <summary>One agent call (a Board agent) was refused.</summary>
        public const string Agent = "agent";
    }

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Runs =
        Meter.CreateCounter<long>(Instruments.Runs, unit: "{run}", description: "Pipeline / workflow runs finished, by outcome.");
    private static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>(Instruments.RunDuration, unit: "s", description: "Wall time of a run, by outcome.",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200] });
    private static readonly Counter<long> BudgetBlocked =
        Meter.CreateCounter<long>(Instruments.BudgetBlocked, unit: "{refusal}", description: "Runs / agent calls refused because the tenant is over its enforced monthly LLM budget.");
    private static readonly Gauge<double> BudgetUtilization =
        Meter.CreateGauge<double>(Instruments.BudgetUtilization, unit: "1", description: "Month-to-date LLM spend divided by the tenant's monthly cap (1 = at cap).");

    /// <summary>The outcome value for a finished pipeline result.</summary>
    public static string OutcomeOf(PipelineStatus status) => status switch
    {
        PipelineStatus.Done => Outcomes.Done,
        PipelineStatus.MaxIterationReached => Outcomes.MaxIterations,
        _ => Outcomes.Failed,
    };

    /// <summary>The outcome value for a finished pipeline result (null — never expected — counts as failed).</summary>
    public static string OutcomeOf(PipelineResult? result) => result is null ? Outcomes.Failed : OutcomeOf(result.Status);

    /// <summary>Counts one finished run. <paramref name="duration"/> is null for a run refused before it started
    /// (budget / admission) — those count, but never skew the latency histogram.</summary>
    public static void RecordRun(string kind, string outcome, TimeSpan? duration = null)
    {
        var tags = new TagList
        {
            { "agentos.run.kind", kind },
            { "agentos.run.outcome", outcome },
        };
        Runs.Add(1, tags);
        if (duration is { } d)
        {
            RunDuration.Record(Math.Max(d.TotalSeconds, 0d), tags);
        }
    }

    /// <summary>Counts one refusal by the budget gate. <paramref name="agent"/> names the agent for an agent-scoped one.</summary>
    public static void RecordBudgetBlocked(string scope, string? agent = null)
    {
        var tags = new TagList { { "agentos.budget.scope", scope } };
        if (!string.IsNullOrWhiteSpace(agent))
        {
            tags.Add("gen_ai.agent.name", agent);
        }
        BudgetBlocked.Add(1, tags);
    }

    /// <summary>Publishes a tenant's spend ÷ cap. Skipped when no cap is set (nothing to alert on).</summary>
    public static void RecordBudget(string tenantId, BudgetStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.CapUsd <= 0m || string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }
        BudgetUtilization.Record(
            (double)(status.SpentUsd / status.CapUsd),
            new TagList
            {
                { "agentos.tenant.id", tenantId },
                { "agentos.budget.enforced", status.EnforceOn },
            });
    }
}
