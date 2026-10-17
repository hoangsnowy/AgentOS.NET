// Tenant-explicit read/write of an agent's system-prompt override and of the tenant's workspace rules, with a
// version history per prompt (who, when, what — newest first, last 20) so a bad edit can be rolled back. Used by
// the admin Prompts app (a Blazor circuit with no HttpContext), so the tenant id is passed explicitly. Backed by
// AppConfig (encrypted; every write is also in the audit trail).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>Reads + writes a tenant's per-agent system-prompt overrides, workspace rules and their history.</summary>
public interface IPromptOverrideService
{
    /// <summary>The tenant's override for the agent, or null when none is set.</summary>
    Task<string?> GetAsync(string tenantId, string agentKey, CancellationToken cancellationToken = default);

    /// <summary>Sets the override (blank clears it back to the default) and records a version.</summary>
    Task SetAsync(string tenantId, string agentKey, string prompt, string actor = "admin", CancellationToken cancellationToken = default);

    /// <summary>Clears the override (revert to the compiled-in default) and records a version.</summary>
    Task ClearAsync(string tenantId, string agentKey, string actor = "admin", CancellationToken cancellationToken = default);

    /// <summary>Saved versions of <paramref name="agentKey"/> (or <see cref="WorkspaceRules.Key"/>), newest first.</summary>
    Task<IReadOnlyList<PromptVersion>> GetHistoryAsync(string tenantId, string agentKey, CancellationToken cancellationToken = default);
}

internal sealed class PromptOverrideService(IAppConfigStore config, TimeProvider? clock = null) : IPromptOverrideService
{
    private readonly IAppConfigStore _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<string?> GetAsync(string tenantId, string agentKey, CancellationToken cancellationToken = default)
        => await _config.GetForTenantAsync(tenantId, AppConfigPromptOverrides.Key(agentKey), cancellationToken).ConfigureAwait(false);

    public async Task SetAsync(string tenantId, string agentKey, string prompt, string actor = "admin", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await ClearAsync(tenantId, agentKey, actor, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _config.SetForTenantAsync(tenantId, AppConfigPromptOverrides.Key(agentKey), prompt, cancellationToken).ConfigureAwait(false);
        await RecordAsync(tenantId, agentKey, actor, prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(string tenantId, string agentKey, string actor = "admin", CancellationToken cancellationToken = default)
    {
        await _config.DeleteForTenantAsync(tenantId, AppConfigPromptOverrides.Key(agentKey), cancellationToken).ConfigureAwait(false);
        await RecordAsync(tenantId, agentKey, actor, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromptVersion>> GetHistoryAsync(string tenantId, string agentKey, CancellationToken cancellationToken = default)
        => WorkspaceRules.Parse(await _config.GetForTenantAsync(tenantId, WorkspaceRules.HistoryKey(agentKey), cancellationToken).ConfigureAwait(false));

    private async Task RecordAsync(string tenantId, string agentKey, string actor, string text, CancellationToken ct)
    {
        var key = WorkspaceRules.HistoryKey(agentKey);
        var existing = await _config.GetForTenantAsync(tenantId, key, ct).ConfigureAwait(false);
        var updated = WorkspaceRules.Append(existing, new PromptVersion(_clock.GetUtcNow(), string.IsNullOrWhiteSpace(actor) ? "admin" : actor, text));
        await _config.SetForTenantAsync(tenantId, key, updated, ct).ConfigureAwait(false);
    }
}
