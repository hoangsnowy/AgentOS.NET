// Enterprise review E1.3 — machine access: personal API tokens and tenant service accounts, so CI pipelines, scripts,
// the VS Code extension and MCP clients call the Api without a browser login.
//   Format   aos_<prefix>_<secret>   prefix = 10 base32 chars (lookup key, shown in the UI), secret = 32 random bytes
//   Stored   HMAC-SHA256(key = a per-token 16-byte random salt, token) as hex — the plaintext is shown once at creation
//            and never again. Rows from before the salt keep their plain SHA-256 and still validate.
//   Checked  prefix lookup (indexed) → constant-time hash compare → not revoked, not expired → a principal with the
//            tenant claim, the token's scopes (read | run | admin, closed upwards) and the derived role.
//   Limits   expiry 1..Tenants:ApiTokens:MaxLifetimeDays (a platform setting); service accounts and the admin scope
//            need a tenant admin; tokens never carry platform-admin.
//   Audit    create, revoke, and use: a use row (with the caller's IP + user agent) whenever last-used advances — at
//            most once per token per 5 minutes — and a refusal row (revoked / expired / wrong secret on a real prefix),
//            also at most once per token per 5 minutes so a stale CI secret can't flood the trail. Every use is
//            additionally logged (token id + prefix only).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Tenants.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>One API token as listed (never the secret).</summary>
public sealed record ApiTokenInfo(Guid Id, string Name, string Prefix, string Kind, IReadOnlyList<string> Scopes, string? OwnerUserId,
    string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc)
{
    /// <summary>Usable right now.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && ExpiresAtUtc > now;

    /// <summary>The role the token's principal carries.</summary>
    public string Role => ApiTokenScopes.RoleFor(Scopes);
}

/// <summary>A token that passed validation.</summary>
public sealed record ApiTokenIdentity(Guid Id, string TenantId, string Name, string Kind, IReadOnlyList<string> Scopes,
    string? OwnerUserId, string CreatedBy, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>The role the token's principal carries.</summary>
    public string Role => ApiTokenScopes.RoleFor(Scopes);
}

/// <summary>Who creates / lists / revokes tokens.</summary>
/// <param name="Name">Display name for the audit trail and "created by".</param>
/// <param name="UserId">Keycloak user id (the owner of personal tokens).</param>
/// <param name="IsAdmin">Tenant admin: may create service accounts + admin-scoped tokens and see / revoke every token.</param>
public sealed record ApiTokenActor(string Name, string? UserId, bool IsAdmin);

/// <summary>What to create.</summary>
public sealed record ApiTokenRequest(string TenantId, string Name, string Kind, IReadOnlyCollection<string> Scopes, int ExpiresInDays, ApiTokenActor Actor);

/// <summary>Where a token was presented (for the audit row).</summary>
public sealed record ApiTokenUse(string? IpAddress, string? UserAgent);

/// <summary>Create, list, revoke and validate API tokens.</summary>
public interface IApiTokenService
{
    /// <summary>Create a token; returns its info and the plaintext — shown ONCE.</summary>
    Task<(ApiTokenInfo Info, string Token)> CreateAsync(ApiTokenRequest request, CancellationToken ct = default);

    /// <summary>The tokens <paramref name="viewer"/> may see in the tenant (admin: all; member: their personal tokens), newest first.</summary>
    Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, ApiTokenActor viewer, CancellationToken ct = default);

    /// <summary>Revoke a token (admin: any of the tenant's; member: their own). False when not found / not allowed / already revoked.</summary>
    Task<bool> RevokeAsync(string tenantId, Guid id, ApiTokenActor actor, CancellationToken ct = default);

    /// <summary>Revoke every active personal token <paramref name="ownerUserId"/> holds in the tenant (member removal). Returns the count.</summary>
    Task<int> RevokeForOwnerAsync(string tenantId, string ownerUserId, string actor, CancellationToken ct = default);

    /// <summary>The longest expiry, in days, a new token may have.</summary>
    Task<int> MaxLifetimeDaysAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The identity behind <paramref name="token"/>, or null when unknown, revoked, expired or malformed.</summary>
    Task<ApiTokenIdentity?> ValidateAsync(string token, ApiTokenUse? use = null, CancellationToken ct = default);
}

/// <summary>Token format helpers (pure).</summary>
public static class ApiTokenFormat
{
    /// <summary>Every token starts with this.</summary>
    public const string Scheme = "aos_";

    /// <summary>The platform setting that caps a token's lifetime.</summary>
    public const string MaxLifetimeKey = "Tenants:ApiTokens:MaxLifetimeDays";

    /// <summary>Lifetime cap when the setting is unset.</summary>
    public const int DefaultMaxLifetimeDays = 366;

    /// <summary>Longest name.</summary>
    public const int MaxNameLength = 128;

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A new token and its prefix.</summary>
    public static (string Token, string Prefix) New()
    {
        Span<byte> idBytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(idBytes);
        Span<char> prefix = stackalloc char[10];
        for (var i = 0; i < prefix.Length; i++) { prefix[i] = Base32[idBytes[i] & 31]; }
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var p = new string(prefix);
        return ($"{Scheme}{p}_{secret}", p);
    }

    /// <summary>A fresh per-token salt (16 random bytes, hex).</summary>
    public static string NewSalt() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The prefix of a well-formed token, else null.</summary>
    public static string? PrefixOf(string? token)
    {
        if (token is null || !token.StartsWith(Scheme, StringComparison.Ordinal) || token.Length < Scheme.Length + 12) { return null; }
        var rest = token.AsSpan(Scheme.Length);
        var underscore = rest.IndexOf('_');
        return underscore == 10 ? rest[..10].ToString() : null;
    }

    /// <summary>The stored hash of <paramref name="token"/>: HMAC-SHA256 keyed by <paramref name="salt"/> (hex), or the
    /// legacy unsalted SHA-256 when <paramref name="salt"/> is empty. Lower hex, 64 chars.</summary>
    public static string Hash(string token, string salt)
    {
        ArgumentNullException.ThrowIfNull(token);
        var bytes = Encoding.UTF8.GetBytes(token);
        return string.IsNullOrEmpty(salt)
            ? Convert.ToHexStringLower(SHA256.HashData(bytes))
            : Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(salt), bytes));
    }

    /// <summary>Constant-time compare of two hex hashes.</summary>
    public static bool HashEquals(string presented, string stored)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(stored));
}

internal sealed partial class ApiTokenService(
    TenantsDbContext db, IAuditLog audit, TimeProvider clock, ISettings? settings = null,
    ILogger<ApiTokenService>? logger = null, ApiTokenAuditThrottle? throttle = null) : IApiTokenService
{
    private static readonly TimeSpan UseGranularity = TimeSpan.FromMinutes(5);

    // Hashed when a prefix is unknown, so an unknown token costs the same as a wrong secret.
    private static readonly string DummySalt = ApiTokenFormat.NewSalt();

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;
    private readonly ApiTokenAuditThrottle _throttle = throttle ?? ApiTokenAuditThrottle.Shared;

    public async Task<(ApiTokenInfo Info, string Token)> CreateAsync(ApiTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentNullException.ThrowIfNull(request.Actor);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > ApiTokenFormat.MaxNameLength)
        {
            throw new InvalidOperationException($"Give the token a name (1–{ApiTokenFormat.MaxNameLength} characters).");
        }
        if (!ApiTokenKind.IsKnown(request.Kind))
        {
            throw new InvalidOperationException($"Kind must be {ApiTokenKind.Personal} or {ApiTokenKind.Service}.");
        }
        var scopes = ApiTokenScopes.Normalize(request.Scopes);
        var actor = request.Actor;
        if (request.Kind == ApiTokenKind.Service && !actor.IsAdmin)
        {
            throw new InvalidOperationException("Only a workspace admin can create a service account.");
        }
        if (scopes.Contains(ApiTokenScopes.Admin) && !actor.IsAdmin)
        {
            throw new InvalidOperationException("Only a workspace admin can create a token with the admin scope.");
        }
        if (request.Kind == ApiTokenKind.Personal && string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new InvalidOperationException("A personal token needs a signed-in user to act as.");
        }
        var maxDays = await MaxLifetimeDaysAsync(request.TenantId, ct).ConfigureAwait(false);
        if (request.ExpiresInDays < 1 || request.ExpiresInDays > maxDays)
        {
            throw new InvalidOperationException($"Expiry must be between 1 and {maxDays} days.");
        }

        var (token, prefix) = ApiTokenFormat.New();
        var salt = ApiTokenFormat.NewSalt();
        var now = clock.GetUtcNow();
        var entity = new ApiTokenEntity
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Name = name,
            Prefix = prefix,
            Salt = salt,
            TokenHash = ApiTokenFormat.Hash(token, salt),
            Kind = request.Kind,
            Scopes = ApiTokenScopes.Format(scopes),
            OwnerUserId = request.Kind == ApiTokenKind.Personal ? actor.UserId : null,
            Role = ApiTokenScopes.RoleFor(scopes),
            CreatedBy = actor.Name,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(request.ExpiresInDays),
        };
        db.ApiTokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), request.TenantId, actor.Name, AuditActions.ApiTokenCreated,
            $"{entity.Name} ({prefix}, {entity.Kind}, {entity.Scopes}, {request.ExpiresInDays} d)", null, null, now), ct).ConfigureAwait(false);
        return (ToInfo(entity), token);
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, ApiTokenActor viewer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var query = db.ApiTokens.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (!viewer.IsAdmin)
        {
            if (string.IsNullOrWhiteSpace(viewer.UserId)) { return []; }
            query = query.Where(t => t.Kind == ApiTokenKind.Personal && t.OwnerUserId == viewer.UserId);
        }
        var rows = await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToInfo).ToArray();
    }

    public async Task<bool> RevokeAsync(string tenantId, Guid id, ApiTokenActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var entity = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct).ConfigureAwait(false);
        if (entity is null || entity.RevokedAtUtc is not null) { return false; }
        var own = entity.Kind == ApiTokenKind.Personal && actor.UserId is { Length: > 0 } && entity.OwnerUserId == actor.UserId;
        if (!actor.IsAdmin && !own) { return false; }
        entity.RevokedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), tenantId, actor.Name, AuditActions.ApiTokenRevoked,
            $"{entity.Name} ({entity.Prefix}, {entity.Kind})", null, null, entity.RevokedAtUtc.Value), ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RevokeForOwnerAsync(string tenantId, string ownerUserId, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        var rows = await db.ApiTokens
            .Where(t => t.TenantId == tenantId && t.Kind == ApiTokenKind.Personal && t.OwnerUserId == ownerUserId && t.RevokedAtUtc == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0) { return 0; }
        var now = clock.GetUtcNow();
        foreach (var row in rows) { row.RevokedAtUtc = now; }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), tenantId, actor, AuditActions.ApiTokenRevoked,
                $"{row.Name} ({row.Prefix}, {row.Kind}, owner removed)", null, null, now), ct).ConfigureAwait(false);
        }
        return rows.Count;
    }

    public async Task<int> MaxLifetimeDaysAsync(string tenantId, CancellationToken ct = default)
    {
        if (settings is null) { return ApiTokenFormat.DefaultMaxLifetimeDays; }
        var days = await settings.GetIntAsync(ApiTokenFormat.MaxLifetimeKey, tenantId, ApiTokenFormat.DefaultMaxLifetimeDays, ct).ConfigureAwait(false);
        return days < 1 ? ApiTokenFormat.DefaultMaxLifetimeDays : days;
    }

    public async Task<ApiTokenIdentity?> ValidateAsync(string token, ApiTokenUse? use = null, CancellationToken ct = default)
    {
        if (ApiTokenFormat.PrefixOf(token) is not { } prefix) { return null; }
        // The token is the credential: this lookup runs before any tenant is known (auth), across tenants by design.
        var entity = await db.ApiTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Prefix == prefix, ct).ConfigureAwait(false);
        if (entity is null)
        {
            _ = ApiTokenFormat.HashEquals(ApiTokenFormat.Hash(token, DummySalt), ApiTokenFormat.Hash(token, DummySalt));
            return null;
        }
        var now = clock.GetUtcNow();
        var refusal = !ApiTokenFormat.HashEquals(ApiTokenFormat.Hash(token, entity.Salt), entity.TokenHash) ? "wrong secret"
            : entity.RevokedAtUtc is not null ? "revoked"
            : entity.ExpiresAtUtc <= now ? "expired"
            : null;
        if (refusal is not null)
        {
            LogRefused(_log, entity.Id, entity.Prefix, refusal);
            if (_throttle.ShouldRecord(entity.Id, now, UseGranularity))
            {
                await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), entity.TenantId, $"token:{entity.Name}", AuditActions.ApiTokenRefused,
                    $"{entity.Name} ({entity.Prefix}, {refusal})", use?.IpAddress, use?.UserAgent, now), ct).ConfigureAwait(false);
            }
            return null;
        }

        LogUsed(_log, entity.Id, entity.Prefix);
        if (entity.LastUsedAtUtc is null || now - entity.LastUsedAtUtc.Value > UseGranularity)
        {
            entity.LastUsedAtUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), entity.TenantId, $"token:{entity.Name}", AuditActions.ApiTokenUsed,
                $"{entity.Name} ({entity.Prefix}, {entity.Kind}, {entity.Scopes})", use?.IpAddress, use?.UserAgent, now), ct).ConfigureAwait(false);
        }
        return new ApiTokenIdentity(entity.Id, entity.TenantId, entity.Name, entity.Kind, ScopesOf(entity), entity.OwnerUserId,
            entity.CreatedBy, entity.ExpiresAtUtc);
    }

    private static IReadOnlyList<string> ScopesOf(ApiTokenEntity t)
        => ApiTokenScopes.Parse(string.IsNullOrWhiteSpace(t.Scopes) ? ApiTokenScopes.FromLegacyRole(t.Role) : t.Scopes);

    private static ApiTokenInfo ToInfo(ApiTokenEntity t)
        => new(t.Id, t.Name, t.Prefix, t.Kind, ScopesOf(t), t.OwnerUserId, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc);

    [LoggerMessage(Level = LogLevel.Debug, Message = "API token {TokenId} ({Prefix}) authenticated.")]
    private static partial void LogUsed(ILogger logger, Guid tokenId, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API token {TokenId} ({Prefix}) refused: {Reason}.")]
    private static partial void LogRefused(ILogger logger, Guid tokenId, string prefix, string reason);
}

/// <summary>At most one refusal audit row per token per window, process-wide.</summary>
internal sealed class ApiTokenAuditThrottle
{
    /// <summary>The process-wide instance.</summary>
    public static ApiTokenAuditThrottle Shared { get; } = new();

    private readonly ConcurrentDictionary<Guid, long> _last = new();

    /// <summary>True (and the window restarts) when nothing was recorded for <paramref name="id"/> within <paramref name="window"/>.</summary>
    public bool ShouldRecord(Guid id, DateTimeOffset now, TimeSpan window)
    {
        var ticks = now.UtcTicks;
        while (true)
        {
            if (!_last.TryGetValue(id, out var previous))
            {
                if (_last.TryAdd(id, ticks)) { return true; }
                continue;
            }
            if (ticks - previous < window.Ticks) { return false; }
            if (_last.TryUpdate(id, ticks, previous)) { return true; }
        }
    }
}

/// <summary>No database: tokens are unavailable (creation fails loudly; validation always refuses).</summary>
internal sealed class NullApiTokenService : IApiTokenService
{
    public Task<(ApiTokenInfo Info, string Token)> CreateAsync(ApiTokenRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException("API tokens need a database — this run has none.");

    public Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, ApiTokenActor viewer, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiTokenInfo>>([]);

    public Task<bool> RevokeAsync(string tenantId, Guid id, ApiTokenActor actor, CancellationToken ct = default) => Task.FromResult(false);

    public Task<int> RevokeForOwnerAsync(string tenantId, string ownerUserId, string actor, CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> MaxLifetimeDaysAsync(string tenantId, CancellationToken ct = default) => Task.FromResult(ApiTokenFormat.DefaultMaxLifetimeDays);

    public Task<ApiTokenIdentity?> ValidateAsync(string token, ApiTokenUse? use = null, CancellationToken ct = default)
        => Task.FromResult<ApiTokenIdentity?>(null);
}
