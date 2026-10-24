// Versioned storage of ticket flows (ADR-0014). The store keeps JSON and who/when; what a flow means (its steps and
// safety invariants) belongs to the Pipeline module's TicketFlowDefinition, validated by the Web's TicketFlowService
// before anything is appended. Every method is tenant-explicit (a Blazor circuit and the Autopilot worker have no
// HttpContext). EF (Postgres) when a database is configured, process-local otherwise (the standalone Web).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Workspaces.Persistence;

/// <summary>One saved version of a ticket flow.</summary>
/// <param name="BoardId">The board it is for; <see cref="Guid.Empty"/> = the tenant's default.</param>
/// <param name="Version">Per-tenant version number.</param>
/// <param name="DefinitionJson">The definition; null = the board uses the tenant's flow again.</param>
/// <param name="CreatedByUserId">Who saved it.</param>
/// <param name="CreatedByName">Their name, as shown.</param>
/// <param name="CreatedAtUtc">When.</param>
/// <param name="Note">What the change was, when it says more than "saved".</param>
public sealed record TicketFlowVersion(Guid BoardId, int Version, string? DefinitionJson, string? CreatedByUserId, string? CreatedByName,
    DateTimeOffset CreatedAtUtc, string? Note);

/// <summary>Append-only, versioned ticket flows per tenant and board.</summary>
/// <summary>A save lost the version-number race to other saves four times in a row.</summary>
public sealed class TicketFlowSaveConflictException : InvalidOperationException
{
    /// <summary>Create.</summary>
    public TicketFlowSaveConflictException() : base("Someone else saved the ticket flow at the same moment.") { }

    /// <summary>Create with a message.</summary>
    public TicketFlowSaveConflictException(string message) : base(message) { }

    /// <summary>Create from the database error.</summary>
    public TicketFlowSaveConflictException(Exception innerException)
        : base("Someone else saved the ticket flow at the same moment.", innerException) { }

    /// <summary>Create with a message and the database error.</summary>
    public TicketFlowSaveConflictException(string message, Exception innerException) : base(message, innerException) { }
}

public interface ITicketFlowStore
{
    /// <summary>The newest version saved for exactly this scope (a board, or <see cref="Guid.Empty"/> for the tenant).</summary>
    Task<TicketFlowVersion?> CurrentAsync(string tenantId, Guid boardId, CancellationToken ct = default);

    /// <summary>One version of the tenant (any scope), or null.</summary>
    Task<TicketFlowVersion?> GetVersionAsync(string tenantId, int version, CancellationToken ct = default);

    /// <summary>The scope's versions, newest first.</summary>
    Task<IReadOnlyList<TicketFlowVersion>> HistoryAsync(string tenantId, Guid boardId, int max = 20, CancellationToken ct = default);

    /// <summary>Append a version to the scope; returns it with its number.</summary>
    Task<TicketFlowVersion> AppendAsync(string tenantId, Guid boardId, string? definitionJson, string? userId, string? userName, string? note,
        CancellationToken ct = default);
}
