// What every Board agent does before its LLM call, in one place: resolve its live profile (Settings → Agent profiles —
// provider, model, temperature, max output tokens; seeds in AgentsOptions) and its system prompt (Settings → Prompts —
// the tenant's override of the template or the built-in one, filled per run, then the fixed security rule, then the
// tenant's workspace rules). Both are read per call, so an edit applies to the next run without a restart.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Prompts;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Profile and prompt resolution shared by the Board agents.</summary>
public static class BoardAgent
{
    private static readonly AgentsOptions Seeds = new();

    /// <summary>The live profile for <paramref name="role"/>: the settings registry when wired, else the seed.</summary>
    public static async ValueTask<AgentOptions> ProfileAsync(IAgentProfiles? profiles, IOptions<AgentsOptions>? options, string role,
        CancellationToken ct)
    {
        var seed = (options?.Value ?? Seeds).For(role) ?? Seeds.For(role)
            ?? throw new ArgumentException($"'{role}' is not a registered agent role.", nameof(role));
        return profiles is null ? seed : await profiles.ResolveAsync(role, seed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The system prompt for <paramref name="role"/>: the tenant's override of <paramref name="defaultTemplate"/> (or the
    /// template itself), its placeholders filled from <paramref name="values"/>, then <paramref name="fixedRules"/> (never
    /// overridable — e.g. the untrusted-text rule), then the tenant's workspace rules.
    /// </summary>
    public static async Task<string> SystemPromptAsync(IPromptOverrides? prompts, string role, string defaultTemplate,
        IReadOnlyDictionary<string, string>? values, string? fixedRules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(defaultTemplate);
        var template = prompts is null ? defaultTemplate : await prompts.ResolveAsync(role, defaultTemplate, ct).ConfigureAwait(false);
        var text = values is null ? template : PromptTemplate.Fill(template, values);
        if (!string.IsNullOrEmpty(fixedRules)) { text += "\n\n" + fixedRules; }
        if (prompts is not null) { text += WorkspaceRules.ToPromptBlock(await prompts.ResolveRulesAsync(ct).ConfigureAwait(false)); }
        return text;
    }
}
