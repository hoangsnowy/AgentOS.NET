// Process-scoped in-memory IAppConfigStore. Keys survive only the current process lifetime.
// Fallback when no DB is configured.
//
// Tenant-partitioned like EfAppConfigStore: rows are keyed (tenant, key). The tenant-explicit overloads use
// the given tenant; the ambient overloads resolve AmbientIdentity, else the default tenant. (It used to be
// tenant-agnostic, which let one tenant's override — e.g. an agent profile — leak to every other tenant.)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;

namespace AgentOs.Modules.AppConfig;

public sealed class InMemoryAppConfigStore : IAppConfigStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Key), string> _items = new();

    private static string Ambient() =>
        AmbientIdentity.Current?.TenantId is { Length: > 0 } t ? t : ITenantContext.DefaultTenantId;

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => GetForTenantAsync(Ambient(), key, cancellationToken);

    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
        => SetForTenantAsync(Ambient(), key, value, cancellationToken);

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        => DeleteForTenantAsync(Ambient(), key, cancellationToken);

    public ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var tenant = Ambient();
        var keys = _items.Keys
            .Where(k => k.Tenant == tenant && k.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<string>>(keys);
    }

    public ValueTask<string?> GetForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_items.TryGetValue((Norm(tenantId), key), out var v) ? v : null);

    public ValueTask SetForTenantAsync(string tenantId, string key, string value, CancellationToken cancellationToken = default)
    {
        _items[(Norm(tenantId), key)] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        _items.TryRemove((Norm(tenantId), key), out _);
        return ValueTask.CompletedTask;
    }

    private static string Norm(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? ITenantContext.DefaultTenantId : tenantId;
}
