// EF-backed, DataProtection-encrypted IAppConfigStore. Keyed by (TenantId, Key) so each tenant
// owns its own LLM keys / GitHub PAT / etc. Singleton; per-op DI scope is created here and the
// tenant id is read out of the OUTER request scope via the captured IServiceProvider. A 15-second
// read cache keeps the LLM hot path off the DB on every call; cache keys include the tenant.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.AppConfig.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.AppConfig;

/// <summary>EF-backed, DataProtection-encrypted <see cref="IAppConfigStore"/>, scoped by tenant.</summary>
public sealed class EfAppConfigStore : IAppConfigStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider _rootProvider;
    private readonly IDataProtector _protector;
    private readonly ILogger<EfAppConfigStore> _logger;
    // Keyed by (tenant, key) — a tuple, not a "tenant:key" string, so a cache hit allocates nothing and two pairs can
    // never collide on the separator ("a:b"+"c" vs "a"+"b:c").
    private readonly ConcurrentDictionary<(string Tenant, string Key), CacheEntry> _cache = new();

    public EfAppConfigStore(IServiceProvider rootProvider, IDataProtectionProvider dp, ILogger<EfAppConfigStore> logger)
    {
        _rootProvider = rootProvider;
        _protector = dp.CreateProtector("AgentOs.AppConfig.v1");
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var scope = _rootProvider.CreateAsyncScope();
        var tenantId = ResolveTenant(scope.ServiceProvider);
        var cacheKey = CacheKey(tenantId, key);
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.FetchedUtc + CacheTtl > DateTime.UtcNow)
        {
            return hit.Value;
        }

        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var row = await db.AppConfig.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken)
            .ConfigureAwait(false);
        var value = row is null ? null : TryUnprotect(row.EncryptedValue);
        _cache[cacheKey] = new CacheEntry(value, DateTime.UtcNow);
        return value;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(tenantId, key);
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.FetchedUtc + CacheTtl > DateTime.UtcNow)
        {
            return hit.Value;
        }

        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var row = await db.AppConfig.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken)
            .ConfigureAwait(false);
        var value = row is null ? null : TryUnprotect(row.EncryptedValue);
        _cache[cacheKey] = new CacheEntry(value, DateTime.UtcNow);
        return value;
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
        => SetForTenantAsync(ResolveTenant(), key, value, cancellationToken);

    /// <inheritdoc />
    public async ValueTask SetForTenantAsync(string tenantId, string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var cipher = _protector.Protect(value);
        var row = await db.AppConfig
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            db.AppConfig.Add(new AppConfigEntity { TenantId = tenantId, Key = key, EncryptedValue = cipher, UpdatedAtUtc = DateTime.UtcNow });
        }
        else
        {
            row.EncryptedValue = cipher;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache[CacheKey(tenantId, key)] = new CacheEntry(value, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        => DeleteForTenantAsync(ResolveTenant(), key, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DeleteForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var row = await db.AppConfig
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            db.AppConfig.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        _cache.TryRemove(CacheKey(tenantId, key), out _);
    }

    private string ResolveTenant()
    {
        if (AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient) { return ambient; }
        using var scope = _rootProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId;
    }

    // The ambient identity (seeded by background Task.Run work that has no HttpContext — e.g. the
    // in-process pipeline run or a Board session) is the override of last resort; otherwise the
    // request-scoped ITenantContext. Without this, a circuit's fire-and-forget run reads/writes the
    // `default` tenant's keys instead of the signed-in member's.
    private static string ResolveTenant(IServiceProvider scopedProvider)
        => AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient
            ? ambient
            : scopedProvider.GetRequiredService<ITenantContext>().TenantId;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await using var scope = _rootProvider.CreateAsyncScope();
        var tenantId = ResolveTenant(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var keys = await db.AppConfig.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Key.StartsWith(prefix))
            .Select(x => x.Key)
            .OrderBy(k => k)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return keys;
    }

    private static (string Tenant, string Key) CacheKey(string tenantId, string key) => (tenantId, key);

    private string? TryUnprotect(string cipher)
    {
        string? Handle(Exception e)
        {
            _logger.LogWarning(e, "Failed to decrypt an app_config value; treating as unset");
            return null;
        }

        try
        {
            return _protector.Unprotect(cipher);
        }
        catch (System.Security.Cryptography.CryptographicException ex) { return Handle(ex); }
        catch (FormatException ex) { return Handle(ex); }
        catch (ArgumentException ex) { return Handle(ex); }
    }

    private readonly record struct CacheEntry(string? Value, DateTime FetchedUtc);
}
