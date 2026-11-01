// GAP-01 — which Azure OpenAI endpoint a call uses, and how it is reached. A value from Settings (IRuntimeOverrides,
// tenant- or platform-admin-set at runtime) must pass the AzureOpenAiEndpoint allowlist — re-checked here at call time,
// so a value saved before the check existed is refused — and is called through SsrfGuard's connect-time handler. The
// operator's bootstrap endpoint (LlmOptions, from env / user-secrets) is trusted and uses the default transport.

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
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
