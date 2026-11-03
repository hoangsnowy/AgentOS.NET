// Audit-trail row for tenant-scoped mutations. Append-only: writers never UPDATE/DELETE these, and the app role may not
// (REVOKE UPDATE, DELETE, TRUNCATE in the RLS bootstrap — GAP-11b). Tamper-evident: each row carries its place in its
// tenant's chain (Sequence 1, 2, 3 …), the previous row's hash and its own (AuditChain). Rows written before the chain
// existed have no Sequence/Hash and are reported as "not chained".
// Stored in the tenants schema alongside the registry so the same DbContext owns the migration.

using System;

namespace AgentOs.Modules.Tenants.Persistence.Entities;

internal sealed class AuditEventEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>Position in the tenant's chain, from 1; null for a row written before the chain (legacy).</summary>
    public long? Sequence { get; set; }

    /// <summary>The previous row's <see cref="Hash"/> (<see cref="AuditChain.Genesis"/> for the first).</summary>
    public string? PrevHash { get; set; }

    /// <summary>SHA-256 (hex) over this row's content and <see cref="PrevHash"/> — see <see cref="AuditChain"/>.</summary>
    public string? Hash { get; set; }
}
