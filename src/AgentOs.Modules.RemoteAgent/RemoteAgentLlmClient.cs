// LLM provider — instead of calling an LLM API (and spending tokens), dispatches the request to a
// connected remote agent via IRemoteAgentBroker and wraps the reply as an LlmResponse with zero
// cost. Registered as keyed ILlmClient "RemoteAgent" so LlmClientFactory resolves it by name.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>ILlmClient that routes a request to the current member's paired remote runner. Zero token
/// usage / cost. The target is the current request's tenant + member (ITenantContext), so the work runs
/// on that member's own machine and nobody else's.</summary>
public sealed class RemoteAgentLlmClient : ILlmClient
{
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(120);

    private readonly IRemoteAgentBroker _broker;
    // Root provider for resolving the request-scoped ITenantContext PER CALL, never at construction.
    // Capturing it in the constructor made resolving this client from the root provider throw
    // "Cannot resolve 'ILlmClient' from root provider because it requires scoped service
    // 'ITenantContext'" on hosts whose tenant context is scoped (Keycloak's HttpTenantContext) — the
    // Settings "Test connection" probe hit exactly that. Same shape as PooledChatLlmClient.
    private readonly IServiceProvider _services;
    private readonly ILogger<RemoteAgentLlmClient> _logger;

    /// <inheritdoc />
    public string Provider => "RemoteAgent";

    public RemoteAgentLlmClient(IRemoteAgentBroker broker, IServiceProvider services, ILogger<RemoteAgentLlmClient> logger)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // The tenant + member to target when AmbientIdentity does not supply them. Resolved inside a fresh
    // scope off the root provider, and read to plain strings before the scope is disposed.
    private (string? TenantId, string? UserId) ResolveFallbackIdentity()
    {
        using var scope = _services.CreateScope();
        var tenant = scope.ServiceProvider.GetService<ITenantContext>();
        return (tenant?.TenantId, tenant?.UserId);
    }

    /// <inheritdoc />
    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // Background session work (a Blazor circuit's Task.Run) has no HttpContext, so ITenantContext is
        // blank there; the session seeds AmbientIdentity so the dispatch targets the member's own runner.
        var amb = AmbientIdentity.Current;
        var fallback = amb is null || amb.UserId is null ? ResolveFallbackIdentity() : default;
        var target = new RunnerTarget(
            amb?.TenantId ?? fallback.TenantId ?? ITenantContext.DefaultTenantId,
            amb?.UserId ?? fallback.UserId ?? string.Empty);

        var genAiSystem = LlmTelemetry.SystemFor(Provider);
        using var activity = LlmTelemetry.StartChat(genAiSystem, request.Model, target.TenantId);

        if (!_broker.HasRunnerFor(target))
        {
            LlmTelemetry.RecordError(activity, "no remote runner connected");
            throw new LlmException(
                "No remote dev runner connected for you. Register a runner (POST /runners), start the AgentOS.NET "
                + "remote agent on your dev machine with that runner id + token, or pick a different provider.",
                Provider);
        }

        var id = Guid.NewGuid().ToString("N");
        var execRequest = new RemoteExecRequest(id, request.SystemPrompt, request.UserPrompt, request.Model, request.Cli, request.Autonomy);

        // A single full-prompt dispatch (M3) caps at 120s, but an agentic issue-work run on the local
        // CLI (clone → build → test → push) needs minutes — the caller raises it via LlmRequest.Timeout.
        var timeout = request.Timeout ?? DispatchTimeout;

        var stopwatch = Stopwatch.StartNew();
        RemoteExecResult result;
        try
        {
            // Live progress for this request only, for as long as it runs.
            void OnProgress(RemoteProgress p)
            {
                if (p.RequestId == id) { request.Progress?.Report(p.Step); }
            }
            if (request.Progress is not null) { _broker.Progressed += OnProgress; }
            try
            {
                result = await _broker.DispatchAsync(execRequest, target, timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (request.Progress is not null) { _broker.Progressed -= OnProgress; }
            }
        }
        catch (TimeoutException ex)
        {
            LlmTelemetry.RecordError(activity, ex.Message);
            throw new LlmException($"Remote agent timed out after {timeout.TotalSeconds:0}s.", Provider, innerException: ex);
        }
        catch (InvalidOperationException ex)
        {
            LlmTelemetry.RecordError(activity, ex.Message);
            throw new LlmException(ex.Message, Provider, innerException: ex);
        }
        stopwatch.Stop();

        if (!result.Ok)
        {
            LlmTelemetry.RecordError(activity, result.Error ?? "remote agent failure");
            throw new LlmException(result.Error ?? "Remote agent reported a failure.", Provider);
        }

        // The runner spends the member's own flat subscription, so the SERVER cost is genuinely $0 — that
        // is the whole point of this provider and CostUsd stays 0m. But "0 tokens" is unhelpful on screen:
        // the CLI reports no usage, so estimate it from the text we DID send and receive (~4 chars/token,
        // the standard rough heuristic). These counts are honest estimates — they let the UI show a real
        // token figure and derive a "what this would have cost on a metered API" number, without ever
        // claiming a billed spend. The estimate is deliberately NOT folded into CostUsd.
        // A current runner sends the CLI's real usage (the whole agentic run: every tool turn, cache reads included);
        // only an older runner, or a CLI that reports none, falls back to the estimate.
        var real = result.InputTokens is not null && result.OutputTokens is not null;
        var inTok = real ? result.InputTokens!.Value : EstimateTokens(request.SystemPrompt) + EstimateTokens(request.UserPrompt);
        var outTok = real ? result.OutputTokens!.Value : EstimateTokens(result.Content);
        LlmTelemetry.RecordSuccess(activity, genAiSystem, request.Model, request.Model, inTok, outTok, 0m, stopwatch.Elapsed.TotalSeconds);
        _logger.LogInformation("[RemoteAgent] request {Id} handled by a remote agent ({Count} connected); {In}->{Out} tokens ({Kind}), 0 API cost.",
            id, _broker.AgentCount, inTok, outTok, real ? "reported by the CLI" : "estimated");

        return new LlmResponse(
            Content: result.Content,
            InputTokens: inTok,
            OutputTokens: outTok,
            CostUsd: 0m,
            Latency: stopwatch.Elapsed,
            Model: request.Model,
            Provider: Provider)
        {
            CacheReadTokens = real ? result.CacheReadTokens ?? 0 : 0,
            CacheWriteTokens = real ? result.CacheWriteTokens ?? 0 : 0,
        };
    }

    // Rough token estimate for text the CLI produced no usage numbers for: ~4 characters per token, the
    // standard English heuristic. Only ever used for RemoteAgent, whose actual server cost is $0.
    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)System.Math.Ceiling(text.Length / 4.0);
}
