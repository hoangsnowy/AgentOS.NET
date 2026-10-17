// Audits every write that bypasses ISettings (plan C.3): LLM keys + GitHub token (RuntimeOverrides), prompt
// overrides, tool policy, budget, workspace credentials, the legacy /settings endpoint. Keys declared in the
// settings catalog are skipped — SettingsService audits those itself with old → new. Values are NEVER recorded
// here (these rows hold API keys, PATs and prompts): only "set (N chars)" / "cleared", except an allowlist of
// non-secret governance keys whose value IS the change (tool allowlist + enforce flag, budget cap + enforce).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.AppConfig;

/// <summary>Decorates <see cref="IAppConfigStore"/>; writes pass through, then a redacted change is audited.</summary>
internal sealed class AuditingAppConfigStore(IAppConfigStore inner, IServiceProvider services, TimeProvider? clock = null) : IAppConfigStore
{
    // Governance keys whose value is not secret and IS the audited change.
    private static readonly HashSet<string> ValueVisible = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools/allowlist", "tools/enforce", "budget/monthly-cap-usd", "budget/enforce",
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

    public ValueTask<string?> GetForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
        => inner.GetForTenantAsync(tenantId, key, cancellationToken);

    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await inner.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        await AuditAsync(AmbientTenant(), key, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await inner.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        await AuditAsync(AmbientTenant(), key, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetForTenantAsync(string tenantId, string key, string value, CancellationToken cancellationToken = default)
    {
        await inner.SetForTenantAsync(tenantId, key, value, cancellationToken).ConfigureAwait(false);
        await AuditAsync(tenantId, key, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteForTenantAsync(string tenantId, string key, CancellationToken cancellationToken = default)
    {
        await inner.DeleteForTenantAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
        await AuditAsync(tenantId, key, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the audit row shows for a write: the value only for allowlisted keys, else its size.</summary>
    internal static string Describe(string key, string? value)
        => value is null ? "cleared"
         : ValueVisible.Contains(key) ? value
         : $"set ({value.Length} chars, value hidden)";

    private async ValueTask AuditAsync(string? tenantId, string key, string? value, CancellationToken ct)
    {
        // Catalogued settings are audited by SettingsService (with old → new and secret masking).
        if (services.GetService<ISettingsCatalog>() is { } catalog && catalog.TryGet(SettingsKey(key), out _))
        {
            return;
        }
        if (services.GetService<ISettingsAuditSink>() is not { } sink)
        {
            return;
        }
        var actor = AmbientIdentity.Current?.UserId is { Length: > 0 } user ? user : CurrentUser() ?? "system";
        await sink.RecordAsync(new SettingChange(
            key, SettingScope.Tenant, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId, actor,
            OldValue: null, NewValue: Describe(key, value), _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }

    // SettingsService rows are the descriptor key, or "@platform/<key>" for the platform scope.
    private static string SettingsKey(string key)
        => key.StartsWith(Settings.SettingsService.PlatformPrefix, StringComparison.Ordinal)
            ? key[Settings.SettingsService.PlatformPrefix.Length..]
            : key;

    private string? AmbientTenant()
    {
        if (AmbientIdentity.Current?.TenantId is { Length: > 0 } ambient) { return ambient; }
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetService<ITenantContext>()?.TenantId;
    }

    private string? CurrentUser()
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetService<ITenantContext>();
        return ctx?.UserName ?? ctx?.UserId;
    }
}
