// Runtime per-tenant system-prompt overrides. An agent consults this before each run: it returns the
// tenant's saved override for that agent, or the compiled-in default when none is set. Optional on the
// agents (null => default), so the feature adds nothing until a tenant saves an override in the Prompts app.

using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>Resolves an agent's effective system prompt for the current tenant.</summary>
public interface IPromptOverrides
{
    /// <summary>Returns the tenant's override for <paramref name="agentKey"/>, or <paramref name="defaultPrompt"/>
    /// when none is set (or on any lookup failure — never throws).</summary>
    Task<string> ResolveAsync(string agentKey, string defaultPrompt, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's workspace rules (applied to every agent), or null — never throws.</summary>
    Task<string?> ResolveRulesAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
