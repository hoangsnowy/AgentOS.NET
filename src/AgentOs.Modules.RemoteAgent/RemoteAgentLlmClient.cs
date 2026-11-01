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

    /// <summary>The model a response reports when the request chose none and the member's CLI ran its own default
    /// (metrics then show this name rather than an empty model).</summary>
    public const string ComputerDefaultModel = "computer-default";

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
        // No model chosen is valid here: the member's CLI then runs its own default model (the runner adds no flag).
        request.ValidateAllowingNoModel();

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
        // Ticket files (ADR-0015): the manifest travels with the request; the bytes wait in the broker until the runner it
        // goes to fetches them, before its CLI starts.
        var files = request.Files is { Count: > 0 } attached ? attached : null;
        // Files sent back (Design gate, ADR-0016): the runner learns only the caps; where they are stored stays here.
        // The server-wide budget for files in flight may be spent: then this run takes no screenshots back (the design check
        // will say none came) rather than queueing unbounded memory.
        var uploads = request.Uploads;
        if (uploads is not null && !_broker.ExpectUploads(id, uploads.Limits))
        {
            _logger.LogWarning("[RemoteAgent] request {Id}: the server is already receiving as many files as it takes at once — no screenshots this run.", id);
            uploads = null;
        }
        var execRequest = new RemoteExecRequest(id, request.SystemPrompt, request.UserPrompt, request.Model, request.Cli, request.Autonomy,
            files is null ? null : RunFileEvidence.Manifest(files), uploads?.Limits);
        if (files is not null) { _broker.StageFiles(id, files); }
        IReadOnlyList<RunUpload> sentBack = [];
        // Governance: the tool calls the CLI makes on that computer are reported back and recorded under this run.
        var sessionId = amb?.SessionId?.ToString("D");
        _broker.TrackRun(id, new RemoteRunContext(target.TenantId, sessionId, request.AgentRole, request.Autonomy));
        var dispatchedAt = DateTimeOffset.UtcNow;

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
                if (uploads is not null) { sentBack = _broker.TakeUploads(id); }
                if (files is not null)
                {
                    await RunFileEvidence.RecordAsync(_services, target.TenantId, id, files, _broker.UnstageFiles(id),
                        "the run's working folder on the member's computer", null, dispatchedAt).ConfigureAwait(false);
                }
                PublishToolCallTotals(target.TenantId, amb?.SessionId, _broker.ToolCallTotals(id));
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

        // The member's Claude/Codex plan said "no more for now": a typed signal, not a failure of the work — the
        // member's wait is recorded so Autopilot holds off until the reset instead of burning its tries.
        if (PlanLimitDetector.FromReply(result.Ok, result.Content, result.Error, request.Cli, DateTimeOffset.UtcNow) is { } limit)
        {
            LlmTelemetry.RecordError(activity, "plan limit");
            _services.GetService<IPlanLimits>()?.Report(target.TenantId, target.MemberUserId, limit);
            _logger.LogWarning("[RemoteAgent] request {Id}: the member's {Plan} plan hit its limit (resets {Reset}).", id, limit.Plan, limit.ResetText ?? "unknown");
            throw new PlanLimitException(limit, Provider);
        }

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

        var stored = uploads is null || sentBack.Count == 0 ? [] : await StoreUploadsAsync(target.TenantId, uploads, sentBack, id).ConfigureAwait(false);

        return new LlmResponse(
            Content: result.Content,
            InputTokens: inTok,
            OutputTokens: outTok,
            CostUsd: 0m,
            Latency: stopwatch.Elapsed,
            Model: string.IsNullOrWhiteSpace(request.Model) ? ComputerDefaultModel : request.Model,
            Provider: Provider)
        {
            CacheReadTokens = real ? result.CacheReadTokens ?? 0 : 0,
            CacheWriteTokens = real ? result.CacheWriteTokens ?? 0 : 0,
            Uploaded = stored,
        };
    }

    // The files the runner sent back, into the tenant's file store under the request's scope (never into git). Each was
    // already checked piece by piece (RunUploadRules); a store failure loses the screenshots, never the run's answer.
    // Each file under the target's folder (one per repo of a multi-repo task), so two repos' "home-390-light.png" never
    // replace each other; a path that would get too long is skipped (logged), never truncated into another name.
    private async Task<IReadOnlyList<AgentOs.Domain.Files.StoredFileInfo>> StoreUploadsAsync(string tenantId, RunUploadTarget target,
        IReadOnlyList<RunUpload> uploads, string requestId)
    {
        var stored = new List<AgentOs.Domain.Files.StoredFileInfo>(uploads.Count);
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService<AgentOs.Domain.Files.IScopedFileStore>() is not { } store) { return stored; }
            foreach (var u in uploads)
            {
                if (AgentOs.Domain.Files.TicketFilePath.Normalize(target.PathPrefix + u.Path) is not { } path)
                {
                    _logger.LogWarning("[RemoteAgent] request {Id}: a file sent back has a path too long to store; skipped.", requestId);
                    continue;
                }
                var info = new AgentOs.Domain.Files.StoredFileInfo(Guid.NewGuid(), tenantId, target.ScopeKey, path, u.Content.LongLength,
                    AgentOs.Domain.Files.RunFile.Hash(u.Content), u.ContentType, "Testing agent (member's computer)", DateTimeOffset.UtcNow);
                stored.Add(await store.PutAsync(info, u.Content).ConfigureAwait(false));
            }
            _logger.LogInformation("[RemoteAgent] request {Id}: stored {Count} file(s) sent back by the runner.", requestId, stored.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.Data.Common.DbException or TimeoutException)
        {
            _logger.LogWarning(ex, "[RemoteAgent] request {Id}: couldn't store the files the runner sent back.", requestId);
        }
        return stored;
    }

    // One line on the task's feed: what the agent did on the computer, and where every call is listed.
    private void PublishToolCallTotals(string tenantId, Guid? sessionId, (int ToolCalls, int Denials) totals)
    {
        if (sessionId is not { } id || totals.ToolCalls == 0
            || _services.GetService<AgentOs.Domain.Sessions.ISessionRunFeed>() is not { } feed)
        {
            return;
        }
        var refused = totals.Denials == 0 ? string.Empty : $" ({totals.Denials} refused by its permissions)";
        feed.Publish(new AgentOs.Domain.Sessions.SessionRunEvent(tenantId, id, AgentOs.Domain.Sessions.SessionRunEventKind.Step,
            $"The agent used {totals.ToolCalls} tool call(s) on the computer{refused} — each one is in Settings › Activity log › Tool calls.",
            DateTimeOffset.UtcNow));
    }

    // Rough token estimate for text the CLI produced no usage numbers for: ~4 characters per token, the
    // standard English heuristic. Only ever used for RemoteAgent, whose actual server cost is $0.
    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)System.Math.Ceiling(text.Length / 4.0);
}
