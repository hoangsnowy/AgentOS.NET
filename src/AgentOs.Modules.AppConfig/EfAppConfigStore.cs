// EF-backed, DataProtection-encrypted IAppConfigStore. Keyed by (TenantId, Key) so each tenant
// owns its own LLM keys / GitHub PAT / etc. Singleton; per-op DI scope is created here and the
// tenant id is read out of the OUTER request scope via the captured IServiceProvider. A 15-second
// read cache keeps the LLM hot path off the DB on every call; cache keys include the tenant.
// RLS (ADR-0012 stage 2, GAP-14): config.app_config is bound like every tenant table. A flow that acts for a tenant
// reads and writes that tenant's rows only (plus the "__platform" rows every tenant may read); a flow with no tenant
// of its own (a background worker, an anonymous request) that names the tenant it works for is bound to that tenant
// for the one call — an explicit tenant FILLS a missing one, it never overrides the flow's own. Platform rows are
// written only inside RowLevelSecurity.PlatformScope (SettingsService does that for platform-scope settings).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.AppConfig.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
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
        return TryCached(tenantId, key, out var cached)
            ? cached
            : await ReadAsync(scope.ServiceProvider, tenantId, key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        if (TryCached(tenantId, key, out var cached))
        {
            return cached;
        }
        await using var scope = _rootProvider.CreateAsyncScope();
        return await ReadAsync(scope.ServiceProvider, tenantId, key, cancellationToken).ConfigureAwait(false);
    }

    private bool TryCached(string tenantId, string key, out string? value)
    {
        if (Cacheable(tenantId) && _cache.TryGetValue(CacheKey(tenantId, key), out var hit) && hit.FetchedUtc + CacheTtl > DateTime.UtcNow)
        {
            value = hit.Value;
            return true;
        }
        value = null;
        return false;
    }

    private async ValueTask<string?> ReadAsync(IServiceProvider scoped, string tenantId, string key, CancellationToken cancellationToken)
    {
        using var bound = BindTenant(tenantId);
        var db = scoped.GetRequiredService<AppConfigDbContext>();
        var row = await db.AppConfig.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken)
            .ConfigureAwait(false);
        var value = row is null ? null : TryUnprotect(row.EncryptedValue);
        if (Cacheable(tenantId))
        {
            _cache[CacheKey(tenantId, key)] = new CacheEntry(value, DateTime.UtcNow);
        }
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

        using var bound = BindTenant(tenantId);
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
        using var bound = BindTenant(tenantId);
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
        using var bound = BindTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppConfigDbContext>();
        var keys = await db.AppConfig.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Key.StartsWith(prefix))
            .Select(x => x.Key)
            .OrderBy(k => k)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return keys;
    }

    /// <summary>Bind a flow that has no tenant of its own to the tenant it names (see the file header); null when the flow
    /// already acts for a tenant, is a platform operation, or the rows are the shared platform rows.</summary>
    internal static IDisposable? BindTenant(string tenantId)
        => string.Equals(tenantId, RowLevelSecurity.PlatformSettingsTenant, StringComparison.Ordinal)
            ? null
            : RowLevelSecurity.TenantScopeIfUnbound(tenantId);

    /// <summary>False for a read the database refuses (RLS on, the flow acts for ANOTHER tenant): it must neither be served
    /// from the per-process cache (that would hand one tenant's value to another flow) nor pin its refused null there.</summary>
    internal static bool Cacheable(string tenantId)
        => !RowLevelSecurity.Enabled
           || RowLevelSecurity.InPlatformScope
           || string.Equals(tenantId, RowLevelSecurity.PlatformSettingsTenant, StringComparison.Ordinal)
           || RowLevelSecurity.CurrentTenant() is not { } flow
           || string.Equals(flow, tenantId, StringComparison.Ordinal);

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
