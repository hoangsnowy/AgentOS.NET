// Factories that produce a Microsoft.Extensions.AI IChatClient from the official SDK clients —
// Azure OpenAI via Azure.AI.OpenAI, Claude via Anthropic.SDK — plus a cross-SDK rate-limit predicate.
// Consumed by PooledChatLlmClient (one IChatClient per API key).

using System;
using System.ClientModel;
using System.Net;
using System.Net.Http;
using Anthropic.SDK;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentOs.Modules.Llm;

internal static class SdkChatClients
{
    /// <summary>Claude via Anthropic.SDK — its Messages endpoint implements <see cref="IChatClient"/>.
    /// Wrapped so the owning <see cref="AnthropicClient"/> is disposed with the chat client (the pool
    /// owns the result for the process lifetime and disposes it on shutdown).</summary>
    public static IChatClient CreateClaude(string apiKey)
    {
        var client = new AnthropicClient(new APIAuthentication(apiKey));
        return new OwningChatClient(client.Messages, client);
    }

    /// <summary>Azure OpenAI via the official SDK, surfaced as <see cref="IChatClient"/>.</summary>
    public static IChatClient CreateAzure(string apiKey, string endpoint, string deployment)
        => new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey))
            .GetChatClient(deployment)
            .AsIChatClient();

    /// <summary>Any OpenAI-compatible <c>/v1/chat/completions</c> server — vLLM, Ollama, LM Studio, llama.cpp,
    /// Azure AI Foundry, a corporate gateway — via the official OpenAI SDK with a custom endpoint (E0.8).
    /// Many self-hosted servers take no key; the SDK still needs a credential, so a blank key becomes a dummy.</summary>
    public static IChatClient CreateOpenAiCompatible(string? apiKey, string endpoint, string model)
        => new OpenAI.OpenAIClient(
                new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) || apiKey == OpenAiCompatibleNoKey ? "not-needed" : apiKey),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();

    /// <summary>Pool placeholder for a keyless OpenAI-compatible server (the pool needs one entry to route).</summary>
    public const string OpenAiCompatibleNoKey = "(no key)";

    /// <summary>True when an exception represents an HTTP 429 / rate-limit / overloaded across the
    /// Azure (ClientResultException) and Anthropic (HttpRequestException) SDK shapes.</summary>
    public static bool IsRateLimited(Exception? ex) => IsRateLimitShaped(ex);

    /// <summary>Classifies an SDK exception so the pooled client can decide whether to fail over to the next
    /// key. <see cref="LlmErrorKind.Transient"/> (429 / 5xx / timeout) and <see cref="LlmErrorKind.Auth"/>
    /// (401/403 — a different key may be valid) warrant a key retry; <see cref="LlmErrorKind.BadRequest"/>
    /// (a malformed request — retrying the same payload just fails again) and
    /// <see cref="LlmErrorKind.Other"/> are non-retryable. Inspects the whole inner-exception chain because
    /// each SDK wraps the HTTP failure differently (ClientResultException / HttpRequestException / message).</summary>
    public static LlmErrorKind Classify(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ClientResultException cre:
                    if (FromStatus(cre.Status) is { } k1) { return k1; }
                    break;
                case HttpRequestException hre when hre.StatusCode is { } sc:
                    if (FromStatus((int)sc) is { } k2) { return k2; }
                    break;
                // A non-cancellation timeout surfaces as TaskCanceledException/TimeoutException; a transport
                // failure as a SocketException. All are transient — another key/attempt may succeed. (Caller
                // cancellation is handled separately, before classification.)
                case TaskCanceledException:
                case TimeoutException:
                case System.Net.Sockets.SocketException:
                    return LlmErrorKind.Transient;
            }

            var msg = e.Message;
            if (string.IsNullOrEmpty(msg))
            {
                continue;
            }
            if (msg.Contains("429", StringComparison.Ordinal)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            {
                return LlmErrorKind.Transient;
            }
            if (msg.Contains("401", StringComparison.Ordinal)
                || msg.Contains("403", StringComparison.Ordinal)
                || msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("invalid x-api-key", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                return LlmErrorKind.Auth;
            }
            if (msg.Contains("service unavailable", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("server error", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return LlmErrorKind.Transient;
            }
        }
        return LlmErrorKind.Other;
    }

    // HTTP status → error kind. Returns null when the status carries no failover signal (let the chain /
    // message heuristics decide).
    private static LlmErrorKind? FromStatus(int status) => status switch
    {
        408 or 429 => LlmErrorKind.Transient,
        >= 500 and <= 599 => LlmErrorKind.Transient,
        401 or 403 => LlmErrorKind.Auth,
        400 or 404 or 409 or 422 => LlmErrorKind.BadRequest,
        _ => null,
    };

    // Narrow check kept only so the legacy IsRateLimited helper stays 429-specific (not any transient).
    private static bool IsRateLimitShaped(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException { Status: 429 }
                or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }
            var msg = e.Message;
            if (!string.IsNullOrEmpty(msg)
                && (msg.Contains("429", StringComparison.Ordinal)
                    || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>How an LLM SDK error should be treated by the pooled client's key-failover loop.</summary>
public enum LlmErrorKind
{
    /// <summary>429 / 5xx / timeout / transport — retry on the next key.</summary>
    Transient,

    /// <summary>401/403 — this key is bad; a different key in the pool may be valid, so retry.</summary>
    Auth,

    /// <summary>400/404/409/422 — the request itself is malformed; retrying the same payload won't help.</summary>
    BadRequest,

    /// <summary>Unclassified — treated as non-retryable.</summary>
    Other,
}
