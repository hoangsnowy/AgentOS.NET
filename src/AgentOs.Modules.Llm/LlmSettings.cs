// The LLM gateway's settings, declared for the UI-first registry (ADR-0011). Keys match the existing
// appsettings paths and the AppConfigKeys rows RuntimeOverrides already writes, so seeded and saved values
// resolve without migration.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Declares <c>Llm:*</c> settings.</summary>
public sealed class LlmSettings : ISettingsContributor
{
    /// <summary>UI category for provider wiring.</summary>
    public const string Category = "LLM providers";

    /// <summary>Default provider when an agent profile doesn't name one.</summary>
    public const string Provider = "Llm:Provider";

    /// <summary>Serve canned offline output when a provider has no key (dev/demo only).</summary>
    public const string OfflineFallback = "Llm:OfflineFallback";

    /// <summary>Azure OpenAI default deployment.</summary>
    public const string AzureModel = "Llm:AzureOpenAi:Model";

    /// <summary>Canonical price-table id for an aliased Azure deployment.</summary>
    public const string AzurePricingModel = "Llm:AzureOpenAi:PricingModel";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(Provider, SettingKind.Choice, Category, "Default provider")
        {
            DefaultValue = "Claude",
            Choices = LlmProviders.Selectable,
            Help = "Used by any agent whose profile doesn't pick a provider.",
        };
        yield return new(AppConfigKeys.LlmForceProvider, SettingKind.Choice, Category, "Force provider")
        {
            Choices = LlmProviders.Selectable,
            Help = "Route every agent to one provider, overriding agent profiles. Leave empty for per-agent routing.",
        };
        yield return new(OfflineFallback, SettingKind.Boolean, Category, "Offline fallback")
        {
            DefaultValue = "false",
            Scope = SettingScope.Platform,
            Help = "When a provider has no key, answer with canned output instead of failing. Runs are labelled Offline. Dev/demo only.",
        };
        yield return new(AppConfigKeys.LlmClaudeApiKey, SettingKind.Secret, Category, "Anthropic API key");
        yield return new(AppConfigKeys.LlmClaudeApiKeys, SettingKind.Secret, Category, "Anthropic key pool")
        {
            Help = "Extra keys, one per line or comma-separated — round-robin with 429 failover.",
        };
        yield return new(AppConfigKeys.LlmAzureEndpoint, SettingKind.Url, Category, "Azure OpenAI endpoint")
        {
            Help = "https://<resource>.openai.azure.com",
        };
        yield return new(AppConfigKeys.LlmAzureApiKey, SettingKind.Secret, Category, "Azure OpenAI API key");
        yield return new(AppConfigKeys.LlmAzureApiKeys, SettingKind.Secret, Category, "Azure OpenAI key pool");
        yield return new(AzureModel, SettingKind.Model, Category, "Azure default deployment")
        {
            DefaultValue = "gpt-4.1",
        };
        yield return new(AzurePricingModel, SettingKind.Model, Category, "Azure pricing model")
        {
            Help = "Canonical model id for cost when the deployment name is an alias (e.g. gpt41-prod → gpt-4.1).",
        };
    }
}
