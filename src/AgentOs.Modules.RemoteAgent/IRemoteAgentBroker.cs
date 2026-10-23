// "Remote dev-IDE agent" runtime — server-side dispatch seam. The server hands a codegen request to a
// connected remote runner (running on the member's own machine) and awaits the result, instead of
// paying for an LLM API call. Transport-agnostic broker: a transport (the SignalR hub) registers
// connected runners with their identity, the broker resolves a request to ONE target runner's
// connection, raises Dispatched, and Complete resolves the pending task with the runner's reply.
//
// M3 — dispatch is now TARGETED, not broadcast. Each connection carries a RunnerConnection (tenant +
// owning member); a RunnerTarget (tenant + member) resolves to that member's runner within the tenant.
// A request for tenant A can never resolve a tenant B connection, which closes the old Clients.All leak.
//
// M4 — bidirectional tool-call protocol: the server runs the agentic LLM loop and dispatches
// individual tool calls (RunnerToolCall) to the runner for local execution; the runner replies with
// RunnerToolResult. The server never sends the full LLM reasoning to the runner — "server thinks,
// runner does". Every tool call still passes through IToolGateway on the server (policy + evidence)
// before being dispatched.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>A codegen task sent to a remote runner. <paramref name="Cli"/> selects which subscription
/// CLI the runner invokes (e.g. "claude", "codex"); null = the runner's configured default.
/// <paramref name="Autonomy"/> is what the CLI may do on that machine (null = answer only; "scoped" = work in a
/// fresh per-run directory) — see <c>LlmRequest.Autonomy</c>.</summary>
public sealed record RemoteExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null, string? Autonomy = null);

/// <summary>A remote runner's reply for a <see cref="RemoteExecRequest"/>.</summary>
public sealed record RemoteExecResult(string Id, bool Ok, string Content, string? Error)
{
    /// <summary>Real input tokens the runner's CLI reported (cache reads/writes included); null = not reported
    /// (an older runner, or a CLI that reports no usage) and the server estimates.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Real output tokens the CLI reported; null = not reported.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Input tokens served from the prompt cache (a subset of <see cref="InputTokens"/>).</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache (a subset of <see cref="InputTokens"/>).</summary>
    public int? CacheWriteTokens { get; init; }
}

/// <summary>Identity of a connected runner, established by the hub's pairing handshake.</summary>
public sealed record RunnerConnection(Guid RunnerId, string TenantId, string OwnerUserId);

/// <summary>Routing key for a dispatch: the tenant and (optionally) the member whose runner should run it.
/// An empty <see cref="MemberUserId"/> (operator mode) matches any runner in the tenant.</summary>
public sealed record RunnerTarget(string TenantId, string MemberUserId);

/// <summary>A request resolved to a specific runner connection, ready for the transport to push.</summary>
public sealed record RemoteDispatch(RemoteExecRequest Request, string ConnectionId);

/// <summary>A dispatched request the server gave up on (the caller cancelled it — Stop — or it timed out); the
/// transport tells that runner to kill the work instead of letting it run on unseen.</summary>
public sealed record RemoteCancel(string RequestId, string ConnectionId);

/// <summary>A running request's current plain step, reported by its runner (a TaskSteps key).</summary>
public sealed record RemoteProgress(string RequestId, string Step);

// ── M4 bidirectional tool protocol ──────────────────────────────────────────────────────────────

/// <summary>A single tool-execution request sent from the server to the runner. The server's LLM
/// loop decides WHAT to run; the runner provides the local execution environment.</summary>
/// <param name="RequestId">Correlates this call back to the originating pipeline/session run.</param>
/// <param name="ToolCallId">Unique per-call key; the runner echoes it in <see cref="RunnerToolResult"/>.</param>
/// <param name="ToolName">The primitive to execute (e.g. <c>"shell"</c>).</param>
/// <param name="JsonInput">Tool-specific JSON payload (e.g. <c>{"command":"dotnet build"}</c>).</param>
public sealed record RunnerToolCall(string RequestId, string ToolCallId, string ToolName, string JsonInput);

/// <summary>The runner's reply for a <see cref="RunnerToolCall"/>.</summary>
/// <param name="RequestId">Echoed from the originating call for correlation.</param>
/// <param name="ToolCallId">Echoed to match the TCS in the broker.</param>
/// <param name="Ok">True when the tool exited with success.</param>
/// <param name="JsonOutput">Stringified result (stdout/output). May be empty on error.</param>
/// <param name="Error">Human-readable error text when <see cref="Ok"/> is false.</param>
public sealed record RunnerToolResult(string RequestId, string ToolCallId, bool Ok, string JsonOutput, string? Error);

/// <summary>A tool call resolved to a specific runner connection, ready for the transport to push.</summary>
public sealed record RunnerToolDispatch(RunnerToolCall Call, string ConnectionId);

// ── "Try the app" (local preview) ──────────────────────────────────────────────────────────────

/// <summary>Run a project's preview on a runner: the latest <paramref name="Branch"/> of <paramref name="Owner"/>/<paramref name="Repo"/>,
/// started with <paramref name="Command"/> (the recipe the person was shown) until <paramref name="Url"/> answers.
/// The runner re-reads the recipe from its own checkout and refuses when it differs.</summary>
public sealed record PreviewStart(string PreviewId, string Owner, string Repo, string Branch, string Command, string Url, int ReadyTimeoutSeconds);

/// <summary>Where a preview stands, as its runner reports it.</summary>
public static class PreviewStates
{
    public const string Preparing = "Preparing";
    public const string Starting = "Starting";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string Stopped = "Stopped";

    /// <summary>True while the app is (being) run — the computer's ports are taken.</summary>
    public static bool IsActive(string state) => state is Preparing or Starting or Ready;
}

/// <summary>A runner's report for a preview it runs.</summary>
public sealed record PreviewStatus(string PreviewId, string State, string? Message);

/// <summary>A preview start or stop, resolved to the runner connection that runs it.</summary>
public sealed record PreviewCommand(string ConnectionId, PreviewStart? Start, string? StopPreviewId);

/// <summary>Seam for "Try the app": start / stop a preview on a member's paired computer and hear back from it.</summary>
public interface IRunnerPreviewBroker
{
    /// <summary>Raised to push a start or stop to a runner (the transport sends it).</summary>
    event Action<PreviewCommand>? PreviewCommanded;

    /// <summary>Raised when a runner reports on a preview it was asked to run (or its computer disconnected).</summary>
    event Action<PreviewStatus>? PreviewReported;

    /// <summary>Send <paramref name="start"/> to the runner matching <paramref name="target"/>; false when none is connected.</summary>
    bool StartPreview(PreviewStart start, RunnerTarget target);

    /// <summary>Ask the runner of a preview to stop it. Unknown id = nothing to stop.</summary>
    void StopPreview(string previewId);

    /// <summary>Called by the hub: a report counts only from the connection the preview was sent to.</summary>
    void ReportPreview(string connectionId, string previewId, string state, string? message);
}

// ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Dispatch seam between the server and connected remote runners.</summary>
public interface IRemoteAgentBroker
{
    /// <summary>True when at least one runner is connected (any tenant). For health/logging only.</summary>
    bool HasAgent { get; }

    int AgentCount { get; }

    /// <summary>True when a runner matching <paramref name="target"/> is currently connected.</summary>
    bool HasRunnerFor(RunnerTarget target);

    /// <summary>True when this specific paired runner has a live connection right now (Board › Computers status).</summary>
    bool IsConnected(Guid runnerId) => false;

    // ── M3: full-prompt dispatch ──

    event Action<RemoteDispatch>? Dispatched;

    /// <summary>Raised when a dispatched request is abandoned (cancelled by its caller, or timed out).</summary>
    event Action<RemoteCancel>? Cancelled;

    /// <summary>Raised when a runner reports the step a pending request is on.</summary>
    event Action<RemoteProgress>? Progressed;

    /// <summary>Called by the hub when a runner sends <c>ReportProgress</c>; ignored for requests no one waits for.</summary>
    void ReportProgress(string requestId, string stepKey);

    Task<RemoteExecResult> DispatchAsync(RemoteExecRequest request, RunnerTarget target, TimeSpan timeout, CancellationToken cancellationToken = default);

    IDisposable RegisterRunner(string connectionId, RunnerConnection runner);

    void Complete(RemoteExecResult result);

    // ── M4: bidirectional tool-call protocol ──

    /// <summary>Raised when a tool call has been resolved to a connection and is ready to push.</summary>
    event Action<RunnerToolDispatch>? ToolCallDispatched;

    /// <summary>Dispatch a single tool-execution request to the runner matched by <paramref name="target"/>.</summary>
    Task<RunnerToolResult> DispatchToolCallAsync(
        RunnerToolCall toolCall,
        RunnerTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Called by the hub when the runner sends a <c>CompleteToolCall</c> message.</summary>
    void CompleteToolCall(RunnerToolResult result);
}

/// <summary>In-process broker. Singleton; thread-safe.</summary>
public sealed class InProcessRemoteAgentBroker : IRemoteAgentBroker, IRunnerPreviewBroker
{
    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteExecResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunnerToolResult>> _pendingToolCalls = new(StringComparer.Ordinal);
    // Preview id → the connection running it (a report from any other connection is ignored).
    private readonly ConcurrentDictionary<string, string> _previews = new(StringComparer.Ordinal);

    public event Action<PreviewCommand>? PreviewCommanded;
    public event Action<PreviewStatus>? PreviewReported;

    public bool StartPreview(PreviewStart start, RunnerTarget target)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(target);
        if (!TryResolveConnection(target, out var connectionId)) { return false; }
        _previews[start.PreviewId] = connectionId;
        PreviewCommanded?.Invoke(new PreviewCommand(connectionId, start, null));
        return true;
    }

    public void StopPreview(string previewId)
    {
        if (_previews.TryGetValue(previewId, out var connectionId))
        {
            PreviewCommanded?.Invoke(new PreviewCommand(connectionId, null, previewId));
        }
    }

    public void ReportPreview(string connectionId, string previewId, string state, string? message)
    {
        if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out var owner)
            || !string.Equals(owner, connectionId, StringComparison.Ordinal))
        {
            return;
        }
        if (state is not (PreviewStates.Preparing or PreviewStates.Starting or PreviewStates.Ready or PreviewStates.Failed or PreviewStates.Stopped))
        {
            return;
        }
        if (!PreviewStates.IsActive(state)) { _previews.TryRemove(previewId, out _); }
        var text = message is { Length: > 4000 } ? message[^4000..] : message;
        PreviewReported?.Invoke(new PreviewStatus(previewId, state, text));
    }

    public bool HasAgent => !_connections.IsEmpty;

    public int AgentCount => _connections.Count;

    public event Action<RemoteDispatch>? Dispatched;
    public event Action<RemoteCancel>? Cancelled;
    public event Action<RemoteProgress>? Progressed;

    public void ReportProgress(string requestId, string stepKey)
    {
        // Only for a request someone is waiting on: an unknown id is a late or forged report.
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(stepKey) || stepKey.Length > 64 || !_pending.ContainsKey(requestId)) { return; }
        Progressed?.Invoke(new RemoteProgress(requestId, stepKey));
    }
    public event Action<RunnerToolDispatch>? ToolCallDispatched;

    public bool IsConnected(Guid runnerId) => _connections.Values.Any(c => c.RunnerId == runnerId);

    public bool HasRunnerFor(RunnerTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return TryResolveConnection(target, out _);
    }

    public async Task<RemoteExecResult> DispatchAsync(RemoteExecRequest request, RunnerTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        if (!TryResolveConnection(target, out var connectionId))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"No paired runner connected for member '{target.MemberUserId}' in tenant '{target.TenantId}'."));
        }

        var tcs = new TaskCompletionSource<RemoteExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
            {
                // The runner is still working on it — tell it to stop (Stop button, or nobody is waiting any more).
                Cancelled?.Invoke(new RemoteCancel(request.Id, connectionId));
                if (cancellationToken.IsCancellationRequested)
                {
                    pending.TrySetCanceled(cancellationToken);
                }
                else
                {
                    pending.TrySetException(new TimeoutException($"Remote runner did not respond within {timeout.TotalSeconds:0}s."));
                }
            }
        });

        try
        {
            Dispatched?.Invoke(new RemoteDispatch(request, connectionId));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public IDisposable RegisterRunner(string connectionId, RunnerConnection runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(runner);
        _connections[connectionId] = runner;
        return new Registration(this, connectionId);
    }

    public void Complete(RemoteExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove(result.Id, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }

    public async Task<RunnerToolResult> DispatchToolCallAsync(
        RunnerToolCall toolCall,
        RunnerTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(target);

        if (!TryResolveConnection(target, out var connectionId))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"No paired runner connected for member '{target.MemberUserId}' in tenant '{target.TenantId}'."));
        }

        var tcs = new TaskCompletionSource<RunnerToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[toolCall.ToolCallId] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pendingToolCalls.TryRemove(toolCall.ToolCallId, out var pending))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    pending.TrySetCanceled(cancellationToken);
                }
                else
                {
                    pending.TrySetException(
                        new TimeoutException($"Runner tool call '{toolCall.ToolName}' timed out after {timeout.TotalSeconds:0}s."));
                }
            }
        });

        try
        {
            ToolCallDispatched?.Invoke(new RunnerToolDispatch(toolCall, connectionId));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingToolCalls.TryRemove(toolCall.ToolCallId, out _);
        }
    }

    public void CompleteToolCall(RunnerToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pendingToolCalls.TryRemove(result.ToolCallId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }

    private bool TryResolveConnection(RunnerTarget target, out string connectionId)
    {
        foreach (var kvp in _connections)
        {
            var conn = kvp.Value;
            if (!string.Equals(conn.TenantId, target.TenantId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(target.MemberUserId)
                && !string.Equals(conn.OwnerUserId, target.MemberUserId, StringComparison.Ordinal))
            {
                continue;
            }
            connectionId = kvp.Key;
            return true;
        }
        connectionId = string.Empty;
        return false;
    }

    private void Unregister(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        // The runner went away: whatever it ran is gone with it (it stops its previews when it exits).
        foreach (var (previewId, owner) in _previews)
        {
            if (string.Equals(owner, connectionId, StringComparison.Ordinal) && _previews.TryRemove(previewId, out _))
            {
                PreviewReported?.Invoke(new PreviewStatus(previewId, PreviewStates.Stopped, "The computer disconnected."));
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly InProcessRemoteAgentBroker _broker;
        private readonly string _connectionId;
        private bool _disposed;

        public Registration(InProcessRemoteAgentBroker broker, string connectionId)
        {
            _broker = broker;
            _connectionId = connectionId;
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _broker.Unregister(_connectionId);
        }
    }
}
