// "Test" on a Settings → Model keys card: a 5-token connectivity probe against ONE named provider, shared by the
// Web (in-process) and the Api (/llm/test?provider=…) so both probe the same way. The provider name is checked
// against a fixed allow-list (never an arbitrary keyed-service lookup from user input). The probe names Claude
// Haiku: Claude serves it, and the BYOK vendors / OpenAI-compatible resolvers map any Claude id to the tenant's
// own default model. Azure has no resolver — its model IS the deployment name — so the caller passes the
// workspace's default deployment.

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

    /// <summary>True for providers whose model is an Azure deployment name (no model resolver).</summary>
    public static bool UsesAzureDeployment(string provider) =>
        string.Equals(provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase) || string.Equals(provider, "MAF", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model to name in the probe (never empty — LlmRequest requires one).</summary>
    public static string ModelFor(string provider, string? azureDeployment) =>
        UsesAzureDeployment(provider) && !string.IsNullOrWhiteSpace(azureDeployment) ? azureDeployment.Trim() : ClaudeProbeModel;

    /// <summary>The probe request for <paramref name="provider"/>.</summary>
    public static LlmRequest Request(string provider, string? azureDeployment) =>
        new("You are a connectivity probe.", "Reply with the single word: OK", ModelFor(provider, azureDeployment), 0.0, 5);

    /// <summary>The workspace's default Azure deployment: the live setting, else the bootstrap option.</summary>
    public static string AzureDeployment(AgentOs.Domain.Configuration.ISettings? settings, string? tenantId, LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (settings is null ? null : LiveSettings.Read(settings, LlmSettings.AzureModel, tenantId)) ?? options.AzureOpenAi.Model;
    }
}
