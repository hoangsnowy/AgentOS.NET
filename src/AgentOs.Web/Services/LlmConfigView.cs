// Single source of the EFFECTIVE LLM configuration for the desktop. The per-tenant runtime override wins,
// then the appsettings/env platform fallback — mirroring LlmClientFactory.Create's precedence so the
// Settings > Status table and the "Live · provider" chips can never claim a provider/key the actual run
// won't use. Before this, that precedence was re-derived in three razor files and drifted.

using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.Options;

namespace AgentOs.Web.Services;

/// <summary>Where an effective ForceProvider came from.</summary>
public enum ForceSource
{
    /// <summary>No force provider — agents use their per-agent configuration.</summary>
    None,
    /// <summary>This tenant's runtime override (Settings UI).</summary>
    RuntimeOverride,
    /// <summary>The platform appsettings/env value.</summary>
    AppSettings,
}

/// <summary>A resolved snapshot of the effective LLM configuration for one tenant.</summary>
public sealed record LlmEffectiveConfig(
    string DefaultProvider,
    string? Force,
    ForceSource ForceSource,
    bool AnthropicKeySet,
    bool AzureKeySet,
    string? AzureEndpoint)
{
    /// <summary>Terse provider label for a "Live · …" chip: the forced provider, else the default.</summary>
    public string ChipLabel => string.IsNullOrWhiteSpace(Force) ? DefaultProvider : $"{Force} (forced)";

    /// <summary><c>Llm:OfflineFallback</c> — a keyless provider is answered by the canned offline client.</summary>
    public bool OfflineFallback { get; init; }

    /// <summary>The platform's OpenAI-compatible endpoint (vLLM / Ollama / Foundry), or null when not configured.</summary>
    public string? CompatEndpoint { get; init; }

    /// <summary>The provider a run will ask for: the forced one, else the default.</summary>
    public string EffectiveProvider => string.IsNullOrWhiteSpace(Force) ? DefaultProvider : Force;

    /// <summary>True when the effective provider has no usable key and the offline fallback will answer with
    /// canned text instead — the chip must say "Offline", not "Live".</summary>
    public bool WillServeCanned => OfflineFallback && !HasKeyFor(EffectiveProvider);

    /// <summary>Whether <paramref name="provider"/> has the credentials it needs. Providers that don't take a
    /// key here (MAF, RemoteAgent, plugins) report true — they fail or succeed on their own terms.</summary>
    public bool HasKeyFor(string provider) => provider.Trim().ToUpperInvariant() switch
    {
        "CLAUDE" or "ANTHROPIC" => AnthropicKeySet,
        "AZUREOPENAI" or "AZURE" or "OPENAI" => AzureKeySet && !string.IsNullOrWhiteSpace(AzureEndpoint),
        "OPENAICOMPATIBLE" or "OPENAI-COMPATIBLE" or "LOCAL" or "OLLAMA" or "VLLM" => !string.IsNullOrWhiteSpace(CompatEndpoint),
        "OFFLINE" => false,
        _ => true,
    };
}

/// <summary>Resolves <see cref="LlmEffectiveConfig"/> for a tenant by reading <see cref="IRuntimeOverrides"/>
/// under the tenant's ambient identity (a Blazor circuit has no <c>HttpContext</c>) then falling back to
/// appsettings. Scoped. The pure <see cref="Combine"/> overload is shared with the Settings tab, which
/// sources the override values from its own (split-mode-aware) seeded fields instead of this reader.</summary>
public sealed class LlmConfigView(IRuntimeOverrides overrides, IOptions<LlmOptions> options, ISettings? settings = null)
{
    private readonly IRuntimeOverrides _overrides = overrides;
    private readonly LlmOptions _opts = options.Value;
    private readonly ISettings? _settings = settings;

    /// <summary>Read this tenant's overrides (under its ambient identity; skipped standalone) and combine
    /// them with the appsettings fallback. Call once per window open — each override read is a 15s-cached
    /// store lookup.</summary>
    public LlmEffectiveConfig Resolve(string? tenantId, string? userId = null)
    {
        using var _ = AmbientIdentity.PushOrNull(tenantId, userId);
        var cfg = Combine(_opts, _overrides.ForceProvider, _overrides.AnthropicApiKey, _overrides.AzureApiKey, _overrides.AzureEndpoint);
        if (_settings is null)
        {
            return cfg;
        }
        // Same live values LlmClientFactory reads per call (Settings UI), so the chip can't drift from a run.
        return cfg with
        {
            DefaultProvider = LiveSettings.Read(_settings, LlmSettings.Provider, tenantId) ?? cfg.DefaultProvider,
            OfflineFallback = LiveSettings.ReadBool(_settings, LlmSettings.OfflineFallback, tenantId, cfg.OfflineFallback),
            CompatEndpoint = LiveSettings.Read(_settings, LlmSettings.CompatEndpoint, null),
        };
    }

    /// <summary>Pure precedence combine: per-tenant override first, then the platform appsettings value.</summary>
    public static LlmEffectiveConfig Combine(
        LlmOptions opts, string? forceOverride, string? anthropicOverride, string? azureOverride, string? endpointOverride)
    {
        var (force, source) = EffectiveForce(forceOverride, opts.ForceProvider);
        return new LlmEffectiveConfig(
            opts.Provider, force, source,
            AnthropicKeySet: IsSet(anthropicOverride, opts.Claude.ApiKey),
            AzureKeySet: IsSet(azureOverride, opts.AzureOpenAi.ApiKey),
            AzureEndpoint: FirstNonBlank(endpointOverride, opts.AzureOpenAi.Endpoint))
        {
            OfflineFallback = opts.OfflineFallback,
        };
    }

    /// <summary>"Set" when EITHER the tenant override OR the platform fallback has a value.</summary>
    public static bool IsSet(string? tenantOverride, string? platformFallback)
        => !string.IsNullOrWhiteSpace(tenantOverride) || !string.IsNullOrWhiteSpace(platformFallback);

    /// <summary>The effective ForceProvider + where it came from.</summary>
    public static (string? Value, ForceSource Source) EffectiveForce(string? tenantOverride, string? platformForce)
        => !string.IsNullOrWhiteSpace(tenantOverride) ? (tenantOverride, ForceSource.RuntimeOverride)
         : !string.IsNullOrWhiteSpace(platformForce) ? (platformForce, ForceSource.AppSettings)
         : (null, ForceSource.None);

    private static string? FirstNonBlank(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;
}
