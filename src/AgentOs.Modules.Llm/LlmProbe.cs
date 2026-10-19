// "Test" on a Settings → Model keys card: a 5-token connectivity probe against ONE named provider, shared by the
// Web (in-process) and the Api (/llm/test?provider=…) so both probe the same way. The provider name is checked
// against a fixed allow-list (never an arbitrary keyed-service lookup from user input), and the probe asks for
// the cheapest model the provider maps: a Claude probe names Haiku; every other provider gets an empty model
// and serves its own configured default (Azure deployment, BYOK vendor default, OpenAI-compatible model).

using System;
using System.Collections.Frozen;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Builds the per-provider connectivity probe.</summary>
public static class LlmProbe
{
    /// <summary>The cheapest current Claude model (the probe never needs more).</summary>
    public const string ClaudeProbeModel = "claude-haiku-5-5";

    /// <summary>Providers a probe may target (case-insensitive).</summary>
    public static FrozenSet<string> Probeable { get; } = new[]
    {
        "Claude", "Anthropic", "AzureOpenAI", LlmProviders.OpenAi, LlmProviders.Gemini, LlmProviders.Xai,
        LlmProviders.OpenAiCompatible, "MAF",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="provider"/> may be probed.</summary>
    public static bool CanProbe(string? provider) => !string.IsNullOrWhiteSpace(provider) && Probeable.Contains(provider.Trim());

    /// <summary>The model to name in the probe: Haiku for Claude, else empty (the provider's own default).</summary>
    public static string ModelFor(string provider) =>
        string.Equals(provider, "Claude", StringComparison.OrdinalIgnoreCase) || string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase)
            ? ClaudeProbeModel
            : string.Empty;

    /// <summary>The probe request for <paramref name="provider"/>.</summary>
    public static LlmRequest Request(string provider) =>
        new("You are a connectivity probe.", "Reply with the single word: OK", ModelFor(provider), 0.0, 5);
}
