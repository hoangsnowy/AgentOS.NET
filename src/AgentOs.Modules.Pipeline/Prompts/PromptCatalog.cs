// The tunable agent prompts the Prompts admin app exposes. Key must match what each agent passes to
// IPromptOverrides.ResolveAsync; DefaultPrompt is the compiled-in baseline shown for reset/diff.

using System.Collections.Generic;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>One tunable agent prompt.</summary>
public sealed record PromptEntry(string Key, string DisplayName, string DefaultPrompt);

/// <summary>The agent system prompts a tenant can override at runtime.</summary>
public static class PromptCatalog
{
    /// <summary>Tunable prompts, in display order. Keys match the agents' ResolveAsync calls.</summary>
    public static IReadOnlyList<PromptEntry> All { get; } =
    [
        new("Requirement", "Requirement Agent", RequirementPrompt.System),
        new("Coding", "Coding Agent", CodingPrompt.System),
        new("Testing", "Testing Agent", TestingPrompt.System),
        new("Qa", "QA Agent", QaPrompt.System),
        new("Decomposer", "Decomposer (idea → tickets)", DecomposerPrompt.System),
    ];
}
