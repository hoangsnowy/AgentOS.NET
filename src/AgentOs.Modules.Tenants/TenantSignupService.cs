// Default impl of ITenantSignupService. Keycloak-first ordering with rollback so a registry-row
// failure never leaves the tenant claim orphaned without a corresponding row. Invitations are
// time-limited DataProtection payloads serialized as JSON, no persistence required.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tenants;

internal sealed partial class TenantSignupService : ITenantSignupService
{
    private const string ProtectorPurpose = "AgentOs.Modules.Tenants.Invitation.v1";

    private readonly ITenantsRepository _repo;
    private readonly IKeycloakAdminClient _kc;
    private readonly IAuditLog _audit;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<TenantSignupService> _logger;
    private readonly AgentOs.Domain.Configuration.ISettings? _settings;

    public TenantSignupService(
        ITenantsRepository repo,
        IKeycloakAdminClient kc,
        IAuditLog audit,
        IDataProtectionProvider dp,
        ILogger<TenantSignupService> logger,
        AgentOs.Domain.Configuration.ISettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(dp);
        _repo = repo;
        _kc = kc;
        _audit = audit;
        _logger = logger;
        _settings = settings;
        _protector = dp.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
    }

    public async Task<bool> IsOpenSignupEnabledAsync(CancellationToken ct = default)
    {
        // Fail closed: no settings registry (or an unreadable value) means no open signup.
        if (_settings is null) { return false; }
        var value = await _settings.GetAsync(Settings.TenantsSettings.OpenSignupKey, tenantId: null, ct).ConfigureAwait(false);
        return bool.TryParse(value.Value, out var open) && open;
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
            var (emailOk, emailErr) = SignupValidation.ValidateEmail(request.Email);
            if (!emailOk) { throw new InvalidOperationException(emailErr!); }
        }

        string tenantId;
        string role;
        SignupMode mode;
        bool createTenantRow;

        if (!string.IsNullOrWhiteSpace(request.InviteToken))
        {
            var preview = PreviewInvitation(request.InviteToken)
                ?? throw new InvalidOperationException("Invitation is invalid or expired.");
            var existing = await _repo.GetAsync(preview.TenantId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Invitation tenant '{preview.TenantId}' no longer exists.");
            tenantId = existing.Id;
            role = preview.Role;
            mode = SignupMode.Invite;
            createTenantRow = false;
        }
        else if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            var (slugOk, slugErr) = SignupValidation.ValidateTenantId(request.TenantId);
            if (!slugOk) { throw new InvalidOperationException(slugErr!); }
            var existing = await _repo.GetAsync(request.TenantId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new InvalidOperationException($"Workspace '{request.TenantId}' already exists.");
            }
            tenantId = request.TenantId;
            role = "admin";
            mode = SignupMode.Slug;
            createTenantRow = true;
        }
        else
        {
            tenantId = await GenerateUniqueSlugAsync(request.Username, ct).ConfigureAwait(false);
            role = "admin";
            mode = SignupMode.AutoCreate;
            createTenantRow = true;
        }

        var roles = new[] { role };
        var sendVerify = !string.IsNullOrWhiteSpace(request.Email);

        string kcUserId = await _kc.CreateUserAsync(
            username: request.Username,
            email: request.Email ?? string.Empty,
            tenantId: tenantId,
            realmRoles: roles,
            sendVerifyEmail: sendVerify,
            password: request.Password,
            ct: ct).ConfigureAwait(false);

        if (createTenantRow)
        {
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
        }

        await _audit.WriteAsync(new AuditEntry(
            Guid.NewGuid(), tenantId, kcUserId, AuditActions.SignupCompleted,
            Target: $"mode={mode}", IpAddress: null, UserAgent: null,
            TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        if (createTenantRow)
        {
            await _audit.WriteAsync(new AuditEntry(
                Guid.NewGuid(), tenantId, kcUserId, AuditActions.TenantCreated,
                Target: tenantId, IpAddress: null, UserAgent: null,
                TimestampUtc: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }

        return new TenantSignupOutcome(tenantId, kcUserId, mode);
    }

    public InvitationToken CreateInvitation(string tenantId, string role, string? email, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var payload = new InvitationPayload(tenantId, role, email, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var token = _protector.Protect(json, ttl);
        return new InvitationToken(token, DateTimeOffset.UtcNow.Add(ttl));
    }

    public InvitationPreview? PreviewInvitation(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) { return null; }
        string json;
        DateTimeOffset expires;
        try
        {
            json = _protector.Unprotect(token, out expires);
        }
        catch (CryptographicException)
        {
            return null;
        }
        InvitationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<InvitationPayload>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null) { return null; }
        return new InvitationPreview(payload.TenantId, payload.Role, payload.Email, expires);
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

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record InvitationPayload(string TenantId, string Role, string? Email, DateTimeOffset IssuedAtUtc);
}
