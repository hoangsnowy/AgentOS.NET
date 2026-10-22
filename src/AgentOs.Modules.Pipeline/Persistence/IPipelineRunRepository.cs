// Repository for pipeline run + artifact + metrics. Interface in Application (Clean Arch),
// EF Core impl in Infrastructure. Domain stays pure and knows nothing about the DB.
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Domain.Pipeline;

namespace AgentOs.Modules.Pipeline.Persistence;

/// <summary>Stores + queries the pipeline run history.</summary>
public interface IPipelineRunRepository
{
    /// <summary>Stores a single run (full PipelineResult + list of RunMetric per LLM call). Stamps the
    /// owning tenant from the ambient <c>ITenantContext</c>.</summary>
    Task SaveAsync(PipelineRunRecord record, CancellationToken ct = default);

    /// <summary>Stores a single run stamping an <b>explicit</b> tenant id rather than reading it from the
    /// ambient <c>ITenantContext</c>. Required for callers with no request scope — a Blazor circuit running
    /// the Workflow studio has a blank <c>ITenantContext</c>, so the run + its <c>run_metrics</c> rows must
    /// carry the signed-in tenant passed in, or the budget gate would never see (and so never cap) that
    /// tenant's workflow spend.</summary>
    Task SaveAsync(PipelineRunRecord record, string tenantId, CancellationToken ct = default);

    /// <summary>Stores one Board agent run (Coding, Reviewer, Testing, Discovery, Autopilot Orchestrator) with the
    /// LLM calls it made, so Insights counts it. It has no pipeline artifacts: its run detail reads as not found.</summary>
    Task SaveAgentRunAsync(AgentRunRecord record, string tenantId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Gets a single full run by Id (null if not found).</summary>
    Task<PipelineRunRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>List of the most recent runs (summary, without the artifact json), newest first.
    /// <paramref name="offset"/> skips that many rows for paging.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> ListAsync(int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>Most recent runs for one tenant, newest first. <b>Tenant-explicit</b>: the caller passes
    /// the tenant id rather than relying on the DbContext's <c>ITenantContext</c>-driven global query
    /// filter, which is blank in a Blazor circuit (no HttpContext). Use this from the desktop (Overview /
    /// run history) so the list is scoped to the signed-in tenant instead of leaking or coming back empty.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> ListForTenantAsync(
        string tenantId, int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>Aggregated LLM cost for one tenant since an optional cutoff (null = all time).
    /// Tenant-explicit: the caller passes the tenant id so this is safe to call from a Blazor
    /// circuit, where ITenantContext is blank (no HttpContext) and the DbContext's global query
    /// filter cannot resolve the tenant on its own.</summary>
    Task<CostSummary> GetCostSummaryForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>Just the total LLM spend (USD) for one tenant since an optional cutoff — a single
    /// <c>SUM(CostUsd)</c>, no breakdown buckets. The budget gate (<c>BudgetGuard</c>) fires this per run and
    /// only needs the headline number, so it skips the four extra GROUP BY round-trips that
    /// <see cref="GetCostSummaryForTenantAsync"/> does for the cost dashboard. Tenant-explicit (safe from a
    /// blank-ITenantContext Blazor circuit).</summary>
    Task<decimal> GetSpendForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>Dashboard-only aggregates the cost summary doesn't carry: average wall-clock latency per
    /// agent, the QA-loop iteration distribution (the shape behind the "average N.N rounds" number), and
    /// pipeline runs started per UTC day. Tenant-explicit + since-scoped exactly like
    /// <see cref="GetCostSummaryForTenantAsync"/>, so it is safe from a blank-ITenantContext Blazor circuit.</summary>
    Task<InsightsExtra> GetInsightsExtraForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default);
}

/// <summary>A single full run to store / read back.</summary>
public sealed record PipelineRunRecord(
    Guid Id,
    PipelineResult Result,
    IReadOnlyList<RunMetric> Metrics,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>One Board agent run and its LLM calls (see <see cref="IPipelineRunRepository.SaveAgentRunAsync"/>).</summary>
/// <param name="Label">What ran, shown in the run history (e.g. "Coding · #15 Build SVG game viewer").</param>
/// <param name="Agent">The agent name its calls are filed under when a call carries no role.</param>
public sealed record AgentRunRecord(
    Guid Id,
    string Label,
    string Agent,
    bool Success,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<AgentOs.Domain.Llm.LlmCallRecord> Calls);

/// <summary>Summary of a single run for the history list.</summary>
public sealed record PipelineRunSummary(
    Guid Id,
    string Status,
    decimal TotalCostUsd,
    int IterationCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string UserStoryPreview);

/// <summary>Aggregated LLM spend for a tenant — headline totals plus breakdowns for the cost dashboard.</summary>
public sealed record CostSummary(
    decimal TotalCostUsd,
    int TotalTokensIn,
    int TotalTokensOut,
    int CallCount,
    int RunCount,
    IReadOnlyList<CostBucket> ByAgent,
    IReadOnlyList<CostBucket> ByProvider,
    IReadOnlyList<CostBucket> ByModel,
    IReadOnlyList<CostBucket> ByDay)
{
    /// <summary>The zero summary — no metrics recorded (or no database wired).</summary>
    public static CostSummary Empty { get; } = new(0m, 0, 0, 0, 0, [], [], [], []);

    /// <summary>Prompt-cache reads across all calls (subset of <see cref="TotalTokensIn"/>).</summary>
    public long TotalCacheReadTokens { get; init; }

    /// <summary>Prompt-cache writes across all calls (subset of <see cref="TotalTokensIn"/>).</summary>
    public long TotalCacheWriteTokens { get; init; }

    /// <summary>Share of prompt tokens served from cache, 0..1.</summary>
    public double CacheHitRatio => TotalTokensIn <= 0 ? 0d : (double)TotalCacheReadTokens / TotalTokensIn;
}

/// <summary>One row of a cost breakdown grouped by agent, provider, model or day. Cache counts are subsets
/// of <paramref name="TokensIn"/>.</summary>
public sealed record CostBucket(
    string Key,
    decimal CostUsd,
    int TokensIn,
    int TokensOut,
    int Calls,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0);

/// <summary>Extra aggregates for the Insights dashboard that the cost summary doesn't compute: per-agent
/// average latency, the QA-iteration distribution, and pipeline runs per day.</summary>
public sealed record InsightsExtra(
    IReadOnlyList<AgentLatencyBucket> ByAgentLatency,
    IReadOnlyList<IterationBucket> ByIteration,
    IReadOnlyList<DayRunsBucket> RunsByDay)
{
    /// <summary>The zero result — no metrics recorded (or no database wired).</summary>
    public static InsightsExtra Empty { get; } = new([], [], []);
}

/// <summary>Average wall-clock latency (ms) for one agent across its LLM calls.</summary>
public sealed record AgentLatencyBucket(string Agent, double AvgLatencyMs, int Calls);

/// <summary>How many distinct runs (and LLM calls) reached a given QA-loop iteration.</summary>
public sealed record IterationBucket(int Iteration, int Runs, int Calls);

/// <summary>Number of pipeline runs started on one UTC calendar day.</summary>
public sealed record DayRunsBucket(string Day, int Runs);
