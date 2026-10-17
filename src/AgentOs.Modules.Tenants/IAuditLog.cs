// Append-only audit trail for tenant-scoped mutations. Best-effort: a write failure must NEVER
// roll back the surrounding operation — the audit row is observability, not durability. Reads
// are tenant-scoped; the implementation enforces the filter on the way out.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants;

/// <summary>Append-only audit trail; writes are best-effort, reads are tenant-scoped.</summary>
public interface IAuditLog
{
    /// <summary>Persist one audit row. Implementations swallow exceptions and log them — never throw.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>List recent audit rows for one tenant, newest first.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default);
}

/// <summary>One row in the audit trail.</summary>
public sealed record AuditEntry(
    Guid Id,
    string TenantId,
    string? UserId,
    string Action,
    string? Target,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset TimestampUtc);

/// <summary>Canonical action strings; keep in sync with anything consuming the audit table.</summary>
public static class AuditActions
{
    public const string SignupCompleted = "signup.completed";
    public const string TenantCreated = "tenant.created";
    public const string MemberInvited = "member.invited";
    public const string MemberRoleChanged = "member.role_changed";
    public const string MemberRemoved = "member.removed";
    public const string MemberPasswordReset = "member.password_reset";
    public const string InvitationMinted = "invitation.minted";
    public const string LoginFailed = "login.failed";
    public const string SettingChanged = "setting.changed";
    public const string TenantPurged = "tenant.purged";
    public const string TenantExported = "tenant.exported";
    public const string ApiTokenCreated = "apitoken.created";
    public const string ApiTokenRevoked = "apitoken.revoked";
}

/// <summary>Audit "tenant" ids that are not tenants.</summary>
public static class AuditScopes
{
    /// <summary>Platform-wide events (platform settings). Tenant slugs never contain '_', so no tenant can own it.</summary>
    public const string Platform = "_platform";
}
