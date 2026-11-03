// Default impl of ITenantSignupService. Keycloak-first ordering with rollback so a registry-row
// failure never leaves the tenant claim orphaned without a corresponding row.
// Invitations (GAP-11) are rows in IInvitationStore. The emailed token is a time-limited DataProtection envelope around
// {invitation id, tenant} only — the row is the authority for role, address and state. Accepting one:
//   read the signed token → bind RLS to the tenant it names (the invitee is anonymous) → the row must be Pending and the
//   address must be the invited one (InvitationRules) → CLAIM it (one conditional UPDATE: a second accept, a revoke or
//   the expiry can't race it) → create the Keycloak account → bind the account to the row. A failed account creation
//   releases the claim so the invitee can retry; a refused attempt is audited.
// Tokens minted before this change (purpose …Invitation.v1: stateless and reusable) no longer open — re-invite.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Invitations;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Logging;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tenants;

internal sealed partial class TenantSignupService : ITenantSignupService
{
    private const string ProtectorPurpose = "AgentOs.Modules.Tenants.Invitation.v2";

    /// <summary>The longest an invitation may live (7 days).</summary>
    public static readonly TimeSpan MaxInvitationLifetime = TimeSpan.FromHours(168);

    private readonly ITenantsRepository _repo;
    private readonly IKeycloakAdminClient _kc;
    private readonly IAuditLog _audit;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<TenantSignupService> _logger;
    private readonly AgentOs.Domain.Configuration.ISettings? _settings;
    private readonly IInvitationStore _invitations;
    private readonly TimeProvider _clock;

    public TenantSignupService(
        ITenantsRepository repo,
        IKeycloakAdminClient kc,
        IAuditLog audit,
        IDataProtectionProvider dp,
        ILogger<TenantSignupService> logger,
        AgentOs.Domain.Configuration.ISettings? settings = null,
        IInvitationStore? invitations = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dp);
        _repo = repo;
        _kc = kc;
        _audit = audit;
        _logger = logger;
        _settings = settings;
        _invitations = invitations ?? new InMemoryInvitationStore();
        _clock = clock ?? TimeProvider.System;
        _protector = dp.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
    }

    public async Task<bool> IsOpenSignupEnabledAsync(CancellationToken ct = default)
    {
        // Fail closed: no settings registry (or an unreadable value) means no open signup.
        if (_settings is null) { return false; }
        try
        {
            var value = await _settings.GetAsync(Settings.TenantsSettings.OpenSignupKey, tenantId: null, ct).ConfigureAwait(false);
            return bool.TryParse(value.Value, out var open) && open;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The login and signup pages ask this on load — a settings-store outage must not take them down. Closed
            // is the safe answer: invitations still work, and a new workspace waits until the store is back.
            _logger.LogWarning(ex, "Could not read {Setting}; treating open self-signup as off.", Settings.TenantsSettings.OpenSignupKey);
            return false;
        }
    }

    public async Task<TenantSignupOutcome> SignupAsync(TenantSignupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // GAP-01: creating a NEW workspace (and becoming its admin) needs open signup; an invitation always works.
        if (string.IsNullOrWhiteSpace(request.InviteToken) && !await IsOpenSignupEnabledAsync(ct).ConfigureAwait(false))
        {
            throw new SignupClosedException();
        }

        var (pwdOk, pwdErr) = SignupValidation.ValidatePassword(request.Password);
        if (!pwdOk) { throw new InvalidOperationException(pwdErr!); }
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException("Username is required.");
        }
        if (!string.IsNullOrEmpty(request.Email))
        {
            var (emailOk, emailErr) = SignupValidation.ValidateEmail(request.Email.Trim());
            if (!emailOk) { throw new InvalidOperationException(emailErr!); }
        }

        if (!string.IsNullOrWhiteSpace(request.InviteToken))
        {
            return await SignupWithInvitationAsync(request, request.InviteToken, ct).ConfigureAwait(false);
        }

        string tenantId;
        SignupMode mode;
        if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            var (slugOk, slugErr) = SignupValidation.ValidateTenantId(request.TenantId);
            if (!slugOk) { throw new InvalidOperationException(slugErr!); }
            var existing = await _repo.GetAsync(request.TenantId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new InvalidOperationException($"Workspace '{request.TenantId}' already exists.");
            }
            tenantId = request.TenantId;
            mode = SignupMode.Slug;
        }
        else
        {
            tenantId = await GenerateUniqueSlugAsync(request.Username, ct).ConfigureAwait(false);
            mode = SignupMode.AutoCreate;
        }

        string kcUserId = await _kc.CreateUserAsync(
            username: request.Username,
            email: request.Email?.Trim() ?? string.Empty,
            tenantId: tenantId,
            realmRoles: AdminRoles,
            sendVerifyEmail: !string.IsNullOrWhiteSpace(request.Email),
            password: request.Password,
            ct: ct).ConfigureAwait(false);

        try
        {
            var record = new TenantRecord(
                tenantId,
                string.IsNullOrWhiteSpace(request.TenantName) ? tenantId : request.TenantName,
                DateTimeOffset.UtcNow);
            await _repo.AddAsync(record, ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx) { await HandleAsync(dbEx).ConfigureAwait(false); }
        catch (System.Data.Common.DbException dbEx) { await HandleAsync(dbEx).ConfigureAwait(false); }
        catch (InvalidOperationException dbEx) { await HandleAsync(dbEx).ConfigureAwait(false); }

        async Task HandleAsync(Exception dbEx)
        {
            void RollbackKeycloak(Exception rollbackEx) =>
                _logger.LogError(rollbackEx,
                    "Tenant DB write failed for '{TenantId}', and rolling back Keycloak user '{UserId}' also failed — orphaned Keycloak user.",
                    LogSafe.Scrub(tenantId), LogSafe.Scrub(kcUserId));
            try
            {
                await _kc.DeleteUserAsync(kcUserId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException rollbackEx) { RollbackKeycloak(rollbackEx); }
            catch (JsonException rollbackEx) { RollbackKeycloak(rollbackEx); }
            catch (TimeoutException rollbackEx) { RollbackKeycloak(rollbackEx); }
            catch (InvalidOperationException rollbackEx) { RollbackKeycloak(rollbackEx); }
            throw new InvalidOperationException(
                $"Tenant registry write failed for '{tenantId}'; Keycloak user rolled back.", dbEx);
        }

        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, kcUserId, AuditActions.SignupCompleted,
            Target: $"mode={mode}", IpAddress: null, UserAgent: null,
            TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, kcUserId, AuditActions.TenantCreated,
            Target: tenantId, IpAddress: null, UserAgent: null,
            TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return new TenantSignupOutcome(tenantId, kcUserId, mode);
    }

    // The invite branch (see the file header for the order and why).
    private async Task<TenantSignupOutcome> SignupWithInvitationAsync(TenantSignupRequest request, string token, CancellationToken ct)
    {
        var payload = ReadToken(token)
            ?? throw new InvalidOperationException(InvitationRules.Describe(InvitationAcceptance.NotFound));
        // The invitee is anonymous (or signed in elsewhere): the signed token, not the caller, names the tenant.
        using var bound = RowLevelSecurity.TenantScope(payload.TenantId);
        var invitation = await _invitations.GetAsync(payload.TenantId, payload.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(InvitationRules.Describe(InvitationAcceptance.NotFound));

        var check = InvitationRules.CheckAcceptance(invitation.StatusAt(_clock.GetUtcNow()), invitation.EmailNormalized, request.Email);
        if (check != InvitationAcceptance.Ok)
        {
            await RefusedAsync(invitation, check, ct).ConfigureAwait(false);
            throw new InvalidOperationException(InvitationRules.Describe(check));
        }
        _ = await _repo.GetAsync(invitation.TenantId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The workspace '{invitation.TenantId}' this invitation is for no longer exists.");

        // Consume it before the account exists: a second accept of the same link, a revoke or the expiry landing in
        // between makes this conditional write touch nothing — the invitation is used at most once.
        if (!await _invitations.TryClaimAsync(invitation.TenantId, invitation.Id, _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            var now = await _invitations.GetAsync(invitation.TenantId, invitation.Id, ct).ConfigureAwait(false);
            var why = now is null ? InvitationAcceptance.NotFound : InvitationRules.ForStatus(now.StatusAt(_clock.GetUtcNow()));
            why = why == InvitationAcceptance.Ok ? InvitationAcceptance.AlreadyUsed : why;
            await RefusedAsync(invitation, why, ct).ConfigureAwait(false);
            throw new InvalidOperationException(InvitationRules.Describe(why));
        }

        string? kcUserId = null;
        try
        {
            kcUserId = await _kc.CreateUserAsync(
                username: request.Username,
                email: invitation.Email,
                tenantId: invitation.TenantId,
                realmRoles: [invitation.Role],
                sendVerifyEmail: true,
                password: request.Password,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            if (kcUserId is null)
            {
                // No account was created (a taken username, Keycloak down): give the invitation back so the invitee can
                // try again. Never throws over the original failure.
                await ReleaseQuietlyAsync(invitation).ConfigureAwait(false);
            }
        }

        try
        {
            await _invitations.CompleteAsync(invitation.TenantId, invitation.Id, kcUserId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The account exists and the invitation stays consumed; only the link to the account is missing.
            _logger.LogWarning(ex, "Invitation {InvitationId} was used, but recording the account it created failed.", invitation.Id);
        }

        var at = _clock.GetUtcNow();
        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), invitation.TenantId, kcUserId, AuditActions.SignupCompleted,
            Target: $"mode={SignupMode.Invite}", IpAddress: null, UserAgent: null, TimestampUtc: at), ct).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), invitation.TenantId, kcUserId, AuditActions.InvitationAccepted,
            Target: $"id={invitation.Id:N},role={invitation.Role},email={invitation.Email}", IpAddress: null, UserAgent: null, TimestampUtc: at), ct).ConfigureAwait(false);
        return new TenantSignupOutcome(invitation.TenantId, kcUserId, SignupMode.Invite);
    }

    private async Task ReleaseQuietlyAsync(InvitationRecord invitation)
    {
        try
        {
            await _invitations.ReleaseAsync(invitation.TenantId, invitation.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Invitation {InvitationId} could not be released after a failed sign-up; it stays used — send a new one.", invitation.Id);
        }
    }

    private Task RefusedAsync(InvitationRecord invitation, InvitationAcceptance why, CancellationToken ct)
        => _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), invitation.TenantId, UserId: null, AuditActions.InvitationRefused,
            Target: $"id={invitation.Id:N},reason={why}", IpAddress: null, UserAgent: null, TimestampUtc: _clock.GetUtcNow()), ct);

    public async Task<InvitationToken> CreateInvitationAsync(
        string tenantId, string role, string email, TimeSpan ttl, AuditActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (role is not ("admin" or "member"))
        {
            throw new ArgumentException("The role must be 'admin' or 'member'.", nameof(role));
        }
        var address = email?.Trim() ?? string.Empty;
        var (emailOk, emailErr) = SignupValidation.ValidateEmail(address);
        if (!emailOk || address.Length > InvitationRules.MaxEmailLength)
        {
            throw new ArgumentException(emailOk ? "The email address is too long." : emailErr, nameof(email));
        }
        if (ttl <= TimeSpan.Zero || ttl > MaxInvitationLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "An invitation lasts between 1 and 168 hours.");
        }

        var now = _clock.GetUtcNow();
        var invitation = new InvitationRecord(
            Guid.NewGuid(), tenantId, address, InvitationRules.NormalizeEmail(address), role, actor.UserId, now, now + ttl);
        using (RowLevelSecurity.TenantScopeIfUnbound(tenantId))
        {
            await _invitations.AddAsync(invitation, ct).ConfigureAwait(false);
        }
        var token = _protector.Protect(JsonSerializer.Serialize(new InvitationPayload(invitation.Id, tenantId), JsonOpts), ttl);
        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, actor.UserId, AuditActions.InvitationMinted,
            Target: $"id={invitation.Id:N},role={role},email={address}",
            actor.IpAddress, actor.UserAgent, now), ct).ConfigureAwait(false);
        return new InvitationToken(invitation.Id, token, invitation.ExpiresAtUtc);
    }

    public async Task<InvitationPreview?> PreviewInvitationAsync(string? token, CancellationToken ct = default)
    {
        if (ReadToken(token) is not { } payload) { return null; }
        // Anonymous by design (the signup page), and the token — signed by us — names the tenant.
        using var bound = RowLevelSecurity.TenantScope(payload.TenantId);
        var invitation = await _invitations.GetAsync(payload.TenantId, payload.Id, ct).ConfigureAwait(false);
        return invitation is null
            ? null
            : new InvitationPreview(invitation.Id, invitation.TenantId, invitation.Role, invitation.Email, invitation.ExpiresAtUtc,
                invitation.StatusAt(_clock.GetUtcNow()));
    }

    public async Task<IReadOnlyList<InvitationInfo>> ListInvitationsAsync(string tenantId, int max = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var rows = await _invitations.ListAsync(tenantId, max, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        return [.. rows.Select(r => r.ToInfo(now))];
    }

    public async Task<bool> RevokeInvitationAsync(string tenantId, Guid invitationId, AuditActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var now = _clock.GetUtcNow();
        if (!await _invitations.TryRevokeAsync(tenantId, invitationId, now, actor.UserId, ct).ConfigureAwait(false))
        {
            return false;
        }
        var invitation = await _invitations.GetAsync(tenantId, invitationId, ct).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, actor.UserId, AuditActions.InvitationRevoked,
            Target: $"id={invitationId:N}{(invitation is null ? string.Empty : $",email={invitation.Email}")}",
            actor.IpAddress, actor.UserAgent, now), ct).ConfigureAwait(false);
        return true;
    }

    // The signed envelope: null for anything that isn't a current, intact invitation token.
    private InvitationPayload? ReadToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) { return null; }
        string json;
        try
        {
            json = _protector.Unprotect(token, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<InvitationPayload>(json, JsonOpts) is { } payload
                && payload.Id != Guid.Empty && !string.IsNullOrWhiteSpace(payload.TenantId)
                    ? payload
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> GenerateUniqueSlugAsync(string username, CancellationToken ct)
    {
        var baseSlug = SlugifyUsername(username);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            var candidate = string.IsNullOrEmpty(baseSlug) ? $"ws-{suffix}" : $"{baseSlug}-{suffix}";
            var clamped = candidate.Length > 32 ? candidate[..32].TrimEnd('-') : candidate;
            var (ok, _) = SignupValidation.ValidateTenantId(clamped);
            if (!ok) { continue; }
            var existing = await _repo.GetAsync(clamped, ct).ConfigureAwait(false);
            if (existing is null) { return clamped; }
        }
        throw new InvalidOperationException("Failed to allocate a unique workspace slug after 5 attempts.");
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugChars();

    private static string SlugifyUsername(string username)
    {
        var lower = username.ToLowerInvariant();
        var slug = NonSlugChars().Replace(lower, "-").Trim('-');
        if (slug.Length > 24) { slug = slug[..24].TrimEnd('-'); }
        return slug;
    }

    private static readonly string[] AdminRoles = ["admin"];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // The token carries only what finds the row; everything else comes from the row.
    private sealed record InvitationPayload(Guid Id, string TenantId);
}
