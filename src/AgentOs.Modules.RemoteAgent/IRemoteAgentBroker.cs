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
/// fresh per-run directory) — see <c>LlmRequest.Autonomy</c>.
/// <paramref name="Files"/> lists the ticket files the runner fetches (<c>ReadRunFile</c>) and writes under
/// <c>&lt;run dir&gt;/.agentos/files/</c> before the CLI starts (ADR-0015); null = none.</summary>
/// <paramref name="Collect"/> asks the runner to send back the image files the agent listed in its reply (the Design
/// gate's screenshots, ADR-0016) with <c>UploadRunFile</c> before it completes the request; null = nothing comes back.
/// The scope they are stored under stays on the server.
public sealed record RemoteExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null, string? Autonomy = null,
    System.Collections.Generic.IReadOnlyList<RunFileEntry>? Files = null, AgentOs.Domain.Files.RunCollect? Collect = null);

/// <summary>A file a runner sent back for a request, complete and checked (<see cref="AgentOs.Domain.Files.RunUploadRules"/>).</summary>
public sealed record RunUpload(string Path, byte[] Content, string ContentType);

/// <summary>One ticket file of a dispatch, as the runner sees it before fetching it: where it goes, how big it is and the
/// SHA-256 the written bytes must match.</summary>
public sealed record RunFileEntry(string Path, long Size, string Sha256);

/// <summary>A piece of a ticket file the runner fetched (<c>ReadRunFile</c>): at most <see cref="MaxBytes"/> bytes from
/// the asked offset; <paramref name="Last"/> when the file ends with it.</summary>
public sealed record RunFileChunk(byte[] Data, bool Last)
{
    /// <summary>The largest chunk (bytes) — well under SignalR's comfortable message size once base64-encoded.</summary>
    public const int MaxBytes = 256 * 1024;
}

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
/// The runner re-reads the recipe from its own checkout and refuses when it differs. <paramref name="IdleMinutes"/> stops it once
/// nobody has had it open that long (0 = the runner's default); <paramref name="BoardId"/> / <paramref name="RepoId"/> travel
/// with it so a runner that restarted can tell the server which Board card its still-running app belongs to.</summary>
public sealed record PreviewStart(string PreviewId, string Owner, string Repo, string Branch, string Command, string Url, int ReadyTimeoutSeconds,
    int IdleMinutes = 0, Guid BoardId = default, Guid RepoId = default);

/// <summary>A preview a runner still runs, announced when it (re)connects — after a runner restart its apps keep running
/// (they live outside the runner's process tree) and the server re-attaches them. Mirrors the runner's record.</summary>
public sealed record PreviewAnnouncement(string PreviewId, string Owner, string Repo, string Command, string Url,
    DateTimeOffset StartedAt, Guid BoardId, Guid RepoId, string State);

/// <summary>A preview a runner re-announced, with the tenant and member of the connection that announced it.</summary>
public sealed record PreviewReattach(string TenantId, string UserId, PreviewAnnouncement Preview);

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

    /// <summary>Raised when a (re)connected runner announces a preview it still runs — the server re-attaches it.</summary>
    event Action<PreviewReattach>? PreviewReattached { add { } remove { } }

    /// <summary>Called by the hub when a runner (re)connects: the previews it still runs, and the ones it found gone.
    /// A preview of another tenant or member is ignored; one that was stopped while the computer was away is stopped now.</summary>
    void AnnouncePreviews(string connectionId, System.Collections.Generic.IReadOnlyList<PreviewAnnouncement> running,
        System.Collections.Generic.IReadOnlyList<string> gone) { }
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

    // ── Ticket files (ADR-0015) ──

    /// <summary>Hold <paramref name="files"/> for request (or tool call) <paramref name="id"/>: only the runner it is then
    /// dispatched to can fetch them (<see cref="ReadRunFile"/>). Stage before dispatching; <see cref="UnstageFiles"/> after.</summary>
    void StageFiles(string id, System.Collections.Generic.IReadOnlyList<AgentOs.Domain.Files.RunFile> files) { }

    /// <summary>Called by the hub: a piece of staged file <paramref name="index"/> from <paramref name="offset"/> — only for
    /// the connection the request was dispatched to; null for anything else.</summary>
    RunFileChunk? ReadRunFile(string connectionId, string id, int index, long offset) => null;

    /// <summary>Drop what was staged for <paramref name="id"/>; returns how many files its runner fetched to the end.</summary>
    int UnstageFiles(string id) => 0;

    // ── Files sent back (Design gate, ADR-0016) ──

    /// <summary>Accept files sent back for request <paramref name="id"/> within <paramref name="limits"/> — only from the
    /// runner it is then dispatched to. Call before dispatching; <see cref="TakeUploads"/> after. False when the server's
    /// budget for files in flight (<see cref="InProcessRemoteAgentBroker.MaxInFlightUploadBytes"/>) can't hold this run's
    /// limit: then nothing may come back.</summary>
    bool ExpectUploads(string id, AgentOs.Domain.Files.RunCollect limits) => false;

    /// <summary>Called by the hub: one piece of a file sent back. Null when accepted, else why it was refused (the file is
    /// then dropped). Only the connection the request was dispatched to is heard.</summary>
    string? UploadRunFile(string connectionId, string id, AgentOs.Domain.Files.RunUploadChunk chunk) => "This server takes no files back.";

    /// <summary>The complete files sent back for <paramref name="id"/>, and forget the request.</summary>
    System.Collections.Generic.IReadOnlyList<RunUpload> TakeUploads(string id) => [];
}

/// <summary>In-process broker. Singleton; thread-safe.</summary>
public sealed class InProcessRemoteAgentBroker(TimeProvider? time = null) : IRemoteAgentBroker, IRunnerPreviewBroker
{
    /// <summary>How long a preview whose computer disconnected stays "running" on the Board, waiting for the runner to
    /// come back (a lab update, a re-pairing, a crash + restart) and re-announce it.</summary>
    public static readonly TimeSpan PreviewReconnectGrace = TimeSpan.FromMinutes(15);

    /// <summary>How long a Stop pressed while the computer was offline is remembered, counted from the moment Stop was
    /// pressed: the app is closed whenever that computer comes back within it. In memory only — a Web restart forgets it
    /// (then the returning runner re-announces the app, the Board shows it again and the person can press Stop again).</summary>
    public static readonly TimeSpan StopOnReturnKeep = TimeSpan.FromDays(7);

    /// <summary>What the Board says when a computer stayed away past <see cref="PreviewReconnectGrace"/>: the server stops
    /// following the app, but nothing could close it — it may well still run there.</summary>
    public const string OfflineMessage =
        "Your computer has been offline for 15 minutes, so the Board stopped following this app — it may still be running "
        + "there. To close it, start the AgentOS.NET runner on that computer again (the app shows up here and you can press "
        + "Stop), or run \"AgentOs.RemoteAgent stop-previews\" on it.";

    private const int MaxAnnounced = 32;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteExecResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunnerToolResult>> _pendingToolCalls = new(StringComparer.Ordinal);
    // Preview id → who runs it: the connection (null while its computer is away — DetachedAt says since when), and the
    // tenant + member + runner it belongs to. A report from any other connection is ignored.
    private readonly ConcurrentDictionary<string, PreviewRoute> _previews = new(StringComparer.Ordinal);
    // Previews stopped while their computer was away: stopped for real as soon as it re-announces them (kept for
    // StopOnReturnKeep after Stop was pressed).
    private readonly ConcurrentDictionary<string, StopRequest> _stopOnReturn = new(StringComparer.Ordinal);
    private readonly Lock _sweepLock = new();
    private ITimer? _sweep;

    private sealed record PreviewRoute(string? ConnectionId, string TenantId, string UserId, Guid RunnerId, DateTimeOffset? DetachedAt);
    private sealed record StopRequest(string TenantId, string UserId, DateTimeOffset StoppedAt);
    // Request / tool-call id → the ticket files staged for it (ADR-0015).
    private readonly ConcurrentDictionary<string, StagedFiles> _staged = new(StringComparer.Ordinal);

    // The ticket files of one dispatch. A file's bytes are released as soon as its runner has fetched the last piece.
    private sealed class StagedFiles(System.Collections.Generic.IReadOnlyList<AgentOs.Domain.Files.RunFile> files)
    {
        public readonly byte[]?[] Contents = files.Select(f => (byte[]?)f.Content).ToArray();
        public volatile string? ConnectionId;
        public int Delivered;
    }

    public void StageFiles(string id, System.Collections.Generic.IReadOnlyList<AgentOs.Domain.Files.RunFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > 0) { _staged[id] = new StagedFiles(files); }
    }

    public RunFileChunk? ReadRunFile(string connectionId, string id, int index, long offset)
    {
        if (string.IsNullOrEmpty(id) || !_staged.TryGetValue(id, out var staged) || staged.ConnectionId is not { } owner
            || !string.Equals(owner, connectionId, StringComparison.Ordinal) || index < 0 || index >= staged.Contents.Length)
        {
            return null;
        }
        var content = Volatile.Read(ref staged.Contents[index]);
        if (content is null || offset < 0 || offset > content.LongLength) { return null; }
        var take = (int)Math.Min(RunFileChunk.MaxBytes, content.LongLength - offset);
        var data = content.AsSpan((int)offset, take).ToArray();
        var last = offset + take >= content.LongLength;
        if (last && Interlocked.Exchange(ref staged.Contents[index], null) is not null)
        {
            Interlocked.Increment(ref staged.Delivered);
        }
        return new RunFileChunk(data, last);
    }

    public int UnstageFiles(string id) =>
        !string.IsNullOrEmpty(id) && _staged.TryRemove(id, out var staged) ? Volatile.Read(ref staged.Delivered) : 0;

    // A dispatch's files can be fetched only by the connection it went to — and files come back only from it.
    private void BindStaged(string id, string connectionId)
    {
        if (_staged.TryGetValue(id, out var staged)) { staged.ConnectionId = connectionId; }
        if (_uploads.TryGetValue(id, out var uploads)) { uploads.ConnectionId = connectionId; }
    }

    // ── Files sent back (Design gate, ADR-0016) ──

    // Request id → what its runner is sending back. Bounded by the request's limits; dropped by TakeUploads.
    private readonly ConcurrentDictionary<string, UploadSlot> _uploads = new(StringComparer.Ordinal);

    private sealed class UploadSlot(AgentOs.Domain.Files.RunCollect limits)
    {
        public readonly AgentOs.Domain.Files.RunCollect Limits = limits;
        public readonly object Gate = new();
        public volatile string? ConnectionId;
        public readonly System.Collections.Generic.Dictionary<string, System.IO.MemoryStream> Open = new(StringComparer.OrdinalIgnoreCase);
        public readonly System.Collections.Generic.List<RunUpload> Done = [];
        public long Total;
    }

    /// <summary>The most bytes all runs together may be sending back at once (held in memory until each run ends): eight
    /// runs at the full per-run cap. A run's whole cap is reserved when it is dispatched, so the budget can't be overrun.</summary>
    public const long MaxInFlightUploadBytes = 8 * AgentOs.Domain.Files.RunUploadRules.MaxTotalBytes;

    private long _reservedUploadBytes;

    /// <summary>Bytes reserved by runs that may still send files back (diagnostics and tests).</summary>
    public long ReservedUploadBytes => Interlocked.Read(ref _reservedUploadBytes);

    public bool ExpectUploads(string id, AgentOs.Domain.Files.RunCollect limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(limits);
        // Never above the hard caps, whatever a caller asks for.
        var capped = new AgentOs.Domain.Files.RunCollect(
            Math.Clamp(limits.MaxFiles, 0, AgentOs.Domain.Files.RunUploadRules.MaxFiles),
            Math.Clamp(limits.MaxFileBytes, 0, AgentOs.Domain.Files.RunUploadRules.MaxFileBytes),
            Math.Clamp(limits.MaxTotalBytes, 0, AgentOs.Domain.Files.RunUploadRules.MaxTotalBytes));
        // The same request asked again: its earlier slot (and reservation) goes first.
        if (_uploads.TryRemove(id, out var replaced)) { Release(replaced); }
        // Reserve the run's whole cap against the server-wide budget, or refuse the slot.
        while (true)
        {
            var reserved = Interlocked.Read(ref _reservedUploadBytes);
            if (reserved + capped.MaxTotalBytes > MaxInFlightUploadBytes) { return false; }
            if (Interlocked.CompareExchange(ref _reservedUploadBytes, reserved + capped.MaxTotalBytes, reserved) == reserved) { break; }
        }
        _uploads[id] = new UploadSlot(capped);
        return true;
    }

    private void Release(UploadSlot slot) => Interlocked.Add(ref _reservedUploadBytes, -slot.Limits.MaxTotalBytes);

    public string? UploadRunFile(string connectionId, string id, AgentOs.Domain.Files.RunUploadChunk chunk)
    {
        if (chunk is null || string.IsNullOrEmpty(id) || !_uploads.TryGetValue(id, out var slot) || slot.ConnectionId is not { } owner
            || !string.Equals(owner, connectionId, StringComparison.Ordinal))
        {
            return "Nothing is expected back for this request.";
        }
        if (!AgentOs.Domain.Files.RunUploadRules.TryCheck(chunk.Path, out var path, out var error) || !string.Equals(path, chunk.Path, StringComparison.Ordinal))
        {
            return $"Refused \"{chunk.Path}\": {error ?? "the path must be sent normalised."}";
        }
        var data = chunk.Data ?? [];
        if (data.Length > AgentOs.Domain.Files.RunUploadRules.ChunkBytes) { return $"Refused \"{path}\": a piece is at most {AgentOs.Domain.Files.RunUploadRules.ChunkBytes} bytes."; }
        lock (slot.Gate)
        {
            if (!slot.Open.TryGetValue(path, out var buffer))
            {
                if (chunk.Offset != 0) { return $"Refused \"{path}\": it must start at the beginning."; }
                if (slot.Done.Exists(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))) { return $"Refused \"{path}\": it was already sent."; }
                if (slot.Done.Count + slot.Open.Count >= slot.Limits.MaxFiles) { return $"Refused \"{path}\": at most {slot.Limits.MaxFiles} files come back from one run."; }
                buffer = new System.IO.MemoryStream();
                slot.Open[path] = buffer;
            }
            string? refused = null;
            if (chunk.Offset != buffer.Length) { refused = $"Refused \"{path}\": pieces must arrive in order."; }
            else if (buffer.Length + data.Length > slot.Limits.MaxFileBytes) { refused = $"Refused \"{path}\": a file is at most {slot.Limits.MaxFileBytes} bytes."; }
            else if (slot.Total + data.Length > slot.Limits.MaxTotalBytes) { refused = $"Refused \"{path}\": one run sends back at most {slot.Limits.MaxTotalBytes} bytes."; }
            else if (!chunk.Last && data.Length == 0) { refused = $"Refused \"{path}\": an empty piece."; }
            if (refused is not null) { Drop(slot, path, buffer); return refused; }
            buffer.Write(data);
            slot.Total += data.Length;
            if (!chunk.Last) { return null; }
            var content = buffer.ToArray();
            slot.Open.Remove(path);
            buffer.Dispose();
            if (!AgentOs.Domain.Files.RunUploadRules.LooksLikeImage(path, content))
            {
                slot.Total -= content.LongLength;
                return $"Refused \"{path}\": it isn't the image its name says.";
            }
            slot.Done.Add(new RunUpload(path, content, AgentOs.Domain.Files.RunUploadRules.ContentType(path)!));
            return null;
        }

        static void Drop(UploadSlot slot, string path, System.IO.MemoryStream buffer)
        {
            slot.Total -= buffer.Length;
            slot.Open.Remove(path);
            buffer.Dispose();
        }
    }

    public System.Collections.Generic.IReadOnlyList<RunUpload> TakeUploads(string id)
    {
        if (string.IsNullOrEmpty(id) || !_uploads.TryRemove(id, out var slot)) { return []; }
        Release(slot);
        lock (slot.Gate)
        {
            foreach (var open in slot.Open.Values) { open.Dispose(); }
            slot.Open.Clear();
            return [.. slot.Done];
        }
    }

    public event Action<PreviewCommand>? PreviewCommanded;
    public event Action<PreviewStatus>? PreviewReported;

    public bool StartPreview(PreviewStart start, RunnerTarget target)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(target);
        if (!TryResolveConnection(target, out var connectionId) || !_connections.TryGetValue(connectionId, out var runner)) { return false; }
        _previews[start.PreviewId] = new PreviewRoute(connectionId, runner.TenantId, runner.OwnerUserId, runner.RunnerId, null);
        PreviewCommanded?.Invoke(new PreviewCommand(connectionId, start, null));
        return true;
    }

    public void StopPreview(string previewId)
    {
        if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out var route)) { return; }
        if (route.ConnectionId is { } connectionId)
        {
            PreviewCommanded?.Invoke(new PreviewCommand(connectionId, null, previewId));
            return;
        }
        // The computer is away: the Board stops showing it now, and the app is closed the moment the runner is back.
        if (_previews.TryRemove(new(previewId, route)))
        {
            _stopOnReturn[previewId] = new StopRequest(route.TenantId, route.UserId, _time.GetUtcNow());
            EnsureSweep();
            PreviewReported?.Invoke(new PreviewStatus(previewId, PreviewStates.Stopped,
                "Stopped. Your computer is offline — the app closes as soon as it reconnects."));
        }
    }

    public event Action<PreviewReattach>? PreviewReattached;

    public void AnnouncePreviews(string connectionId, System.Collections.Generic.IReadOnlyList<PreviewAnnouncement> running,
        System.Collections.Generic.IReadOnlyList<string> gone)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(gone);
        if (string.IsNullOrEmpty(connectionId) || !_connections.TryGetValue(connectionId, out var runner)) { return; }
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var preview in running.Take(MaxAnnounced))
        {
            if (preview is null || !IsPreviewId(preview.PreviewId)
                || preview.State is not (PreviewStates.Preparing or PreviewStates.Starting or PreviewStates.Ready))
            {
                continue;
            }
            seen.Add(preview.PreviewId);
            if (_stopOnReturn.TryGetValue(preview.PreviewId, out var stopped))
            {
                if (SameMember(stopped.TenantId, stopped.UserId, runner) && _stopOnReturn.TryRemove(new(preview.PreviewId, stopped)))
                {
                    PreviewCommanded?.Invoke(new PreviewCommand(connectionId, null, preview.PreviewId));
                }
                continue;
            }
            var attached = new PreviewRoute(connectionId, runner.TenantId, runner.OwnerUserId, runner.RunnerId, null);
            if (_previews.TryGetValue(preview.PreviewId, out var route))
            {
                // Only the member's own computer may take a preview back, and only one whose computer went away.
                if (!SameMember(route, runner)) { continue; }
                if (route.ConnectionId is { } live && !string.Equals(live, connectionId, StringComparison.Ordinal)
                    && _connections.ContainsKey(live))
                {
                    continue;
                }
                if (!_previews.TryUpdate(preview.PreviewId, attached, route)) { continue; }
            }
            else if (!_previews.TryAdd(preview.PreviewId, attached))
            {
                continue; // the server forgot it (a restart): re-attached as new, if the Board can place it
            }
            var text = preview with { Command = Clip(preview.Command, 2000), Url = Clip(preview.Url, 2000) };
            PreviewReattached?.Invoke(new PreviewReattach(runner.TenantId, runner.OwnerUserId, text));
        }
        // Gone ones count only from the member's own computer — another member can't clear someone's preview or Stop.
        foreach (var id in gone.Take(MaxAnnounced).Where(id => !string.IsNullOrEmpty(id)))
        {
            if (_previews.TryGetValue(id, out var route) && route.ConnectionId is null && SameMember(route, runner))
            {
                ForgetDetached(id, route, "The app had stopped while your computer was away.");
            }
            if (_stopOnReturn.TryGetValue(id, out var stop) && SameMember(stop.TenantId, stop.UserId, runner))
            {
                _stopOnReturn.TryRemove(new(id, stop));
            }
        }
        // The same runner came back without some of its previews: they are gone.
        foreach (var (id, route) in _previews.Where(e => e.Value.ConnectionId is null && e.Value.RunnerId == runner.RunnerId && !seen.Contains(e.Key)))
        {
            ForgetDetached(id, route, "The app isn't running on your computer any more.");
        }
    }

    /// <summary>Report previews whose computer stayed away past <see cref="PreviewReconnectGrace"/> as stopped.</summary>
    public void SweepDetachedPreviews()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, route) in _previews.Where(e => e.Value.DetachedAt is { } since && now - since >= PreviewReconnectGrace))
        {
            ForgetDetached(id, route, OfflineMessage);
        }
        foreach (var (id, stop) in _stopOnReturn.Where(e => now - e.Value.StoppedAt >= StopOnReturnKeep))
        {
            _stopOnReturn.TryRemove(new(id, stop));
        }
        lock (_sweepLock)
        {
            if (!_previews.Values.Any(r => r.DetachedAt is not null) && _stopOnReturn.IsEmpty)
            {
                _sweep?.Dispose();
                _sweep = null;
            }
        }
    }

    private void ForgetDetached(string id, PreviewRoute route, string message)
    {
        if (_previews.TryRemove(new(id, route)))
        {
            PreviewReported?.Invoke(new PreviewStatus(id, PreviewStates.Stopped, message));
        }
    }

    private void EnsureSweep()
    {
        lock (_sweepLock)
        {
            _sweep ??= _time.CreateTimer(_ => SweepDetachedPreviews(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    private static bool SameMember(PreviewRoute route, RunnerConnection runner) => SameMember(route.TenantId, route.UserId, runner);

    private static bool SameMember(string tenantId, string userId, RunnerConnection runner) =>
        string.Equals(tenantId, runner.TenantId, StringComparison.Ordinal)
        && string.Equals(userId, runner.OwnerUserId, StringComparison.Ordinal);

    private static bool IsPreviewId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Clip(string? text, int max) => text is null ? string.Empty : text.Length > max ? text[..max] : text;

    public void ReportPreview(string connectionId, string previewId, string state, string? message)
    {
        if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out var route)
            || !string.Equals(route.ConnectionId, connectionId, StringComparison.Ordinal))
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

        BindStaged(request.Id, connectionId);
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

        BindStaged(toolCall.ToolCallId, connectionId);
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
        // The runner went away, but its previews run on outside it (a restart, an update, a re-pairing): the Board keeps
        // showing them while the computer has PreviewReconnectGrace to come back and re-announce them.
        var detachedAny = false;
        foreach (var (previewId, route) in _previews.Where(e => string.Equals(e.Value.ConnectionId, connectionId, StringComparison.Ordinal)))
        {
            detachedAny |= _previews.TryUpdate(previewId, route with { ConnectionId = null, DetachedAt = _time.GetUtcNow() }, route);
        }
        if (detachedAny) { EnsureSweep(); }
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
