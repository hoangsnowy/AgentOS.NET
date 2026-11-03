// Self-service signup orchestrator. Dispatches one of three modes based on the request shape:
//   - Invite: caller supplies an invitation token; joins an existing tenant.
//   - Slug: caller supplies an explicit tenant id; first user becomes the tenant Admin.
//   - AutoCreate: no slug, no invite; service generates a unique slug and the user is Admin.
// All three modes provision the Keycloak user FIRST and then write the registry row, with a
// Keycloak rollback if the registry write throws.
// Invitations (GAP-11) are rows, not stateless tokens: the token is a signed, time-limited envelope around the row's id
// and tenant. An invitation is single-use (consumed on accept), bound to the address it was sent to (accepted only for
// that address, case-insensitive), revocable by a tenant admin until it is used, and expires as before (1–168 h).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants;

/// <summary>Self-service signup orchestrator. See file header for the dispatch contract.</summary>
public interface ITenantSignupService
{
    /// <summary>Provision the Keycloak user + (when needed) the tenant row, picking the mode from
    /// the request shape. Throws <see cref="InvalidOperationException"/> with a user-readable
    /// message on validation / invitation / collision errors.</summary>
    Task<TenantSignupOutcome> SignupAsync(TenantSignupRequest request, CancellationToken ct = default);

    /// <summary>True when anyone may create a NEW workspace (the platform setting <c>Tenants:OpenSignup</c>, GAP-01).
    /// When false, <see cref="SignupAsync"/> accepts only invitations and throws <see cref="SignupClosedException"/>
    /// otherwise.</summary>
    Task<bool> IsOpenSignupEnabledAsync(CancellationToken ct = default);

    /// <summary>Create a single-use invitation to <paramref name="tenantId"/> as <paramref name="role"/> for
    /// <paramref name="email"/>, valid for <paramref name="ttl"/> (1–168 h), and audit it. Throws
    /// <see cref="ArgumentException"/> for a bad role, address or lifetime.</summary>
    Task<InvitationToken> CreateInvitationAsync(
        string tenantId, string role, string email, TimeSpan ttl, AuditActor actor, CancellationToken ct = default);

    /// <summary>Read an invitation link without using it: null when the token is missing, malformed, tampered, from
    /// before invitations were single-use, or names no invitation. A used, revoked or expired one IS returned, with its
    /// <see cref="InvitationPreview.Status"/>, so the page can say why it can't be used. Never throws for a bad token.</summary>
    Task<InvitationPreview?> PreviewInvitationAsync(string? token, CancellationToken ct = default);

    /// <summary>The tenant's invitations, newest first (any state).</summary>
    Task<IReadOnlyList<InvitationInfo>> ListInvitationsAsync(string tenantId, int max = 100, CancellationToken ct = default);

    /// <summary>Withdraw an unused invitation and audit it. False when it doesn't exist in this tenant, was already
    /// used, or was already revoked.</summary>
    Task<bool> RevokeInvitationAsync(string tenantId, Guid invitationId, AuditActor actor, CancellationToken ct = default);
}

/// <summary>Who did something, for the audit row.</summary>
public sealed record AuditActor(string? UserId, string? IpAddress = null, string? UserAgent = null);

/// <summary>Open self-signup is turned off on this server (GAP-01): only an invitation can create an account.</summary>
public sealed class SignupClosedException : InvalidOperationException
{
    /// <summary>The user-facing message.</summary>
    public const string DefaultMessage = "Creating a new workspace is turned off on this server. Ask an administrator for an invitation.";

    /// <inheritdoc />
    public SignupClosedException() : base(DefaultMessage) { }

    /// <inheritdoc />
    public SignupClosedException(string message) : base(message) { }

    /// <inheritdoc />
    public SignupClosedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Inputs for a single self-service signup attempt.</summary>
public sealed record TenantSignupRequest(
    string Username,
    string Password,
    string? Email,
    string? TenantId,
    string? TenantName,
    string? InviteToken);

/// <summary>Result of a successful signup.</summary>
public sealed record TenantSignupOutcome(string TenantId, string KeycloakUserId, SignupMode Mode);

/// <summary>A minted invitation: its id, the token for the link, and when it expires.</summary>
public sealed record InvitationToken(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Where an invitation stands.</summary>
public enum InvitationStatus
{
    /// <summary>Not used yet, not revoked, not expired: it can be accepted.</summary>
    Pending = 0,

    /// <summary>Used — it created an account (single-use).</summary>
    Accepted = 1,

    /// <summary>Withdrawn by an administrator before it was used.</summary>
    Revoked = 2,

    /// <summary>Past its expiry without being used.</summary>
    Expired = 3,
}

/// <summary>An invitation link, decoded and looked up. The page pre-fills the (bound) email from it.</summary>
public sealed record InvitationPreview(Guid Id, string TenantId, string Role, string Email, DateTimeOffset ExpiresAtUtc, InvitationStatus Status)
{
    /// <summary>True when the invitation can still be accepted.</summary>
    public bool CanAccept => Status == InvitationStatus.Pending;
}

/// <summary>One invitation in a tenant admin's list.</summary>
public sealed record InvitationInfo(
    Guid Id,
    string TenantId,
    string Email,
    string Role,
    string? CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    InvitationStatus Status);

/// <summary>Which branch of <see cref="ITenantSignupService.SignupAsync"/> ran.</summary>
public enum SignupMode
{
    /// <summary>An invitation token resolved the tenant + role.</summary>
    Invite = 0,
    /// <summary>Caller supplied an explicit slug; tenant was created and caller became Admin.</summary>
    Slug = 1,
    /// <summary>Caller supplied no slug; a fresh slug was generated and caller became Admin.</summary>
    AutoCreate = 2,
}
