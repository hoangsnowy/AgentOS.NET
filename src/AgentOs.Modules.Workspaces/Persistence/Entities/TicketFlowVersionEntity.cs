// One saved version of a ticket flow (the per-ticket graph Autopilot runs — ADR-0016). Append-only: a save, a revert
// or "use the default flow" adds a version, so every change keeps who made it and when, and any older version can be
// brought back. Scope: BoardId = Guid.Empty is the tenant's default flow; a board id is that board's override (board
// wins). Version numbers run per tenant across every scope, so "v7" names one row of the tenant and a running ticket
// can snapshot it. Tenant-scoped (EF filter + RLS, workspaces.ticket_flows in RowLevelSecurity.Tables).

using System;

namespace AgentOs.Modules.Workspaces.Persistence.Entities;

public sealed class TicketFlowVersionEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The board the flow is for; <see cref="Guid.Empty"/> = the tenant's default.</summary>
    public Guid BoardId { get; set; }

    /// <summary>Per-tenant version number (1, 2, …) across every scope.</summary>
    public int Version { get; set; }

    /// <summary>The definition (JSON); null = this board uses the tenant's flow again.</summary>
    public string? DefinitionJson { get; set; }

    public string? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>What the change was ("Reverted to v3"), when it says more than "saved".</summary>
    public string? Note { get; set; }
}
