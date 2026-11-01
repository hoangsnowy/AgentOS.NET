// GAP-01 — which Azure OpenAI endpoint a call uses, and how it is reached. A value from Settings (IRuntimeOverrides,
// tenant- or platform-admin-set at runtime) must pass the AzureOpenAiEndpoint allowlist — re-checked here at call time,
// so a value saved before the check existed is refused — and is called through SsrfGuard's connect-time handler. The
// operator's bootstrap endpoint (LlmOptions, from env / user-secrets) is trusted and uses the default transport.
// The platform's own Azure key only ever goes to that bootstrap endpoint: a Settings endpoint runs on the tenant's keys
// alone (KeysFor / EnsureKeyMayGoTo), so a tenant can't point the platform key at a host it controls.

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Security;
using Azure.AI.OpenAI;

namespace AgentOs.Modules.Llm;

internal static class AzureEndpoints
{
    /// <summary>The endpoint to call; <paramref name="Untrusted"/> when it came from Settings (allowlisted + SSRF-guarded).</summary>
    internal sealed record Resolved(string Endpoint, bool Untrusted);

    // One SSRF-hardened HttpClient for every Settings-configured Azure endpoint (a client per call would churn sockets).
    private static readonly Lazy<HttpClient> Hardened = new(() => new HttpClient(SsrfGuard.CreateHardenedHandler(), disposeHandler: true)
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,   // the SDK pipeline applies its own network timeout
    });

    /// <summary>The Settings value when set (must be an Azure OpenAI host — else <see cref="LlmException"/>), else the
    /// bootstrap endpoint.</summary>
    public static Resolved Resolve(string? fromSettings, string? bootstrap)
    {
        if (string.IsNullOrWhiteSpace(fromSettings))
        {
            return new Resolved(bootstrap ?? string.Empty, Untrusted: false);
        }
        if (AzureOpenAiEndpoint.Validate(fromSettings) is { } error)
        {
            throw new LlmException($"The Azure OpenAI endpoint set in Settings was refused: {error}", "AzureOpenAI");
        }
        return new Resolved(fromSettings, Untrusted: true);
    }

    /// <summary>What a workspace is told when it points Azure OpenAI at its own endpoint without its own key.</summary>
    internal const string TenantEndpointNeedsTenantKey =
        "This workspace sends Azure OpenAI calls to its own endpoint but has no Azure OpenAI key of its own. "
        + "Set your own Azure OpenAI key together with your own endpoint, or remove the endpoint to use the platform's.";

    /// <summary>The key pool for a call to <paramref name="endpoint"/>: the tenant's own keys when it set any; otherwise
    /// the platform's keys, but ONLY when the endpoint is the platform's own (bootstrap) endpoint. A Settings endpoint
    /// with no tenant key is refused with <see cref="LlmException"/> before any request — the platform's key is never
    /// sent to a host a tenant chose (GAP-01).</summary>
    public static IReadOnlyList<string> KeysFor(Resolved endpoint, IReadOnlyList<string> tenantKeys, IReadOnlyList<string> platformKeys, string provider)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tenantKeys);
        ArgumentNullException.ThrowIfNull(platformKeys);
        if (tenantKeys.Count > 0)
        {
            return tenantKeys;
        }
        if (endpoint.Untrusted)
        {
            throw new LlmException(TenantEndpointNeedsTenantKey, provider);
        }
        return platformKeys;
    }

    /// <summary>Last line of defence where the SDK client is built: a platform key never goes to a Settings endpoint,
    /// even if the endpoint changed between choosing the key and building the client.</summary>
    public static void EnsureKeyMayGoTo(Resolved endpoint, string apiKey, IReadOnlyCollection<string> platformKeys, string provider)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(platformKeys);
        if (endpoint.Untrusted && platformKeys.Contains(apiKey, StringComparer.Ordinal))
        {
            throw new LlmException(TenantEndpointNeedsTenantKey, provider);
        }
    }

    /// <summary>The SDK client for <paramref name="endpoint"/> — over the SSRF-hardened transport when untrusted.</summary>
    public static AzureOpenAIClient CreateClient(Resolved endpoint, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var options = new AzureOpenAIClientOptions();
        if (endpoint.Untrusted)
        {
            options.Transport = new HttpClientPipelineTransport(Hardened.Value);
        }
        return new AzureOpenAIClient(new Uri(endpoint.Endpoint), new ApiKeyCredential(apiKey), options);
    }
}
