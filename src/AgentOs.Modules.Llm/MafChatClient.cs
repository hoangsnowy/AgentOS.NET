// ILlmClient backed by Azure OpenAI SDK through Microsoft.Extensions.AI IChatClient (the substrate
// Microsoft Agent Framework builds on). Selected via provider key "MAF" or Llm:ForceProvider=MAF.

using System;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Logging;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Llm;

/// <summary>Azure OpenAI client via the official SDK + Microsoft.Extensions.AI <see cref="IChatClient"/>.</summary>
public sealed class MafChatClient : ILlmClient
{
    // One AzureOpenAIClient per (endpoint, key) for the process lifetime — the SDK client is thread-safe
    // and reusable (like HttpClient), so building one per request churns sockets/handlers. Mirrors the
    // one-client-per-key pooling in PooledChatLlmClient. Static so it survives the keyed-transient
    // registration of MafChatClient.
    private static readonly ConcurrentDictionary<string, AzureOpenAIClient> ClientCache = new(StringComparer.Ordinal);

    private readonly AzureOpenAiOptions _options;
    private readonly IRuntimeOverrides _overrides;
    private readonly ILogger<MafChatClient> _logger;
    private readonly ISettings? _settings;

    /// <inheritdoc />
    public string Provider => "MAF";

    public MafChatClient(IOptions<LlmOptions> options, IRuntimeOverrides overrides, ILogger<MafChatClient> logger, ISettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value?.AzureOpenAi ?? new AzureOpenAiOptions();
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings;
    }

    // Llm:AzureOpenAi:PricingModel, live from Settings when the registry is wired; else the bound option.
    private async ValueTask<string?> PricingModelAsync(CancellationToken ct) =>
        _settings is null
            ? _options.PricingModel
            : await _settings.GetStringAsync(LlmSettings.AzurePricingModel, AmbientIdentity.Current?.TenantId, _options.PricingModel, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // No model chosen in the agent's profile → the configured Azure deployment (live setting, else the option).
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            request = ProviderDefaultModel.Apply(request,
                (_settings is null ? null : LiveSettings.Read(_settings, LlmSettings.AzureModel, AmbientIdentity.Current?.TenantId)) ?? _options.Model);
        }
        request.Validate();

        // Resolve credentials with the SAME tenant-override-first precedence as the AzureOpenAI pool — a tenant
        // who set only their Settings key (no platform Llm:AzureOpenAi:ApiKey) must still be able to force MAF,
        // and per-tenant isolation requires using THEIR key/endpoint, not the shared platform one. Resolved
        // per-call (this is keyed-transient): IRuntimeOverrides reads the ambient tenant, never at ctor time.
        var configuredPricingModel = await PricingModelAsync(cancellationToken).ConfigureAwait(false);
        var tenantKeys = await _overrides.GetAzureApiKeysAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = tenantKeys.Count > 0 ? tenantKeys[0] : _options.ApiKey;
        var endpoint = !string.IsNullOrWhiteSpace(_overrides.AzureEndpoint) ? _overrides.AzureEndpoint! : _options.Endpoint;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            throw new LlmException(
                "Azure OpenAI not configured for the MAF client. Set your Azure key + endpoint in Settings "
                + "(per-tenant) or 'Llm:AzureOpenAi:ApiKey' + 'Llm:AzureOpenAi:Endpoint' (user-secrets or env).",
                Provider);
        }

        var deployment = string.IsNullOrWhiteSpace(_options.Model) ? request.Model : _options.Model;
        var azure = GetOrCreateClient(endpoint, apiKey);
        IChatClient chat = azure.GetChatClient(deployment).AsIChatClient();

        var messages = LlmMessages.For(request);

        var options = new ChatOptions
        {
            ModelId = deployment,
            // Capability is keyed on the canonical (pricing) model, not the arbitrary deployment alias.
            Temperature = ModelCapabilities.TemperatureFor(ResolvePricingModel(configuredPricingModel, deployment), request.Temperature),
            MaxOutputTokens = request.MaxTokens,
        };

        var tenantId = AmbientIdentity.Current?.TenantId;
        var genAiSystem = LlmTelemetry.SystemFor(Provider);
        using var activity = LlmTelemetry.StartChat(genAiSystem, deployment, tenantId);
        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException ex) { OnChatFailed(ex); throw; }
        catch (ClientResultException ex) { OnChatFailed(ex); throw; }
        catch (System.Text.Json.JsonException ex) { OnChatFailed(ex); throw; }
        catch (TimeoutException ex) { OnChatFailed(ex); throw; }
        catch (InvalidOperationException ex) { OnChatFailed(ex); throw; }
        void OnChatFailed(Exception ex) => LlmTelemetry.RecordError(
            activity, ex.Message, genAiSystem, deployment, SdkChatClients.ErrorTypeOf(ex), request.AgentRole);
        stopwatch.Stop();

        var usage = UsageReader.Read(response);
        var inputTokens = usage.Input;
        var outputTokens = usage.Output;

        // ONE authoritative model id for telemetry/log/LlmResponse: `deployment` (the alias actually called).
        // Pricing is a SEPARATE id: deployment names are arbitrary aliases (e.g. "gpt41-prod") that aren't
        // priced prefixes, so PricingModel maps the alias to a canonical price-table prefix; fall back to the
        // deployment when unset. Cost AND the UNPRICED warning both read this same id — no four-way disagreement.
        var pricingModel = ResolvePricingModel(configuredPricingModel, deployment);
        var cost = CostCalculator.Calculate(pricingModel, usage);
        if (!CostCalculator.IsKnown(pricingModel))
        {
            // Unpriced model = cost is a pessimistic ceiling estimate until the model is added to the table.
            _logger.LogWarning(
                "Cost: model {Model} is not in the price table — cost ESTIMATED at the fail-closed ceiling rate. "
                + "Update CostCalculator or set 'Llm:AzureOpenAi:PricingModel' to a canonical price-table id.",
                LogSafe.Scrub(pricingModel));
        }
        LlmTelemetry.RecordSuccess(activity, genAiSystem, deployment, response.ModelId ?? deployment,
            inputTokens, outputTokens, cost, stopwatch.Elapsed.TotalSeconds,
            cacheReadTokens: usage.CacheRead, cacheWriteTokens: usage.CacheWrite,
            reasoningTokens: usage.Reasoning, agent: request.AgentRole);
        _logger.LogInformation(
            "LlmCallCompleted {Provider} {Model} {InTok} {OutTok} {CostUsd} {Ms} {Tenant}",
            Provider, LogSafe.Scrub(deployment), inputTokens, outputTokens, cost, stopwatch.Elapsed.TotalMilliseconds, LogSafe.Scrub(tenantId ?? ""));

        return new LlmResponse(
            Content: response.Text ?? string.Empty,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: cost,
            Latency: stopwatch.Elapsed,
            Model: deployment,
            Provider: Provider)
        {
            CacheReadTokens = usage.CacheRead,
            CacheWriteTokens = usage.CacheWrite,
            ReasoningTokens = usage.Reasoning,
        };
    }

    // Maps a deployment alias to the canonical id fed to CostCalculator. Deployment names are arbitrary
    // aliases (e.g. "gpt41-prod") that aren't priced prefixes; PricingModel pins the alias to a price-table
    // prefix. Falls back to the deployment id when unset. Internal for the pricing test (InternalsVisibleTo
    // "AgentOs.Tests").
    internal static string ResolvePricingModel(string? pricingModel, string deployment) =>
        string.IsNullOrWhiteSpace(pricingModel) ? deployment : pricingModel;

    // Returns a cached AzureOpenAIClient for the (endpoint, key) pair, building one on first use. Internal
    // for the reuse test (InternalsVisibleTo "AgentOs.Tests"); callers must validate endpoint/key first.
    internal static AzureOpenAIClient GetOrCreateClient(string endpoint, string apiKey) =>
        ClientCache.GetOrAdd(
            $"{endpoint}|{apiKey}",
            _ => new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey)));
}
