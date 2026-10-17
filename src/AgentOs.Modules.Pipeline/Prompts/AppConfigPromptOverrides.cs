// Reads a tenant's system-prompt override from the encrypted AppConfig KV store. Defensive: a missing
// store, a missing override, or any read failure all fall back to the compiled-in default — a prompt
// lookup must never break an agent run.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.AppConfig;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Prompts;

internal sealed class AppConfigPromptOverrides : IPromptOverrides
{
    private readonly IAppConfigStore? _config;
    private readonly ILogger<AppConfigPromptOverrides> _logger;

    public AppConfigPromptOverrides(ILogger<AppConfigPromptOverrides> logger, IAppConfigStore? config = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;
    }

    /// <summary>AppConfig key holding a tenant's override of an agent's system prompt.</summary>
    internal static string Key(string agentKey) => $"prompt/{agentKey}/system";

    public async Task<string?> ResolveRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = await ResolveAsync(WorkspaceRules.Key, string.Empty, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(rules) ? null : rules;
    }

    public async Task<string> ResolveAsync(string agentKey, string defaultPrompt, CancellationToken cancellationToken = default)
    {
        if (_config is null)
        {
            return defaultPrompt;
        }

        try
        {
            var tenant = AmbientIdentity.Current?.TenantId ?? ITenantContext.DefaultTenantId;
            var overridden = await _config.GetForTenantAsync(tenant, Key(agentKey), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(overridden) ? defaultPrompt : overridden;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { return OnLookupFailed(ex); }
        catch (System.Data.Common.DbException ex) { return OnLookupFailed(ex); }
        catch (System.Security.Cryptography.CryptographicException ex) { return OnLookupFailed(ex); }
        catch (TimeoutException ex) { return OnLookupFailed(ex); }
        catch (IOException ex) { return OnLookupFailed(ex); }
        catch (InvalidOperationException ex) { return OnLookupFailed(ex); }

        // A prompt lookup must never break an agent run — fall back to the default on any store/decrypt error.
        string OnLookupFailed(Exception ex)
        {
            _logger.LogWarning(ex, "Prompt override lookup failed for {Agent}; using the default prompt.", agentKey);
            return defaultPrompt;
        }
    }
}
