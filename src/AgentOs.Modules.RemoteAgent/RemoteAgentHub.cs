// SignalR transport for the remote dev-IDE agent runtime. A dev-side runner connects here with its
// runner id + pairing token (?runnerId=...&token=...), is authenticated against the persisted salted
// token hash, then receives "Execute" pushes for codegen requests and calls CompleteRequest with the
// result.
//
// M3 — pairing is now PER-RUNNER. The old single shared RemoteAgent:PairingToken (one static secret
// for every machine, in plaintext config) is gone. The hub resolves the runner row by the presented id
// (tenant-unfiltered — the connection has no tenant yet), verifies the token against its stored hash in
// constant time, rejects revoked/unknown runners, and registers the connection with the runner's
// tenant + owning member so dispatch can target it.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Hub dev runners connect to.</summary>
public sealed class RemoteAgentHub : Hub
{
    /// <summary>Route the hub is mapped at.</summary>
    public const string Path = "/hubs/remote-agent";

    private static readonly ConcurrentDictionary<string, IDisposable> Registrations = new(StringComparer.Ordinal);

    private readonly IRemoteAgentBroker _broker;
    private readonly IRunnerDirectory _runners;
    private readonly IRunnerPairingService _pairing;

    public RemoteAgentHub(IRemoteAgentBroker broker, IRunnerDirectory runners, IRunnerPairingService pairing)
    {
        _broker = broker;
        _runners = runners;
        _pairing = pairing;
    }

    private static string? HeaderOrQuery(Microsoft.AspNetCore.Http.HttpRequest? request, string header, string query)
    {
        if (request is null) { return null; }
        var fromHeader = request.Headers[header].ToString();
        return string.IsNullOrWhiteSpace(fromHeader) ? request.Query[query].ToString() : fromHeader;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // Credentials come in headers (E0.5 — the token must not ride the URL, which lands in proxy/server logs);
        // the query string is accepted only as a fallback for runner binaries that predate it.
        var request = Context.GetHttpContext()?.Request;
        var rawRunnerId = HeaderOrQuery(request, "X-AgentOS-Runner-Id", "runnerId");
        var presentedToken = HeaderOrQuery(request, "X-AgentOS-Runner-Token", "token");

        if (!Guid.TryParse(rawRunnerId, CultureInfo.InvariantCulture, out var runnerId)
            || string.IsNullOrWhiteSpace(presentedToken))
        {
            Context.Abort();
            return;
        }

        var identity = await _runners.FindForPairingAsync(runnerId, Context.ConnectionAborted).ConfigureAwait(false);
        if (identity is null
            || string.Equals(identity.Status, "Revoked", StringComparison.Ordinal)
            || !_pairing.Verify(presentedToken, identity.TokenHash))
        {
            Context.Abort();
            return;
        }

        Registrations[Context.ConnectionId] = _broker.RegisterRunner(
            Context.ConnectionId,
            new RunnerConnection(identity.RunnerId, identity.TenantId, identity.OwnerUserId));

        // Promote Pending → Paired now that the token has been verified, so the Runners table reflects
        // reality: before this, a machine that connected successfully still read "Pending" forever and
        // the documented Pending → Paired transition existed only as a comment. Deliberately NOT
        // Context.ConnectionAborted — the pairing fact has already happened, and a connection that drops
        // in the next millisecond must not roll it back.
        await _runners.MarkPairedAsync(identity.RunnerId, CancellationToken.None).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Registrations.TryRemove(Context.ConnectionId, out var registration))
        {
            registration.Dispose();
        }
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Invoked by the runner when it has a result for a dispatched request (M3).</summary>
    public void CompleteRequest(RemoteExecResult result) => _broker.Complete(result);

    /// <summary>Invoked by the runner when it has finished executing a tool call (M4).</summary>
    public void CompleteToolCall(RunnerToolResult result) => _broker.CompleteToolCall(result);
}
