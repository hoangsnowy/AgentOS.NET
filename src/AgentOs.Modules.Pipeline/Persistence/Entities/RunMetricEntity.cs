// Persistence entity for a single LLM call (maps 1-1 from Application.Metrics.RunMetric).
// Relational → run_metrics table, SQL queries for cost-report / kc-bench.
namespace AgentOs.Modules.Pipeline.Persistence.Entities;

internal sealed class RunMetricEntity
{
    public long Id { get; set; }

    /// <summary>Owning tenant — stamped on insert; denormalized from the parent run for analytics
    /// queries that group cost by tenant without joining pipeline_runs.</summary>
    public string TenantId { get; set; } = AgentOs.SharedKernel.Identity.ITenantContext.DefaultTenantId;

    public Guid RunId { get; set; }

    public string KcId { get; set; } = string.Empty;

    public int Iteration { get; set; }

    public string AgentName { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public int TokensIn { get; set; }

    public int TokensOut { get; set; }

    /// <summary>Prompt-cache reads (subset of <see cref="TokensIn"/>).</summary>
    public int CacheReadTokens { get; set; }

    /// <summary>Prompt-cache writes (subset of <see cref="TokensIn"/>).</summary>
    public int CacheWriteTokens { get; set; }

    /// <summary>Reasoning / thinking tokens (subset of <see cref="TokensOut"/>).</summary>
    public int ReasoningTokens { get; set; }

    public double LatencyMs { get; set; }

    public decimal CostUsd { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}
