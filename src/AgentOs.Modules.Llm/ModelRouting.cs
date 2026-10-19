// UX phase 1 (docs/plans/ux-simplification.md): Agent profiles shows only the MODEL by default — the provider is
// inferred from it, so an admin choosing "gemini-3.8-flash" doesn't also have to know to switch the provider to
// Gemini. The current provider is kept whenever it already serves the model (an Azure deployment of a GPT model
// stays on Azure) and for providers that pick their own model (paired machine, OpenAI-compatible, plugins).

using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Which provider serves a chosen model.</summary>
public static class ModelRouting
{
    /// <summary>The provider to use for <paramref name="model"/>, given the row's <paramref name="currentProvider"/>.</summary>
    public static string ProviderFor(string? currentProvider, string model)
    {
        var current = currentProvider?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model)) { return current; }
        var currentFamily = CostCalculator.FamilyOf(current);
        // Self-model providers (RemoteAgent, OpenAI-compatible, plugins) map any model themselves — keep them.
        if (current.Length > 0 && currentFamily is null) { return current; }
        var family = CostCalculator.ModelFamily(model);
        if (family == currentFamily) { return current; }
        return family switch
        {
            "anthropic" => "Claude",
            "gemini" => LlmProviders.Gemini,
            "xai" => LlmProviders.Xai,
            _ => LlmProviders.OpenAi,
        };
    }
}
