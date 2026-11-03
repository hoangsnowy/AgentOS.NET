// Approval policies (E1.5) — one approval request: a step of a task (plan, diff, pull request, merge) that a person with
// the approver or admin role approves or rejects on Board › Tasks. Tenant-scoped: EF query filter + explicit tenant
// predicates in the store + RLS (sessions.approvals in RowLevelSecurity.Tables).

using System;

namespace AgentOs.Modules.Sessions.Persistence.Entities;

/// <summary>An approval request for one gate of one change of a task.</summary>
public sealed class ApprovalEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid BoardId { get; set; }
    public Guid SessionId { get; set; }
    public int IssueNumber { get; set; }

    /// <summary><see cref="AgentOs.Domain.Governance.ApprovalGate"/> by name.</summary>
    public string Gate { get; set; } = string.Empty;

    /// <summary>The task's fix round it was asked for (0 for the plan).</summary>
    public int Run { get; set; }

    /// <summary><see cref="AgentOs.Domain.Governance.ApprovalStatus"/> by name.</summary>
    public string Status { get; set; } = "Pending";

    public string Title { get; set; } = string.Empty;
    public string? Link { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public string? Note { get; set; }
}
