// ISettings over the encrypted app_config store (ADR-0011). Precedence: operator lock → tenant → platform →
// seed (IConfiguration at the same key path) → descriptor default. Platform rows live under the reserved
// tenant "__platform" with an "@platform/" key prefix, which keeps them distinct even in the tenant-agnostic
// in-memory store used for standalone runs. Reads ride the store's own per-process cache.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.AppConfig.Settings;

/// <summary>The settings service.</summary>
public sealed class SettingsService : ISettings
{
    /// <summary>Reserved tenant id that holds platform-scope rows.</summary>
    public const string PlatformTenant = "__platform";

    /// <summary>Key prefix for platform-scope rows.</summary>
    public const string PlatformPrefix = "@platform/";

    /// <summary>Bootstrap-config section that pins operator-locked values.</summary>
    public const string LocksSection = "Settings:Locks";

    private readonly ISettingsCatalog _catalog;
    private readonly IAppConfigStore _store;
    private readonly IConfiguration _config;
    private readonly ISettingsAuditSink _audit;
    private readonly ILogger<SettingsService> _logger;
    private readonly TimeProvider _clock;
    private long _version;

    /// <summary>Create the service.</summary>
    public SettingsService(
        ISettingsCatalog catalog, IAppConfigStore store, IConfiguration config, ISettingsAuditSink audit,
        ILogger<SettingsService> logger, TimeProvider? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc />
    public async ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default)
    {
        if (!_catalog.TryGet(key, out var d))
        {
            throw new KeyNotFoundException($"Setting '{key}' is not declared — add it to a module's ISettingsContributor.");
        }
        return await ResolveAsync(d, tenantId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SettingView>> ListAsync(
        string? tenantId, string? category = null, CancellationToken cancellationToken = default)
    {
        var views = new List<SettingView>();
        foreach (var d in _catalog.All)
        {
            if (category is not null && !string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var v = await ResolveAsync(d, tenantId, cancellationToken).ConfigureAwait(false);
            var display = !v.HasValue ? null : d.IsSecret ? SettingValidation.Mask(v.Value!) : v.Value;
            views.Add(new SettingView(d, display, v.Source, v.HasValue));
        }
        return views;
    }

    /// <inheritdoc />
    public async ValueTask<SettingWriteResult> SetAsync(
        string key, string? value, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default)
    {
        if (!_catalog.TryGet(key, out var d))
        {
            return SettingWriteResult.Fail($"'{key}' is not a recognised setting.");
        }
        if (LockedValue(d) is not null)
        {
            return SettingWriteResult.Fail($"{d.Label} is locked by the operator (Settings:Locks) and cannot be changed here.");
        }
        if (scope == SettingScope.Tenant && d.Scope == SettingScope.Platform)
        {
            return SettingWriteResult.Fail($"{d.Label} is platform-wide; only a platform admin can change it.");
        }
        if (scope == SettingScope.Tenant && string.IsNullOrWhiteSpace(tenantId))
        {
            return SettingWriteResult.Fail("A tenant-scoped write needs a tenant.");
        }

        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && SettingValidation.Validate(d, trimmed) is { } error)
        {
            return SettingWriteResult.Fail(error);
        }

        var (rowTenant, rowKey) = Row(d.Key, scope, tenantId);
        var old = await _store.GetForTenantAsync(rowTenant, rowKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(trimmed))
        {
            await _store.DeleteForTenantAsync(rowTenant, rowKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.SetForTenantAsync(rowTenant, rowKey, trimmed, cancellationToken).ConfigureAwait(false);
        }
        Interlocked.Increment(ref _version);

        var change = new SettingChange(
            d.Key, scope, scope == SettingScope.Tenant ? tenantId : null, actor,
            Redact(d, old), Redact(d, trimmed), _clock.GetUtcNow());
        try
        {
            await _audit.RecordAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The audit sink contract is "must not throw"; a broken sink must still not undo a saved setting.
            _logger.LogWarning(ex, "Settings audit sink failed for {Key}", d.Key);
        }

        return SettingWriteResult.Success;
    }

    private async ValueTask<SettingValue> ResolveAsync(SettingDescriptor d, string? tenantId, CancellationToken ct)
    {
        if (LockedValue(d) is { } locked)
        {
            return new SettingValue(locked, SettingSource.Locked);
        }

        if (d.Scope == SettingScope.Tenant && !string.IsNullOrWhiteSpace(tenantId))
        {
            var t = await _store.GetForTenantAsync(tenantId, d.Key, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(t))
            {
                return new SettingValue(t, SettingSource.Tenant);
            }
        }

        var p = await _store.GetForTenantAsync(PlatformTenant, PlatformPrefix + d.Key, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(p))
        {
            return new SettingValue(p, SettingSource.Platform);
        }

        var seed = _config[d.Key];
        if (!string.IsNullOrWhiteSpace(seed))
        {
            return new SettingValue(seed, SettingSource.Seed);
        }

        return new SettingValue(d.DefaultValue, SettingSource.Default);
    }

    private string? LockedValue(SettingDescriptor d)
    {
        var v = _config[$"{LocksSection}:{d.Key}"];
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static (string Tenant, string Key) Row(string key, SettingScope scope, string? tenantId) =>
        scope == SettingScope.Platform ? (PlatformTenant, PlatformPrefix + key) : (tenantId!, key);

    private static string? Redact(SettingDescriptor d, string? value) =>
        value is null ? null : d.IsSecret ? "(secret)" : value;
}
