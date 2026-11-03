// GAP-11 — one invitation = one row. The emailed token carries only this row's id (and its tenant, signed), so an
// invitation is single-use (consumed on accept), bound to the invited address (EmailNormalized), revocable by a tenant
// admin before it is used, and expires as before. Tenant-scoped: TenantId on every row, under RLS (ADR-0012).

using System;

namespace AgentOs.Modules.Tenants.Persistence.Entities;

internal sealed class InvitationEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The invited address as the admin typed it (trimmed) — what the invitee sees and signs up with.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary><see cref="Invitations.InvitationRules.NormalizeEmail"/> of <see cref="Email"/> — what acceptance compares.</summary>
    public string EmailNormalized { get; set; } = string.Empty;

    public string Role { get; set; } = "member";
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Set when the invitation is consumed (claimed at the start of an accept; released if that accept fails).</summary>
    public DateTimeOffset? AcceptedAtUtc { get; set; }

    /// <summary>The account the invitation created (Keycloak user id), once the accept completed.</summary>
    public string? AcceptedUserId { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}
