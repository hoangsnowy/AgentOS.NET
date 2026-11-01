// Provider-neutral bridge from Microsoft.Extensions.AI IChatClient to the app's ILlmClient port.
// Substrate for the SDK-based gateway: each provider produces an IChatClient, wrapped here into
// the cost/latency-shaped LlmResponse the agents expect.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.AI;

namespace AgentOs.Modules.Llm;

/// <summary>Adapts <see cref="IChatClient"/> to <see cref="ILlmClient"/>.</summary>
public sealed class ChatClientLlmClient : ILlmClient
{
    private readonly IChatClient _chat;
    private readonly string? _defaultModel;

    /// <inheritdoc />
    public string Provider { get; }

    /// <summary>Create. <paramref name="defaultModel"/> is used when a request chose no model.</summary>
    public ChatClientLlmClient(IChatClient chat, string provider, string? defaultModel = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        Provider = string.IsNullOrWhiteSpace(provider) ? "ChatClient" : provider;
        _defaultModel = defaultModel;
    }

    /// <inheritdoc />
    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ProviderDefaultModel.Apply(request, _defaultModel);
        request.Validate();

        var messages = LlmMessages.For(request);

        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = ModelCapabilities.TemperatureFor(request.Model, request.Temperature),
            MaxOutputTokens = request.MaxTokens,
        };

        var tenantId = AmbientIdentity.Current?.TenantId;
        var genAiSystem = LlmTelemetry.SystemFor(Provider);
        using var activity = LlmTelemetry.StartChat(genAiSystem, request.Model, tenantId);
        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Net.Http.HttpRequestException ex) { throw OnChatFailed(ex); }
        catch (System.ClientModel.ClientResultException ex) { throw OnChatFailed(ex); }
        catch (System.Text.Json.JsonException ex) { throw OnChatFailed(ex); }
        catch (TimeoutException ex) { throw OnChatFailed(ex); }
        catch (InvalidOperationException ex) { throw OnChatFailed(ex); }
        LlmException OnChatFailed(Exception ex)
        {
            LlmTelemetry.RecordError(activity, ex.Message);
            return new LlmException($"{Provider} chat request failed: {ex.Message}", Provider, innerException: ex);
        }
        stopwatch.Stop();

        var usage = UsageReader.Read(response);
        var cost = CostCalculator.Calculate(request.Model, usage);
        LlmTelemetry.RecordSuccess(activity, genAiSystem, request.Model, response.ModelId ?? request.Model,
            usage.Input, usage.Output, cost, stopwatch.Elapsed.TotalSeconds);

        return new LlmResponse(
            Content: response.Text ?? string.Empty,
            InputTokens: usage.Input,
            OutputTokens: usage.Output,
            CostUsd: cost,
            Latency: stopwatch.Elapsed,
            Model: request.Model,
            Provider: Provider)
        {
            CacheReadTokens = usage.CacheRead,
            CacheWriteTokens = usage.CacheWrite,
            ReasoningTokens = usage.Reasoning,
        };
    }
}
