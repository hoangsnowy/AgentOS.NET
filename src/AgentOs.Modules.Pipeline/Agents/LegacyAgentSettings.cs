// Keys an agent role had before the one-agent registry. "Decomposer" became "Discovery" (2026-10): a tenant's saved
// Agents:Decomposer:* profile values (and the platform's, and an appsettings seed) keep working — read only while the
// Discovery key itself has no stored value. Legacy keys are no longer declared in the settings registry (they would
// show up as extra rows in Settings search), so they are read straight from the same rows SettingsService writes:
// the tenant row, then the platform row, then the bootstrap configuration. Read-only, except a Reset in Agent profiles
// also clears the tenant's legacy rows so the inherited value really comes back.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using Microsoft.Extensions.Configuration;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Reads (and resets) the values a role stored under its former key.</summary>
public sealed class LegacyAgentSettings
{
    /// <summary>The former key of the Discovery role (profile key and prompt key).</summary>
    public const string Decomposer = "Decomposer";

    private static readonly string[] Fields =
        [nameof(AgentOptions.Provider), nameof(AgentOptions.Model), nameof(AgentOptions.Temperature), nameof(AgentOptions.MaxTokens)];

    private readonly IAppConfigStore? _store;
    private readonly IConfiguration? _config;

    /// <summary>Create. Without a store or configuration there is nothing legacy to read.</summary>
    public LegacyAgentSettings(IAppConfigStore? store = null, IConfiguration? config = null)
    {
        _store = store;
        _config = config;
    }

    /// <summary>The former key of <paramref name="role"/>, or null when it was never renamed.</summary>
    public static string? LegacyRoleOf(string role) =>
        string.Equals(role, AgentRoles.Discovery, StringComparison.Ordinal) ? Decomposer : null;

    /// <summary><paramref name="role"/>'s <paramref name="field"/> saved under its former key (tenant → platform →
    /// configuration), or null. Never throws: a store failure is "nothing legacy".</summary>
    public async ValueTask<string?> ReadAsync(string role, string field, string? tenantId, CancellationToken ct = default)
    {
        if (LegacyRoleOf(role) is not { } legacy) { return null; }
        var key = AgentSettings.Key(legacy, field);
        if (_store is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tenantId)
                    && await _store.GetForTenantAsync(tenantId, key, ct).ConfigureAwait(false) is { } t && !string.IsNullOrWhiteSpace(t))
                {
                    return t.Trim();
                }
                if (await _store.GetForTenantAsync(SettingsService.PlatformTenant, SettingsService.PlatformPrefix + key, ct).ConfigureAwait(false) is { } p
                    && !string.IsNullOrWhiteSpace(p))
                {
                    return p.Trim();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // a legacy value is a courtesy: a failing store never breaks the current key's resolution
            }
        }
        return _config?[key] is { } seed && !string.IsNullOrWhiteSpace(seed) ? seed.Trim() : null;
    }

    /// <summary>True when <paramref name="tenantId"/> (or the platform/configuration) still holds any legacy value of
    /// <paramref name="role"/> — Agent profiles says so on the row.</summary>
    public async ValueTask<bool> AnyAsync(string role, string? tenantId, CancellationToken ct = default)
    {
        foreach (var field in Fields)
        {
            if (await ReadAsync(role, field, tenantId, ct).ConfigureAwait(false) is not null) { return true; }
        }
        return false;
    }

    /// <summary>Delete the tenant's own legacy rows of <paramref name="role"/> (Agent profiles → Reset).</summary>
    public async ValueTask ClearTenantAsync(string role, string tenantId, CancellationToken ct = default)
    {
        if (_store is null || LegacyRoleOf(role) is not { } legacy || string.IsNullOrWhiteSpace(tenantId)) { return; }
        foreach (var field in Fields)
        {
            await _store.DeleteForTenantAsync(tenantId, AgentSettings.Key(legacy, field), ct).ConfigureAwait(false);
        }
    }
}
