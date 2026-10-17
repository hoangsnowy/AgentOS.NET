// Model + price rules for the OpenAI-compatible provider (E0.8). Agent profiles default to vendor models
// (claude-sonnet-…, gpt-…) that a self-hosted server doesn't host: a catalogued vendor id is mapped to the
// configured server model, while any other id (qwen2.5-coder, llama3.1:70b, a Foundry deployment) is taken as
// the operator's deliberate choice. Cost uses the operator's own per-million rates — 0 by default on their own
// hardware — so local runs are metered truthfully instead of at the unknown-model fail-closed ceiling.

using System.Globalization;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Model mapping and pricing for <see cref="LlmProviders.OpenAiCompatible"/>.</summary>
internal static class OpenAiCompatibleModels
{
    /// <summary>Fallback when nothing is configured.</summary>
    public const string DefaultModel = "llama3.1";

    /// <summary>The model to request: a vendor-catalogued id → the configured model; anything else as given.</summary>
    public static string Resolve(string? requested, string? configured)
    {
        var fallback = string.IsNullOrWhiteSpace(configured) ? DefaultModel : configured.Trim();
        if (string.IsNullOrWhiteSpace(requested)) { return fallback; }
        return CostCalculator.IsKnown(requested) ? fallback : requested.Trim();
    }

    /// <summary>USD for <paramref name="usage"/> at the configured $/1M rates (cache reads billed as input).</summary>
    public static decimal Cost(TokenUsage usage, string? inputPerMTok, string? outputPerMTok)
    {
        System.ArgumentNullException.ThrowIfNull(usage);
        var input = Rate(inputPerMTok);
        var output = Rate(outputPerMTok);
        return ((usage.Input * input) + (usage.Output * output)) / 1_000_000m;

        static decimal Rate(string? s) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0m;
    }
}
