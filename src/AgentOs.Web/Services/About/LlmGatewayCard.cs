// The LLM gateway card on System → About. Unlike the infrastructure probes this is PER TENANT (a tenant's
// own keys and forced provider win over the platform's), so it is built per circuit from LlmConfigView and
// never enters the shared snapshot. No provider is called — a probe must not spend tokens.

using System.Collections.Generic;

namespace AgentOs.Web.Services.About;

/// <summary>Builds the LLM gateway <see cref="ServiceStatus"/> from a tenant's effective configuration.</summary>
public static class LlmGatewayCard
{
    /// <summary>Microsoft.Extensions.AI version the gateway is built on.</summary>
    public static string SdkVersion { get; } = Format.ShortVersion(
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(Microsoft.Extensions.AI.IChatClient).Assembly)?.InformationalVersion);

    /// <summary>The card for <paramref name="cfg"/>.</summary>
    public static ServiceStatus From(LlmEffectiveConfig cfg)
    {
        System.ArgumentNullException.ThrowIfNull(cfg);
        var provider = cfg.EffectiveProvider;
        var (health, summary, mode, map) = cfg.HasKeyFor(provider)
            ? (ServiceHealth.Healthy, $"Agents call {provider} with this tenant's key", $"Live · {provider}", $"live · {provider}")
            : cfg.WillServeCanned
                ? (ServiceHealth.Degraded, $"No {provider} key — the offline fallback answers with canned text", "Offline · canned answers (≈, not billed)", "offline · canned")
                : (ServiceHealth.Down, $"No key for {provider} and the offline fallback is off", "Unavailable", "no provider");

        var facts = new List<ServiceFact>
        {
            new("Mode", mode),
            new("Default provider", cfg.DefaultProvider),
            new("Forced provider", cfg.ForceSource switch
            {
                ForceSource.RuntimeOverride => $"{cfg.Force} (this tenant's setting)",
                ForceSource.AppSettings => $"{cfg.Force} (platform)",
                _ => "—",
            }),
            new("Anthropic key", cfg.AnthropicKeySet ? "Configured" : "Not set"),
            new("Azure OpenAI", cfg.HasKeyFor("AzureOpenAI") ? "Configured" : cfg.AzureKeySet ? "Key set, endpoint missing" : "Not set"),
            new("OpenAI / Gemini / xAI", cfg.VendorKeys.Count == 0 ? "No keys" : string.Join(", ", cfg.VendorKeys.Order(StringComparer.Ordinal))),
            new("OpenAI-compatible", string.IsNullOrWhiteSpace(cfg.CompatEndpoint) ? "Not set" : "Configured (platform)"),
            new("Offline fallback", cfg.OfflineFallback ? "On" : "Off"),
            new("SDK", $"Microsoft.Extensions.AI {SdkVersion}"),
        };

        return new ServiceStatus("llm", "LLM gateway", "Model providers for the agents", "lightning", health)
        {
            MapLabel = map,
            Summary = summary,
            Facts = facts,
            Infra =
            [
                .. cfg.AzureEndpoint is { Length: > 0 } azure ? new[] { new ServiceFact("Azure endpoint", azure) } : [],
                .. cfg.CompatEndpoint is { Length: > 0 } compat ? new[] { new ServiceFact("OpenAI-compatible endpoint", compat) } : [],
            ],
        };
    }
}
