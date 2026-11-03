// Append-only audit trail for tenant-scoped mutations. Best-effort: a write failure must NEVER
// roll back the surrounding operation — the audit row is observability, not durability. Reads
// are tenant-scoped; the implementation enforces the filter on the way out.
// GAP-11b: append-only in the database too (the app role may only INSERT and SELECT) and tamper-evident — each tenant's
// events form a hash chain (AuditChain) that IAuditChainVerifier walks on demand (Settings › Activity log › Verify).

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

/// <summary>Verifies a tenant's audit hash chain (GAP-11b).</summary>
public interface IAuditChainVerifier
{
    /// <summary>Walk <paramref name="tenantId"/>'s chain (or <see cref="AuditScopes.Platform"/>'s) oldest first and report
    /// the first event that doesn't hold. The caller authorizes: a tenant admin their own tenant, a platform admin the
    /// platform scope.</summary>
    Task<AuditChainReport> VerifyAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>One row in the audit trail. <paramref name="Sequence"/> and <paramref name="Hash"/> are its place in the
/// tenant's hash chain — set by the store on read, ignored on write (null for an event recorded before the chain).</summary>
public sealed record AuditEntry(
    Guid Id,
    string TenantId,
    string? UserId,
    string Action,
    string? Target,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset TimestampUtc,
    long? Sequence = null,
    string? Hash = null);

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

    /// <summary>An administrator withdrew an unused invitation (GAP-11).</summary>
    public const string InvitationRevoked = "invitation.revoked";

    /// <summary>An invitation was used: it created this account (single-use, GAP-11).</summary>
    public const string InvitationAccepted = "invitation.accepted";

    /// <summary>Someone tried to use an invitation that was used, revoked, expired, or sent to another address (GAP-11).</summary>
    public const string InvitationRefused = "invitation.refused";
    public const string LoginFailed = "login.failed";
    public const string SettingChanged = "setting.changed";
    public const string TenantPurged = "tenant.purged";
    public const string TenantExported = "tenant.exported";
    public const string ApiTokenCreated = "apitoken.created";
    public const string ApiTokenRevoked = "apitoken.revoked";
    public const string TicketFlowChanged = "ticketflow.changed";

    /// <summary>The owner approved a design, or asked for design changes (Board › Tasks, ADR-0016).</summary>
    public const string DesignDecided = "design.decided";

    /// <summary>New screenshots cleared an earlier design approval — it stands only on the screens it was given on.</summary>
    public const string DesignApprovalCleared = "design.approval_cleared";
}

/// <summary>Audit "tenant" ids that are not tenants.</summary>
public static class AuditScopes
{
    /// <summary>Platform-wide events (platform settings). Tenant slugs never contain '_', so no tenant can own it.</summary>
    public const string Platform = "_platform";
}
