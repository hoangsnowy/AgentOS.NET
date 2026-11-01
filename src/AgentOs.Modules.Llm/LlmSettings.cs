// The LLM gateway's settings, declared for the UI-first registry (ADR-0011). Keys match the existing
// appsettings paths and the AppConfigKeys rows RuntimeOverrides already writes, so seeded and saved values
// resolve without migration.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Declares <c>Llm:*</c> settings.</summary>
/// <param name="offlineFallbackDefault">Default for <see cref="OfflineFallback"/> — on only in Development.</param>
public sealed class LlmSettings(bool offlineFallbackDefault = false) : ISettingsContributor
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

    /// <summary>OpenAI-compatible server base URL, e.g. <c>http://ollama:11434/v1</c> (platform-only: the server calls it).</summary>
    public const string CompatEndpoint = "Llm:OpenAiCompatible:Endpoint";

    /// <summary>OpenAI-compatible API key (blank for keyless servers).</summary>
    public const string CompatApiKey = "Llm:OpenAiCompatible:ApiKey";

    /// <summary>Model served when an agent profile names a vendor model this server doesn't host.</summary>
    public const string CompatModel = "Llm:OpenAiCompatible:Model";

    /// <summary>USD per 1M input tokens (0 for self-hosted).</summary>
    public const string CompatInputPrice = "Llm:OpenAiCompatible:InputPricePerMTok";

    /// <summary>USD per 1M output tokens (0 for self-hosted).</summary>
    public const string CompatOutputPrice = "Llm:OpenAiCompatible:OutputPricePerMTok";

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
            DefaultValue = offlineFallbackDefault ? "true" : "false",
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
            Help = "https://<resource>.openai.azure.com — an Azure OpenAI resource host (other hosts: operator config only).",
            // GAP-01: tenant-scoped and called server-side with the tenant's key — Azure resource hosts only.
            Validator = AgentOs.SharedKernel.Security.AzureOpenAiEndpoint.Validate,
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

        // BYOK vendors (fixed endpoints in VendorProviders): each tenant brings its own key.
        foreach (var v in VendorProviders.All)
        {
            yield return new(v.KeySetting, SettingKind.Secret, Category, $"{v.Name} API key")
            {
                Help = $"Your {v.Name} key — used when an agent's provider is {v.Name}. Endpoint {v.Endpoint}.",
            };
            yield return new(v.ModelSetting, SettingKind.Model, Category, $"{v.Name} default model")
            {
                DefaultValue = v.DefaultModel,
                Help = $"Used when an agent profile names a model {v.Name} doesn't serve.",
            };
        }

        // OpenAI-compatible (self-hosted / air-gapped). Platform scope: AgentOS.NET calls this URL server-side, so a
        // tenant must never be able to point it at an internal address (SSRF).
        yield return new(CompatEndpoint, SettingKind.Text, Category, "OpenAI-compatible endpoint")
        {
            Scope = SettingScope.Platform,
            Help = "Base URL of any /v1/chat/completions server — vLLM, Ollama (http://host:11434/v1), LM Studio, Azure AI Foundry.",
            Validator = v => System.Uri.TryCreate(v, System.UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
                ? null
                : "Enter an absolute http(s) URL, e.g. http://ollama:11434/v1.",
        };
        yield return new(CompatApiKey, SettingKind.Secret, Category, "OpenAI-compatible API key")
        {
            Scope = SettingScope.Platform,
            Help = "Leave empty for servers that need no key (Ollama, most vLLM setups).",
        };
        yield return new(CompatModel, SettingKind.Model, Category, "OpenAI-compatible model")
        {
            Scope = SettingScope.Platform,
            DefaultValue = "llama3.1",
            Help = "Used when an agent's profile names a vendor model (Claude/GPT) this server doesn't host.",
        };
        yield return new(CompatInputPrice, SettingKind.Number, Category, "OpenAI-compatible input price ($/1M)")
        {
            Scope = SettingScope.Platform, DefaultValue = "0", Min = 0, Max = 1000,
            Help = "What a million input tokens cost you — 0 for your own hardware. Feeds cost metrics and the budget.",
        };
        yield return new(CompatOutputPrice, SettingKind.Number, Category, "OpenAI-compatible output price ($/1M)")
        {
            Scope = SettingScope.Platform, DefaultValue = "0", Min = 0, Max = 1000,
        };
    }
}
