// AgentOs.Domain/Llm/LlmOptions.cs
// Sprint 1 — Options binding for the LLM Gateway (bound from the appsettings "Llm" section).

namespace AgentOs.Domain.Llm;

/// <summary>
/// Root options bound from the <c>appsettings.json</c> <c>"Llm"</c> section.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Section name used for <c>Configuration.GetSection(...)</c>.</summary>
    public const string SectionName = "Llm";

    /// <summary>Default provider: <c>"Claude"</c>, <c>"AzureOpenAI"</c>, <c>"MAF"</c>, or <c>"RemoteAgent"</c>.</summary>
    public string Provider { get; set; } = "Claude";

    /// <summary>
    /// Optional. When set (e.g. <c>"AzureOpenAI"</c>), every agent uses this provider regardless of its
    /// per-agent <c>Agents:&lt;Name&gt;:Provider</c>. Lets you run the whole pipeline on one provider
    /// (e.g. Azure-only, no Anthropic key needed). Leave empty/null for the per-agent hybrid configuration.
    /// </summary>
    public string? ForceProvider { get; set; }

    /// <summary>
    /// Optional inter-provider failover map: provider name → ordered fallback providers. When the primary
    /// provider's client throws an <see cref="LlmException"/> (its provider-level failure signal — e.g.
    /// every key is rate-limited, or no key is configured), the gateway transparently retries the same
    /// request on the next provider in the list. Empty (default) keeps single-provider behavior. This sits
    /// ABOVE each provider's own intra-provider key-pool failover. Example:
    /// <c>"Llm:Fallbacks": { "Claude": ["AzureOpenAI"], "AzureOpenAI": ["Claude"] }</c>.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> Fallbacks { get; set; }
        = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, the gateway appends a keyless, deterministic <c>Offline</c> provider to the END of every
    /// provider's failover chain. A real provider still answers whenever a key is configured; only when it
    /// throws an <see cref="LlmException"/> (e.g. no API key) does the request fall through to canned,
    /// schema-valid offline output — so the Workflow studio + 5-agent pipeline RUN on a fresh checkout with
    /// no secrets (demo, E2E, first launch). Defaults to false; the standalone Web dev config turns it on,
    /// Production turns it off so a missing cloud key fails loudly instead of silently serving canned data.
    /// </summary>
    public bool OfflineFallback { get; set; }

    /// <summary>Claude (Anthropic) client configuration.</summary>
    public ClaudeOptions Claude { get; set; } = new();

    /// <summary>Azure OpenAI client configuration.</summary>
    public AzureOpenAiOptions AzureOpenAi { get; set; } = new();
}

/// <summary>Options for Claude (Anthropic Messages API).</summary>
public sealed class ClaudeOptions
{
    /// <summary>API key (set via user-secrets or env var, do NOT commit).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional pool of API keys for round-robin + rate-limit failover. When a key returns HTTP 429 the
    /// router cools it down and routes to the next key. Combined with <see cref="ApiKey"/> (and any runtime
    /// override) into one distinct pool. Example: <c>"Llm:Claude:ApiKeys": ["sk-ant-a","sk-ant-b"]</c>.
    /// </summary>
    public System.Collections.Generic.List<string> ApiKeys { get; set; } = new();

    /// <summary>Base URL of the Anthropic API.</summary>
    public string Endpoint { get; set; } = "https://api.anthropic.com";

    /// <summary>Default model (for example <c>claude-sonnet-4-6</c>).</summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>The <c>anthropic-version</c> header.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Maximum number of retries on 429/5xx/timeout.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>HTTP timeout per request (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Options for Azure OpenAI.</summary>
public sealed class AzureOpenAiOptions
{
    /// <summary>Azure OpenAI API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional pool of API keys for round-robin + rate-limit failover (e.g. keys from several Azure
    /// resources/regions). On HTTP 429 a key is cooled down and traffic routes to the next.
    /// </summary>
    public System.Collections.Generic.List<string> ApiKeys { get; set; } = new();

    /// <summary>Azure OpenAI endpoint (for example <c>https://my-resource.openai.azure.com</c>).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Deployment name / model alias.</summary>
    public string Model { get; set; } = "gpt-4.1";

    /// <summary>
    /// Optional. Maps the deployment alias (<see cref="Model"/>) to a canonical model id in the cost
    /// price table (e.g. deployment <c>"gpt41-prod"</c> → PricingModel <c>"gpt-4.1"</c>). Real Azure
    /// deployment names are arbitrary aliases that are NOT priced prefixes, so cost would silently
    /// compute to $0 and the budget gate + Cost app would undercount. The cost calculator is fed THIS
    /// id; leave empty to price directly off the deployment id. Telemetry/logs/<see cref="LlmResponse.Model"/>
    /// always report the deployment id (the alias), not this.
    /// </summary>
    public string PricingModel { get; set; } = string.Empty;

    /// <summary>API version (for example <c>2024-10-21</c>).</summary>
    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>Maximum number of retries.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Timeout (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

