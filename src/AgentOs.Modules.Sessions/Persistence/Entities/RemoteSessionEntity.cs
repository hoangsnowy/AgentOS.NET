// M3 — a remote session = one unit of work, a member × workspace (schema sessions.sessions). It is the
// durable record of "member M wants to act on workspace W"; live execution is dispatched to that
// member's runner. Tenant-stamped on write; reads are tenant-filtered by the DbContext query filter.

using System;

namespace AgentOs.Modules.Sessions.Persistence.Entities;

/// <summary>A persisted remote session = a member's unit of work against one connected workspace.</summary>
public sealed class RemoteSessionEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The connected workspace (Workspaces module) this session acts on.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The member (token <c>sub</c>) who owns the session; their runner executes it.</summary>
    public string MemberUserId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Draft → Active → Closed (or Failed).</summary>
    public string Status { get; set; } = "Draft";

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    // M5 — run result fields, populated after the issue-work agent completes.
    public int? IssueNumber { get; set; }
    public string? PrUrl { get; set; }
    public string? Error { get; set; }

    // Board reshape — the PRIMARY repo this session targets (the ticket's own repo). A session can now
    // fan out to N repos (SessionRepoEntity children); these stay as the primary for back-compat
    // display. Nullable for pre-board / migrated sessions.
    public string? RepoOwner { get; set; }
    public string? RepoName { get; set; }
    public string? RepoDefaultBranch { get; set; }

    // Multi-repo — the board item this session came from (for traceability) + its kind.
    public string? BoardItemNodeId { get; set; }
    public string? TicketKind { get; set; }

    // "Run on my machine" — when true, the issue-work loop is routed to the member's paired dev-machine
    // CLI (claude-code / codex) via the RemoteAgent provider instead of the server LLM, spending zero
    // server tokens. Chosen per session at create time; remembered so a re-run keeps the choice.
    public bool RunOnMachine { get; set; }

    // Coherence Phase 2 — which engine runs this session. "Quick" = IssueWorkAgent (an edit loop on the
    // paired runner; the default). "Quality" = the 5-agent SDLC pipeline (server-side, greenfield only,
    // gated by the ticket-type router). Chosen per session at create time; remembered for re-runs.
    public string Brain { get; set; } = "Quick";

    // The ticket's `type:*` label (e.g. "type:feature"/"type:bug") captured at create time, so the
    // greenfield router can hard-gate the Quality engine even on the unattended "Run all ai:ready" path
    // where there is no per-ticket UI. Null for legacy/migrated sessions or tickets with no type label.
    public string? TicketType { get; set; }

    // Delivery metrics (Insights › Delivery): how many times the change went back to the Coding agent — red checks,
    // conflicts, review, acceptance or a person's "Ask for changes" — and when its last change merged.
    public int FixRounds { get; set; }
    public DateTimeOffset? MergedAtUtc { get; set; }

    // The ticket flow (workspaces.ticket_flows) version Autopilot started this task with (ADR-0014): the task keeps
    // running that flow — through the MAF workflow and, after a restart, the step-by-step rules — even when the flow is
    // changed meanwhile. 0 = the built-in flow; null = a task started before ticket flows (it follows the current one).
    public int? TicketFlowVersion { get; set; }
}
