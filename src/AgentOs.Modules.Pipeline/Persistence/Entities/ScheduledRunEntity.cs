// A scheduled pipeline run (pipeline.scheduled_runs). Tenant-scoped: global query filter + Postgres RLS.

namespace AgentOs.Modules.Pipeline.Persistence.Entities;

internal sealed class ScheduledRunEntity
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The user story the pipeline runs.</summary>
    public string Story { get; set; } = string.Empty;

    /// <summary><c>Once</c> | <c>Cron</c>.</summary>
    public string Kind { get; set; } = "Cron";

    public string? Cron { get; set; }

    public DateTimeOffset? RunAtUtc { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Next occurrence; null when nothing is left to run (a fired one-off, or paused).</summary>
    public DateTimeOffset? NextRunAtUtc { get; set; }

    public DateTimeOffset? LastRunAtUtc { get; set; }

    public string? LastStatus { get; set; }

    /// <summary>Whose identity the run executes under (audit + budget attribution).</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
