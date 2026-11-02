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
//
// Governance: ReportToolCalls carries the tool calls a CLI run made on the member's computer; the broker accepts a batch
// only from the runner that run was dispatched to, and each call becomes an evidence row (RunnerToolEvidence.cs).

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

    /// <summary>The largest message a runner may send (bytes): one upload piece as base64 JSON, with room to spare.</summary>
    public const long MaxReceiveBytes = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, IDisposable> Registrations = new(StringComparer.Ordinal);

    private readonly IRemoteAgentBroker _broker;
    private readonly IRunnerDirectory _runners;
    private readonly IRunnerPairingService _pairing;
    private readonly AgentOs.Domain.Tools.IToolInvocationLog? _evidence;

    public RemoteAgentHub(IRemoteAgentBroker broker, IRunnerDirectory runners, IRunnerPairingService pairing,
        AgentOs.Domain.Tools.IToolInvocationLog? evidence = null)
    {
        _broker = broker;
        _runners = runners;
        _pairing = pairing;
        _evidence = evidence;
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
            || !_pairing.Verify(presentedToken, identity.TokenHash)
            || _broker.IsRevoked(identity.RunnerId))   // revoked here after its row was read (GAP-04)
        {
            Context.Abort();
            return;
        }

        // The abort callback lets a revoke cut this connection off at once (GAP-04) instead of at its next handshake.
        var context = Context;
        Registrations[Context.ConnectionId] = _broker.RegisterRunner(
            Context.ConnectionId,
            new RunnerConnection(identity.RunnerId, identity.TenantId, identity.OwnerUserId),
            abort: context.Abort);
        // A revoke that landed between the check above and the registration: the broker refused it and aborted the
        // connection — nothing to mark paired.
        if (_broker.IsRevoked(identity.RunnerId))
        {
            return;
        }

        // Promote Pending → Paired now that the token has been verified, so the Runners table reflects
        // reality: before this, a machine that connected successfully still read "Pending" forever and
        // the documented Pending → Paired transition existed only as a comment. Deliberately NOT
        // Context.ConnectionAborted — the pairing fact has already happened, and a connection that drops
        // in the next millisecond must not roll it back.
        await _runners.MarkPairedAsync(identity.RunnerId, CancellationToken.None).ConfigureAwait(false);
        // Re-pairing the same computer (a new code, a reinstall) used to leave one Offline row — and one still-valid
        // token — per pairing on Board › Computers. Its older offline entries retire now.
        await _runners.RetireReplacedAsync(identity.RunnerId, _broker.IsConnected, CancellationToken.None).ConfigureAwait(false);

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

    /// <summary>Invoked by the runner when it has a result for a dispatched request (M3). Heard only from the connection
    /// the request was dispatched to (or that runner's reconnect, same tenant) — GAP-06.</summary>
    public void CompleteRequest(RemoteExecResult result) => _broker.Complete(Context.ConnectionId, result);

    /// <summary>Runner → server: the plain step a running request is on (live progress on the Board). Heard only from the
    /// connection the request was dispatched to, or that runner's reconnect (same runner id, same tenant) — GAP-06; a step
    /// from any other connection, or for a request no one waits for, is dropped and counted.</summary>
    public void ReportProgress(string requestId, string step) => _broker.ReportProgress(Context.ConnectionId, requestId, step);

    /// <summary>Runner → server: where a "Try the app" preview it runs stands (Preparing / Starting / Ready / Failed / Stopped).</summary>
    public void ReportPreview(string previewId, string state, string? message) =>
        (_broker as IRunnerPreviewBroker)?.ReportPreview(Context.ConnectionId, previewId, state, message);

    /// <summary>Runner → server, on every (re)connect: the "Try the app" previews it still runs (they outlive a runner
    /// restart) and the ones it found gone. The server re-attaches the first and closes the second on the Board.</summary>
    public void AnnouncePreviews(PreviewAnnouncement[]? running, string[]? gone) =>
        (_broker as IRunnerPreviewBroker)?.AnnouncePreviews(Context.ConnectionId, running ?? [], gone ?? []);

    /// <summary>Runner → server: a piece of a ticket file of the request it is running (ADR-0015). Answers only the
    /// connection the request was dispatched to, or that runner's reconnect (same runner id, same tenant); null for
    /// anything else (an unknown id, another runner).</summary>
    public RunFileChunk? ReadRunFile(string requestId, int index, long offset) =>
        _broker.ReadRunFile(Context.ConnectionId, requestId, index, offset);

    /// <summary>Runner → server: one piece of a file it sends back for the request it ran (the Design gate's screenshots,
    /// ADR-0016). Heard only from the connection the request was dispatched to, or that runner's reconnect (same runner
    /// id, same tenant) — the screenshots come at the end of a run, after any mid-run reconnect; every piece is path-,
    /// size- and order-checked. Null when accepted, else why it was refused.</summary>
    public string? UploadRunFile(string requestId, AgentOs.Domain.Files.RunUploadChunk chunk) =>
        _broker.UploadRunFile(Context.ConnectionId, requestId, chunk);

    /// <summary>Runner → server: tool calls the CLI made during a run on this computer (Bash, Edit, Write, Read…), in
    /// batches. Recorded as evidence (origin "member-computer") when the broker accepts them: only from the runner the
    /// run was dispatched to (also after it reconnected), while it runs or within the grace window after it ended. The
    /// run's tenant is pushed as the ambient identity first, so a write that happens inline (the evidence queue full)
    /// runs with that tenant set for row-level security.</summary>
    public async Task ReportToolCalls(string requestId, string? permissions, System.Collections.Generic.IReadOnlyList<RunnerToolEvent>? calls)
    {
        if (_broker.AcceptToolCalls(Context.ConnectionId, requestId, permissions, calls) is not { } batch) { return; }
        Guid? session = Guid.TryParse(batch.Context.SessionId, CultureInfo.InvariantCulture, out var s) ? s : null;
        using (AgentOs.SharedKernel.Identity.AmbientIdentity.Push(batch.Context.TenantId, null, session))
        {
            await RunnerToolEvidence.RecordAsync(_evidence, batch, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Invoked by the runner when it has finished executing a tool call (M4). Heard only from the connection the
    /// call was dispatched to (or that runner's reconnect, same tenant) — GAP-06.</summary>
    public void CompleteToolCall(RunnerToolResult result) => _broker.CompleteToolCall(Context.ConnectionId, result);
}
