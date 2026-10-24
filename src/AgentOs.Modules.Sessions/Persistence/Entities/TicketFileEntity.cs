// Ticket files (ADR-0015) — one stored file of a scope (a ticket, a task, a New idea draft). Tenant-scoped: EF query
// filter + explicit tenant predicates in the store + RLS (sessions.ticket_files in RowLevelSecurity.Tables). The
// content lives in the row (bytea): files are small and bounded by the tenant's limits (TicketFiles:*), and a ticket's
// files travel together with its runs.

using System;

namespace AgentOs.Modules.Sessions.Persistence.Entities;

/// <summary>A file a person attached to a ticket, delivered to every agent run of it.</summary>
public sealed class TicketFileEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Whose files these are (<see cref="AgentOs.Domain.Files.TicketFileScope"/>).</summary>
    public string ScopeKey { get; set; } = string.Empty;

    /// <summary>The normalised relative path (<see cref="AgentOs.Domain.Files.TicketFilePath"/>).</summary>
    public string Path { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];
    public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Content"/>.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";
    public string UploadedBy { get; set; } = string.Empty;
    public DateTimeOffset UploadedAtUtc { get; set; }
}
