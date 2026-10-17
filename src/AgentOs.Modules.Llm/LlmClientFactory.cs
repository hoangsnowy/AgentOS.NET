// Resolves ILlmClient by provider name via keyed services. Every provider (Claude/AzureOpenAI/
// MAF/RemoteAgent) registers as a keyed ILlmClient under its canonical name; the factory just
// normalizes the requested name and does a keyed lookup. Effective provider honors runtime
// ForceProvider (Settings UI) and the LlmOptions.ForceProvider config value.

using System;
using System.Collections.Generic;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Llm;

/// <inheritdoc />
public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly IServiceProvider _services;
    private readonly LlmOptions _options;
    private readonly IRuntimeOverrides _overrides;
    private readonly ISettings? _settings;

    /// <summary>Create. With <paramref name="settings"/> the default provider, force provider and offline
    /// fallback are read live per call (Settings UI, no restart); without it they come from options/overrides.</summary>
    public LlmClientFactory(IServiceProvider services, IOptions<LlmOptions> options, IRuntimeOverrides overrides, ISettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options.Value ?? new LlmOptions();
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _settings = settings;
    }

    /// <inheritdoc />
    public ILlmClient CreateDefault() =>
        Create(_settings is null
            ? _options.Provider
            : LiveSettings.Read(_settings, LlmSettings.Provider, LiveSettings.CurrentTenant(_services)) ?? _options.Provider);

    /// <inheritdoc />
    public ILlmClient Create(string providerName)
    {
        // Settings path: one lookup covers tenant override → platform → appsettings seed (same key the old
        // RuntimeOverrides row + Llm:ForceProvider config used). Legacy path kept for hosts without a registry.
        var tenant = _settings is null ? null : LiveSettings.CurrentTenant(_services);
        var force = _settings is not null
            ? LiveSettings.Read(_settings, AppConfigKeys.LlmForceProvider, tenant)
            : !string.IsNullOrWhiteSpace(_overrides.ForceProvider) ? _overrides.ForceProvider : _options.ForceProvider;
        var effective = string.IsNullOrWhiteSpace(force) ? providerName : force;
        if (string.IsNullOrWhiteSpace(effective))
        {
            throw new ArgumentException("Provider name must not be empty.", nameof(providerName));
        }

        var primary = ResolveRequired(effective, providerName);

        var fallbacks = ResolveFallbacks(effective);

        // Keyless offline safety net: when enabled (standalone dev / E2E / demo), append the Offline provider
        // to the END of the chain so a no-key LlmException falls through to canned schema-valid output instead
        // of failing the run. Skipped when the effective provider already IS Offline, or it's already a
        // configured fallback, so it never appears twice.
        var offlineFallback = _settings is null
            ? _options.OfflineFallback
            : LiveSettings.ReadBool(_settings, LlmSettings.OfflineFallback, tenant, _options.OfflineFallback);
        if (offlineFallback
            && !string.Equals(NormalizeKey(effective), OfflineLlmClient.ProviderName, StringComparison.Ordinal))
        {
            var offline = _services.GetKeyedService<ILlmClient>(OfflineLlmClient.ProviderName);
            if (offline is not null && !fallbacks.Exists(c => c.Provider == OfflineLlmClient.ProviderName))
            {
                fallbacks.Add(offline);
            }
        }

        // No fallbacks declared → return the bare client, identical to the pre-failover behavior.
        if (fallbacks.Count == 0)
        {
            return primary;
        }

        var chain = new List<ILlmClient>(1 + fallbacks.Count) { primary };
        chain.AddRange(fallbacks);
        return new FailoverLlmClient(chain, _services.GetRequiredService<ILogger<FailoverLlmClient>>());
    }

    // The primary must resolve — a missing primary is a hard misconfiguration.
    private ILlmClient ResolveRequired(string effective, string requested)
    {
        var key = NormalizeKey(effective);
        return _services.GetKeyedService<ILlmClient>(key)
            ?? throw new LlmException(
                $"LLM provider '{requested}' (resolved to '{key}') is not registered. "
                + "Built-in: Claude | AzureOpenAI | MAF | RemoteAgent. "
                + "A plugin provider must register a keyed ILlmClient under this exact name.");
    }

    // Resolves the configured fallback providers for the effective primary as RAW keyed clients (never via
    // Create — that would recurse into failover composition). Unknown/duplicate/self entries are skipped so
    // a stray fallback name degrades gracefully rather than breaking an otherwise-healthy primary.
    private List<ILlmClient> ResolveFallbacks(string effectivePrimary)
    {
        var result = new List<ILlmClient>();
        if (_options.Fallbacks is not { Count: > 0 } map)
        {
            return result;
        }

        var primaryKey = NormalizeKey(effectivePrimary);
        if (!map.TryGetValue(effectivePrimary, out var names) && !map.TryGetValue(primaryKey, out names))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { primaryKey };
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = NormalizeKey(name);
            if (!seen.Add(key))
            {
                continue;
            }

            var client = _services.GetKeyedService<ILlmClient>(key);
            if (client is not null)
            {
                result.Add(client);
            }
        }

        return result;
    }

    // Maps known aliases to their canonical built-in key. An UNKNOWN name falls through to the trimmed
    // original (case preserved) so a plugin-registered keyed ILlmClient resolves under its own name.
    internal static string NormalizeKey(string providerName)
    {
        var trimmed = providerName.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "CLAUDE" or "ANTHROPIC" => "Claude",
            "AZUREOPENAI" or "AZURE" => "AzureOpenAI",
            "OPENAI" => LlmProviders.OpenAi,
            "GEMINI" or "GOOGLE" => LlmProviders.Gemini,
            "XAI" or "GROK" => LlmProviders.Xai,
            "MAF" or "MAF-AZURE" or "AGENTFRAMEWORK" => "MAF",
            "REMOTEAGENT" or "REMOTE" or "IDE" => "RemoteAgent",
            "OPENAICOMPATIBLE" or "OPENAI-COMPATIBLE" or "LOCAL" or "OLLAMA" or "VLLM" => LlmProviders.OpenAiCompatible,
            _ => trimmed,
        };
    }
}
