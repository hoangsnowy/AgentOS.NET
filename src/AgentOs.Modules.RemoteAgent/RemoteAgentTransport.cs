// Bridges the in-process IRemoteAgentBroker to the SignalR hub.
//
// M3 — full-prompt dispatch: when the gateway raises Dispatched, push "Execute" to the ONE resolved
// runner connection (Clients.Client, never Clients.All). Runner replies via CompleteRequest.
//
// M4 — tool-call dispatch: when the broker raises ToolCallDispatched, push "ExecuteToolCall" to the
// same targeted connection. Runner replies via CompleteToolCall. The server-side LLM loop stays on
// the server; only the tool execution crosses the wire.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Hosted service that wires the broker's dispatch event to the SignalR hub.</summary>
public sealed class RemoteAgentTransport : IHostedService
{
    private readonly IRemoteAgentBroker _broker;
    private readonly IHubContext<RemoteAgentHub> _hub;
    private readonly IRemoteExecApprover _approver;
    private readonly ILogger<RemoteAgentTransport> _logger;

    public RemoteAgentTransport(
        IRemoteAgentBroker broker,
        IHubContext<RemoteAgentHub> hub,
        IRemoteExecApprover approver,
        ILogger<RemoteAgentTransport> logger)
    {
        _broker = broker;
        _hub = hub;
        _approver = approver;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _broker.Dispatched += OnDispatched;
        _broker.Cancelled += OnCancelled;
        _broker.ToolCallDispatched += OnToolCallDispatched;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _broker.Dispatched -= OnDispatched;
        _broker.Cancelled -= OnCancelled;
        _broker.ToolCallDispatched -= OnToolCallDispatched;
        return Task.CompletedTask;
    }

    // ── M3: full-prompt dispatch ──

    private void OnDispatched(RemoteDispatch dispatch) => _ = PushAsync(dispatch);

    private async Task PushAsync(RemoteDispatch dispatch)
    {
        var request = dispatch.Request;
        try
        {
            if (!await _approver.ApproveAsync(request).ConfigureAwait(false))
            {
                _logger.LogWarning("[RemoteAgent] request {Id} denied by approval gate.", request.Id);
                _broker.Complete(new RemoteExecResult(request.Id, false, string.Empty, "Denied by approval gate."));
                return;
            }
            await _hub.Clients.Client(dispatch.ConnectionId).SendAsync("Execute", request).ConfigureAwait(false);
        }
        catch (HubException ex) { _broker.Complete(new RemoteExecResult(request.Id, false, string.Empty, $"Transport error: {ex.Message}")); }
        catch (System.IO.IOException ex) { _broker.Complete(new RemoteExecResult(request.Id, false, string.Empty, $"Transport error: {ex.Message}")); }
        catch (InvalidOperationException ex) { _broker.Complete(new RemoteExecResult(request.Id, false, string.Empty, $"Transport error: {ex.Message}")); }
        catch (TimeoutException ex) { _broker.Complete(new RemoteExecResult(request.Id, false, string.Empty, $"Transport error: {ex.Message}")); }
    }

    // Stop / timeout: tell the runner to kill the CLI run. Best effort — a runner that is already gone has nothing
    // left to kill, and an older runner without a "Cancel" handler ignores the message.
    private void OnCancelled(RemoteCancel cancel) => _ = PushCancelAsync(cancel);

    private async Task PushCancelAsync(RemoteCancel cancel)
    {
        try
        {
            await _hub.Clients.Client(cancel.ConnectionId).SendAsync("Cancel", cancel.RequestId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HubException or System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "[RemoteAgent] could not deliver cancel for request {Id}.", cancel.RequestId);
        }
    }

    // ── M4: bidirectional tool-call dispatch ──

    private void OnToolCallDispatched(RunnerToolDispatch dispatch) => _ = PushToolCallAsync(dispatch);

    private async Task PushToolCallAsync(RunnerToolDispatch dispatch)
    {
        void Handle(Exception e) =>
            _broker.CompleteToolCall(new RunnerToolResult(
                dispatch.Call.RequestId,
                dispatch.Call.ToolCallId,
                false,
                string.Empty,
                $"Transport error: {e.Message}"));

        try
        {
            // Same governance seam as the full-prompt path: a tool call (runner_shell = arbitrary
            // shell on the paired machine) must pass the approver before it crosses the wire.
            if (!await _approver.ApproveToolCallAsync(dispatch.Call).ConfigureAwait(false))
            {
                _logger.LogWarning("[RemoteAgent] tool call {ToolCallId} ({Tool}) denied by approval gate.",
                    dispatch.Call.ToolCallId, dispatch.Call.ToolName);
                _broker.CompleteToolCall(new RunnerToolResult(
                    dispatch.Call.RequestId, dispatch.Call.ToolCallId, false, string.Empty,
                    "Denied by approval gate."));
                return;
            }
            await _hub.Clients.Client(dispatch.ConnectionId)
                .SendAsync("ExecuteToolCall", dispatch.Call)
                .ConfigureAwait(false);
        }
        catch (HubException ex) { Handle(ex); }
        catch (System.IO.IOException ex) { Handle(ex); }
        catch (InvalidOperationException ex) { Handle(ex); }
        catch (TimeoutException ex) { Handle(ex); }
    }
}
