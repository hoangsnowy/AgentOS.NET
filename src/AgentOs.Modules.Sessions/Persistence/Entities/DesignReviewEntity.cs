// Design gate (ADR-0016) — one design review: a task's test run, the screenshots it sent back (metadata; the bytes live
// in sessions.ticket_files under the design scope), the Designer agent's verdict and the owner's decision. Tenant-scoped:
// EF query filter + explicit tenant predicates in the store + RLS (sessions.design_reviews in RowLevelSecurity.Tables).

using System;

namespace AgentOs.Modules.Sessions.Persistence.Entities;

/// <summary>A design review of one test run of a task.</summary>
public sealed class DesignReviewEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid BoardId { get; set; }
    public Guid SessionId { get; set; }
    public int IssueNumber { get; set; }

    /// <summary>The task's fix round when it was tested (0 = the first attempt) — part of the screenshots' scope key.</summary>
    public int Run { get; set; }

    /// <summary><see cref="AgentOs.Domain.Sessions.DesignReviewStatus"/> by name.</summary>
    public string Status { get; set; } = "Pending";

    public string Summary { get; set; } = string.Empty;

    /// <summary>The findings (screen, problem, fix) as JSON.</summary>
    public string FindingsJson { get; set; } = "[]";

    /// <summary>The screenshots (path, screen, width, theme) as JSON.</summary>
    public string ShotsJson { get; set; } = "[]";

    public string? Error { get; set; }

    /// <summary>Automatic re-runs of a check that couldn't run (<see cref="AgentOs.Domain.Sessions.DesignReview.MaxAutomaticRetries"/>).</summary>
    public int Retries { get; set; }

    /// <summary><see cref="AgentOs.Domain.Sessions.DesignDecision"/> by name, or null when the owner hasn't decided.</summary>
    public string? Decision { get; set; }

    public string? DecisionNote { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
