// Enterprise review E1.3 — machine access: tenant-scoped API tokens (service accounts) so CI pipelines, scripts and
// MCP clients call the Api without a browser login.
//   Format   aos_<prefix>_<secret>   prefix = 10 base32 chars (lookup key, shown in the UI), secret = 32 random bytes
//   Stored   SHA-256 of the WHOLE token (hex) — the plaintext is shown once at creation and never again
//   Checked  prefix lookup (indexed) → constant-time hash compare → not revoked, not expired → a principal with the
//            tenant claim and the token's role (member | admin). Last use is recorded at most every 5 minutes.
// Create / revoke land in the audit trail. Tokens never carry platform-admin.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>One API token as listed (never the secret).</summary>
public sealed record ApiTokenInfo(Guid Id, string Name, string Prefix, string Role, string CreatedBy, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc)
{
    /// <summary>Usable right now.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && ExpiresAtUtc > now;
}

/// <summary>A token that passed validation.</summary>
public sealed record ApiTokenIdentity(Guid Id, string TenantId, string Name, string Role);

/// <summary>Create, list, revoke and validate tenant API tokens.</summary>
public interface IApiTokenService
{
    /// <summary>Create a token; returns its info and the plaintext — shown ONCE.</summary>
    Task<(ApiTokenInfo Info, string Token)> CreateAsync(string tenantId, string name, string role, int expiresInDays, string actor, CancellationToken ct = default);

    /// <summary>The tenant's tokens, newest first.</summary>
    Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Revoke a token of the tenant (no-op when already revoked or not the tenant's).</summary>
    Task<bool> RevokeAsync(string tenantId, Guid id, string actor, CancellationToken ct = default);

    /// <summary>The identity behind <paramref name="token"/>, or null when unknown, revoked, expired or malformed.</summary>
    Task<ApiTokenIdentity?> ValidateAsync(string token, CancellationToken ct = default);
}

/// <summary>Token format helpers (pure).</summary>
public static class ApiTokenFormat
{
    /// <summary>Every token starts with this.</summary>
    public const string Scheme = "aos_";

    /// <summary>Roles a token may hold.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["member", "admin"];

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A new token and its prefix.</summary>
    public static (string Token, string Prefix) New()
    {
        Span<byte> idBytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(idBytes);
        var prefix = string.Create(10, idBytes.ToArray(), static (span, bytes) =>
        {
            for (var i = 0; i < span.Length; i++) { span[i] = Base32[bytes[i] & 31]; }
        });
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return ($"{Scheme}{prefix}_{secret}", prefix);
    }

    /// <summary>The prefix of a well-formed token, else null.</summary>
    public static string? PrefixOf(string? token)
    {
        if (token is null || !token.StartsWith(Scheme, StringComparison.Ordinal) || token.Length < Scheme.Length + 12) { return null; }
        var rest = token.AsSpan(Scheme.Length);
        var underscore = rest.IndexOf('_');
        return underscore == 10 ? rest[..10].ToString() : null;
    }

    /// <summary>SHA-256 (hex) of the whole token.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

internal sealed class ApiTokenService(TenantsDbContext db, IAuditLog audit, TimeProvider clock) : IApiTokenService
{
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);

    public async Task<(ApiTokenInfo Info, string Token)> CreateAsync(string tenantId, string name, string role, int expiresInDays, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ApiTokenFormat.Roles.Contains(role, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Role must be one of: {string.Join(", ", ApiTokenFormat.Roles)}.");
        }
        if (expiresInDays is < 1 or > 366)
        {
            throw new InvalidOperationException("Expiry must be between 1 and 366 days.");
        }
        var (token, prefix) = ApiTokenFormat.New();
        var now = clock.GetUtcNow();
        var entity = new ApiTokenEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Prefix = prefix,
            TokenHash = ApiTokenFormat.Hash(token),
            Role = role,
            CreatedBy = actor,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(expiresInDays),
        };
        db.ApiTokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), tenantId, actor, AuditActions.ApiTokenCreated,
            $"{entity.Name} ({prefix}, {role}, {expiresInDays} d)", null, null, now), ct).ConfigureAwait(false);
        return (ToInfo(entity), token);
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, CancellationToken ct = default)
        => await db.ApiTokens.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new ApiTokenInfo(t.Id, t.Name, t.Prefix, t.Role, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> RevokeAsync(string tenantId, Guid id, string actor, CancellationToken ct = default)
    {
        var entity = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct).ConfigureAwait(false);
        if (entity is null || entity.RevokedAtUtc is not null) { return false; }
        entity.RevokedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), tenantId, actor, AuditActions.ApiTokenRevoked,
            $"{entity.Name} ({entity.Prefix})", null, null, entity.RevokedAtUtc.Value), ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ApiTokenIdentity?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (ApiTokenFormat.PrefixOf(token) is not { } prefix) { return null; }
        // The token is the credential: this lookup runs before any tenant is known (auth), across tenants by design.
        var entity = await db.ApiTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Prefix == prefix, ct).ConfigureAwait(false);
        if (entity is null) { return null; }
        var presented = Encoding.ASCII.GetBytes(ApiTokenFormat.Hash(token));
        var stored = Encoding.ASCII.GetBytes(entity.TokenHash);
        if (!CryptographicOperations.FixedTimeEquals(presented, stored)) { return null; }
        var now = clock.GetUtcNow();
        if (entity.RevokedAtUtc is not null || entity.ExpiresAtUtc <= now) { return null; }
        if (entity.LastUsedAtUtc is null || now - entity.LastUsedAtUtc.Value > LastUsedGranularity)
        {
            entity.LastUsedAtUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return new ApiTokenIdentity(entity.Id, entity.TenantId, entity.Name, entity.Role);
    }

    private static ApiTokenInfo ToInfo(ApiTokenEntity t)
        => new(t.Id, t.Name, t.Prefix, t.Role, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc);
}

/// <summary>No database: tokens are unavailable (creation fails loudly; validation always refuses).</summary>
internal sealed class NullApiTokenService : IApiTokenService
{
    public Task<(ApiTokenInfo Info, string Token)> CreateAsync(string tenantId, string name, string role, int expiresInDays, string actor, CancellationToken ct = default)
        => throw new InvalidOperationException("API tokens need a database — this run has none.");

    public Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiTokenInfo>>([]);

    public Task<bool> RevokeAsync(string tenantId, Guid id, string actor, CancellationToken ct = default) => Task.FromResult(false);

    public Task<ApiTokenIdentity?> ValidateAsync(string token, CancellationToken ct = default) => Task.FromResult<ApiTokenIdentity?>(null);
}
