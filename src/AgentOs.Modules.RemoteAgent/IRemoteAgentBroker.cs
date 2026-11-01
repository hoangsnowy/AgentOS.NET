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
// runner does". Each of THESE calls passes through IToolGateway on the server (IToolPolicy + evidence)
// before being dispatched.
//
// An M3 run is different: the CLI on the member's computer (claude -p with scoped permissions) calls its OWN tools
// (Bash, Edit, Write, Read…) and IToolGateway never sees them — the allow/deny decision is the CLI's scoped
// permissions on that machine (ScopedAutonomy.cs), not IToolPolicy. Governance there is evidence after the fact: the
// runner reports every call (ReportToolCalls — a bounded, redacted summary, ok / error / refused) during the run, the
// broker accepts a batch only from the runner the run was dispatched to (TrackRun / AcceptToolCalls), and the hub
// records each call through IToolInvocationLog with origin "member-computer" (RunnerToolEvidence.cs).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// <c>&lt;run dir&gt;/.agentos/files/</c> before the CLI starts (ADR-0015); null = none.
/// <paramref name="Collect"/> asks the runner to send back the image files the agent listed in its reply (the Design
/// gate's screenshots, ADR-0016) with <c>UploadRunFile</c> before it completes the request; null = nothing comes back.
/// The scope they are stored under stays on the server.
/// <paramref name="ResumeOf"/> is the id of an earlier request this one continues (a follow-up asking a run that ended
/// without its reply for it): the runner works in that run's kept folder and resumes its CLI session when it still has
/// them, else it starts fresh — an older runner ignores it. Null = a fresh run.</summary>
public sealed record RemoteExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null, string? Autonomy = null,
    System.Collections.Generic.IReadOnlyList<RunFileEntry>? Files = null, AgentOs.Domain.Files.RunCollect? Collect = null, string? ResumeOf = null);

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

    /// <summary>True only when the runner continued the earlier run this request resumed (<see cref="RemoteExecRequest.ResumeOf"/>):
    /// it ran the CLI with <c>--resume &lt;session&gt;</c> in that run's kept folder. An older runner never sets it.</summary>
    public bool Resumed { get; init; }
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

// ── Evidence of a CLI run's own tool calls (M3) ────────────────────────────────────────────────

/// <summary>One tool call the CLI on a member's computer made during a run, as its runner reports it.</summary>
/// <param name="ToolUseId">The CLI's id of the call.</param>
/// <param name="Tool">The CLI tool (<c>Bash</c>, <c>Edit</c>, an MCP tool…).</param>
/// <param name="Summary">What it did, bounded + redacted on the computer (and again on the server) — never contents or output.</param>
/// <param name="Ok">True when the tool ran and reported no error.</param>
/// <param name="Denied">True when the CLI's permissions refused it (it never ran).</param>
/// <param name="StartedUtc">When the CLI asked for it.</param>
/// <param name="DurationMs">Until its result came back.</param>
public sealed record RunnerToolEvent(string ToolUseId, string Tool, string Summary, bool Ok, bool Denied, DateTimeOffset StartedUtc, long DurationMs);

/// <summary>Who a dispatched CLI run works for — what its tool calls are recorded under.</summary>
/// <param name="TenantId">The tenant (the dispatch target's).</param>
/// <param name="SessionId">The Board task (session) the run belongs to, when there is one.</param>
/// <param name="AgentRole">The agent role (Coding, Reviewer, Testing…), when the caller named one.</param>
/// <param name="RequestedAutonomy">What the SERVER asked the CLI to be allowed (<see cref="RemoteExecRequest.Autonomy"/>:
/// null = answer only, "scoped"…) — recorded next to what the computer reports it actually used.</param>
public sealed record RemoteRunContext(string TenantId, string? SessionId, string? AgentRole, string? RequestedAutonomy = null);

/// <summary>A batch of tool calls the broker accepted: from the runner the run went to, sanitised and within the run's
/// cap — ready to record. <paramref name="Permissions"/> is what the computer reported with the run's FIRST batch,
/// pinned for the rest of the run (a later batch can't relabel it).</summary>
public sealed record RunnerToolCallBatch(string RequestId, RemoteRunContext Context, Guid RunnerId, string Permissions,
    System.Collections.Generic.IReadOnlyList<RunnerToolEvent> Calls);

/// <summary>Why the broker refused (part of) a runner's batch — the low-cardinality <c>reason</c> tag of
/// <c>agentos.evidence.remote_refused</c>.</summary>
public static class RunnerToolRefusals
{
    /// <summary>No run with that id is tracked (never dispatched here, or forgotten after its grace window).</summary>
    public const string UnknownRun = "unknown_run";

    /// <summary>Tracked, but not dispatched yet.</summary>
    public const string NotDispatched = "not_dispatched";

    /// <summary>The batch came from a connection that is not the run's runner (another computer or tenant).</summary>
    public const string WrongConnection = "wrong_connection";

    /// <summary>The run ended longer ago than the grace window.</summary>
    public const string GracePassed = "grace_passed";

    /// <summary>The run already recorded its cap of calls.</summary>
    public const string RunCap = "run_cap";

    /// <summary>Calls beyond the per-batch cap.</summary>
    public const string BatchCap = "batch_cap";
}

/// <summary>What decided which tools a CLI run could use, as the runner reports it with each batch.</summary>
public static class RunnerPermissions
{
    /// <summary>Scoped autonomy: the runner's allow/deny lists (a Board "Run on my machine" session).</summary>
    public const string Scoped = "scoped";

    /// <summary><c>REMOTE_AGENT_YOLO</c>: the CLI skipped its permission checks.</summary>
    public const string Yolo = "yolo";

    /// <summary><c>REMOTE_AGENT_ARGS</c>: flags the machine's operator chose.</summary>
    public const string Custom = "custom";

    /// <summary>The CLI's defaults (answer-only).</summary>
    public const string Prompt = "prompt";

    /// <summary>A reported value, or <see cref="Prompt"/> for anything unknown.</summary>
    public static string Normalize(string? reported) => reported?.Trim().ToLowerInvariant() switch
    {
        Scoped => Scoped,
        Yolo => Yolo,
        Custom => Custom,
        _ => Prompt,
    };

    /// <summary>The evidence's policy source for <paramref name="permissions"/>.</summary>
    public static string PolicySource(string permissions) => Normalize(permissions) switch
    {
        Scoped => AgentOs.Domain.Tools.ToolEvidencePolicies.CliScopedPermissions,
        Yolo => AgentOs.Domain.Tools.ToolEvidencePolicies.CliSkipPermissions,
        Custom => AgentOs.Domain.Tools.ToolEvidencePolicies.CliCustomArgs,
        _ => AgentOs.Domain.Tools.ToolEvidencePolicies.CliDefaultPermissions,
    };
}

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

    /// <summary>Called by the hub when a runner sends <c>ReportProgress</c>; ignored for requests no one waits for and
    /// for any connection other than the one the request was dispatched to (or that runner's reconnect, same tenant).</summary>
    void ReportProgress(string connectionId, string requestId, string stepKey);

    Task<RemoteExecResult> DispatchAsync(RemoteExecRequest request, RunnerTarget target, TimeSpan timeout, CancellationToken cancellationToken = default);

    IDisposable RegisterRunner(string connectionId, RunnerConnection runner);

    /// <summary>Register a runner connection with the transport's way to abort it — used when the runner is revoked
    /// (<see cref="AgentOs.Domain.Sessions.IRunnerDisconnector.Disconnect"/>, GAP-04).</summary>
    IDisposable RegisterRunner(string connectionId, RunnerConnection runner, Action? abort) => RegisterRunner(connectionId, runner);

    /// <summary>Server-side completion (the transport's approval denial or delivery failure) — trusted, no owner check.</summary>
    void Complete(RemoteExecResult result);

    /// <summary>Called by the hub when a runner sends <c>CompleteRequest</c>. Accepted only from the connection the request
    /// was dispatched to (or a reconnect of that same runner in the same tenant); false when refused (counted and logged).</summary>
    bool Complete(string connectionId, RemoteExecResult result);

    // ── M4: bidirectional tool-call protocol ──

    /// <summary>Raised when a tool call has been resolved to a connection and is ready to push.</summary>
    event Action<RunnerToolDispatch>? ToolCallDispatched;

    /// <summary>Dispatch a single tool-execution request to the runner matched by <paramref name="target"/>.</summary>
    Task<RunnerToolResult> DispatchToolCallAsync(
        RunnerToolCall toolCall,
        RunnerTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side completion of a tool call (approval denial, delivery failure) — trusted, no owner check.</summary>
    void CompleteToolCall(RunnerToolResult result);

    /// <summary>Called by the hub when the runner sends a <c>CompleteToolCall</c> message. Accepted only from the connection
    /// the call was dispatched to (or a reconnect of that same runner in the same tenant); false when refused.</summary>
    bool CompleteToolCall(string connectionId, RunnerToolResult result);

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

    // ── Evidence of a CLI run's own tool calls ──

    /// <summary>Remember who request <paramref name="requestId"/> works for, before dispatching it: the tool calls its
    /// runner reports are accepted (<see cref="AcceptToolCalls"/>) only for a tracked run.</summary>
    void TrackRun(string requestId, RemoteRunContext context) { }

    /// <summary>Called by the hub: a batch of tool calls a runner reports for <paramref name="requestId"/>. Accepted only
    /// from the runner the run was dispatched to (its connection, or its new one after a reconnect — same runner, same
    /// tenant), while it runs or shortly after (a stopped run's last batch), within the run's cap; null when refused
    /// (counted and logged, never silent). The calls come back sanitised (bounded, redacted again), and the reported
    /// permissions are pinned by the run's first batch.</summary>
    RunnerToolCallBatch? AcceptToolCalls(string connectionId, string requestId, string? permissions,
        System.Collections.Generic.IReadOnlyList<RunnerToolEvent>? calls) => null;

    /// <summary>The tool calls (and refusals among them) accepted so far for a tracked run.</summary>
    (int ToolCalls, int Denials) ToolCallTotals(string requestId) => default;
}

/// <summary>In-process broker. Singleton; thread-safe.</summary>
public sealed class InProcessRemoteAgentBroker : IRemoteAgentBroker, IRunnerPreviewBroker, AgentOs.Domain.Sessions.IRunnerDisconnector
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

    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new(StringComparer.Ordinal);
    // Connection id → how the transport aborts it (GAP-04: a revoked runner is cut off at once).
    private readonly ConcurrentDictionary<string, Action> _aborts = new(StringComparer.Ordinal);
    // Pending id → the waiting task + who it was dispatched to (GAP-06: only that runner may answer it).
    private readonly ConcurrentDictionary<string, Pending<RemoteExecResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Pending<RunnerToolResult>> _pendingToolCalls = new(StringComparer.Ordinal);

    // A dispatched request or tool call: the task its caller awaits, and the connection (+ runner, tenant) it went to.
    private sealed record Pending<T>(TaskCompletionSource<T> Task, string ConnectionId, RunnerConnection? Runner);
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
    // Request id → who the CLI run works for + where it went (evidence of its tool calls).
    private readonly ConcurrentDictionary<string, TrackedRun> _runs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>How long after a run ended (completed, stopped, timed out) its runner's last batch is still accepted.</summary>
    internal static readonly TimeSpan ToolCallGrace = TimeSpan.FromMinutes(2);

    /// <summary>The most tool calls recorded for one run (the runner itemises 2000 and counts the rest in one row).</summary>
    internal const int MaxToolCallsPerRun = 2001;

    /// <summary>The most calls accepted from one batch.</summary>
    internal const int MaxToolCallsPerBatch = 200;

    // Evidence must never be dropped silently: every refused call is counted here (reason tag) and logged.
    private static readonly System.Diagnostics.Metrics.Meter EvidenceMeter = new("AgentOs.Tools");
    private static readonly System.Diagnostics.Metrics.Counter<long> RemoteRefused = EvidenceMeter.CreateCounter<long>(
        "agentos.evidence.remote_refused", description: "Tool calls a runner reported that the server refused to record (tag: reason).");

    // GAP-06: a result or progress report from a connection the request was not dispatched to (tag: kind).
    private static readonly System.Diagnostics.Metrics.Counter<long> ResultRefused = EvidenceMeter.CreateCounter<long>(
        "agentos.remote.result_refused", description: "Runner results / progress refused because they came from a connection the request was not dispatched to (tag: kind).");

    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    /// <param name="time">The clock for the grace window (a fake one in tests); null = the system clock.</param>
    /// <param name="logger">Where refused evidence is reported; null = counted only.</param>
    public InProcessRemoteAgentBroker(TimeProvider? time = null, Microsoft.Extensions.Logging.ILogger<InProcessRemoteAgentBroker>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    private sealed class TrackedRun(RemoteRunContext context, DateTimeOffset trackedAt)
    {
        public readonly RemoteRunContext Context = context;
        public readonly DateTimeOffset TrackedAt = trackedAt;
        public volatile string? ConnectionId;
        public Guid RunnerId;
        public long EndedAtUnixMs; // 0 = still running
        public int Accepted;
        public int Denials;
        // What the computer reported with the run's first accepted batch — never relabelled by a later one.
        public string? Permissions;
    }

    public void TrackRun(string requestId, RemoteRunContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(context);
        PruneRuns();
        _runs[requestId] = new TrackedRun(context, _time.GetUtcNow());
    }

    public RunnerToolCallBatch? AcceptToolCalls(string connectionId, string requestId, string? permissions,
        System.Collections.Generic.IReadOnlyList<RunnerToolEvent>? calls)
    {
        if (calls is not { Count: > 0 }) { return null; }
        if (string.IsNullOrEmpty(requestId) || !_runs.TryGetValue(requestId, out var run))
        {
            return Refuse(RunnerToolRefusals.UnknownRun, requestId, calls.Count);
        }
        if (run.ConnectionId is not { } owner) { return Refuse(RunnerToolRefusals.NotDispatched, requestId, calls.Count); }
        if (!IsRunnersConnection(run, owner, connectionId)) { return Refuse(RunnerToolRefusals.WrongConnection, requestId, calls.Count); }
        var now = _time.GetUtcNow();
        var ended = Interlocked.Read(ref run.EndedAtUnixMs);
        if (ended != 0 && now.ToUnixTimeMilliseconds() - ended > (long)ToolCallGrace.TotalMilliseconds)
        {
            _runs.TryRemove(requestId, out _);
            return Refuse(RunnerToolRefusals.GracePassed, requestId, calls.Count);
        }
        var room = MaxToolCallsPerRun - Volatile.Read(ref run.Accepted);
        if (room <= 0) { return Refuse(RunnerToolRefusals.RunCap, requestId, calls.Count); }
        var present = calls.Where(c => c is not null).ToList();
        var take = Math.Min(room, MaxToolCallsPerBatch);
        if (present.Count > take)
        {
            Refuse(take == room ? RunnerToolRefusals.RunCap : RunnerToolRefusals.BatchCap, requestId, present.Count - take);
        }
        var accepted = present.Take(take).Select(c => Sanitize(c, run.TrackedAt, now)).ToList();
        if (accepted.Count == 0) { return null; }
        Interlocked.Add(ref run.Accepted, accepted.Count);
        Interlocked.Add(ref run.Denials, accepted.Count(c => c.Denied));
        // The computer's own report of its permissions is pinned on the first batch: an altered runner can't turn a
        // run's evidence from "scoped" into something else halfway through.
        Interlocked.CompareExchange(ref run.Permissions, RunnerPermissions.Normalize(permissions), null);
        return new RunnerToolCallBatch(requestId, run.Context, run.RunnerId, run.Permissions!, accepted);
    }

    // The run's own connection, or — after the runner's SignalR auto-reconnect gave it a new connection id — any live
    // connection registered for the SAME runner in the SAME tenant (it authenticated with that runner's token).
    private bool IsRunnersConnection(TrackedRun run, string owner, string connectionId)
    {
        if (string.Equals(owner, connectionId, StringComparison.Ordinal)) { return true; }
        return run.RunnerId != Guid.Empty
            && _connections.TryGetValue(connectionId, out var conn)
            && conn.RunnerId == run.RunnerId
            && string.Equals(conn.TenantId, run.Context.TenantId, StringComparison.Ordinal);
    }

    // Counted (agentos.evidence.remote_refused{reason}) and logged — the request id is runner-supplied, so it is scrubbed.
    private RunnerToolCallBatch? Refuse(string reason, string? requestId, int count)
    {
        RemoteRefused.Add(count, new System.Collections.Generic.KeyValuePair<string, object?>("reason", reason));
        if (_logger is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger,
                "Tool-call evidence refused: {Count} call(s) reported for run {RequestId} were not recorded ({Reason}).",
                count, AgentOs.Domain.Tools.SecretRedactor.Bound(AgentOs.SharedKernel.Logging.LogSafe.Scrub(requestId), 80), reason);
        }
        return null;
    }

    public (int ToolCalls, int Denials) ToolCallTotals(string requestId) =>
        !string.IsNullOrEmpty(requestId) && _runs.TryGetValue(requestId, out var run)
            ? (Volatile.Read(ref run.Accepted), Volatile.Read(ref run.Denials))
            : default;

    // Never trust the wire: ids and names bounded, the summary redacted + bounded again (an older or altered runner),
    // a refusal is never "ok", and times stay within the run.
    private static RunnerToolEvent Sanitize(RunnerToolEvent c, DateTimeOffset trackedAt, DateTimeOffset now)
    {
        var id = AgentOs.Domain.Tools.SecretRedactor.Bound(string.IsNullOrWhiteSpace(c.ToolUseId) ? Guid.NewGuid().ToString("N") : c.ToolUseId.Trim(), 128);
        var tool = SafeToolName(c.Tool);
        var summary = AgentOs.Domain.Tools.SecretRedactor.Summary(c.Summary);
        var started = c.StartedUtc < trackedAt.AddMinutes(-1) || c.StartedUtc > now.AddMinutes(1) ? now : c.StartedUtc;
        var duration = Math.Clamp(c.DurationMs, 0, (long)TimeSpan.FromHours(24).TotalMilliseconds);
        return new RunnerToolEvent(id, tool, summary, c.Ok && !c.Denied, c.Denied, started, duration);
    }

    // A tool name as the CLI names it (Bash, mcp__github__create_issue): letters, digits, _ . : - and ( ), ≤ 64.
    // An over-long name is cut first and then checked like any other (the cut never skips the character check).
    internal static string SafeToolName(string? tool)
    {
        var t = (tool ?? string.Empty).Trim();
        if (t.Length > 64) { t = t[..64]; }
        if (t.Length == 0) { return "unknown"; }
        return t.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or ':' or '-' or '(' or ')') ? t : "unknown";
    }

    // The run went to this connection: only it may report the run's tool calls.
    private void BindRun(string requestId, string connectionId)
    {
        if (_runs.TryGetValue(requestId, out var run))
        {
            run.ConnectionId = connectionId;
            if (_connections.TryGetValue(connectionId, out var runner)) { run.RunnerId = runner.RunnerId; }
        }
    }

    private void EndRun(string requestId)
    {
        if (_runs.TryGetValue(requestId, out var run))
        {
            Interlocked.CompareExchange(ref run.EndedAtUnixMs, _time.GetUtcNow().ToUnixTimeMilliseconds(), 0);
        }
    }

    // Ended runs past the grace window, and runs tracked but never dispatched for a day, are dropped.
    private void PruneRuns()
    {
        var now = _time.GetUtcNow();
        foreach (var id in _runs.Where(kv => IsStale(kv.Value, now)).Select(kv => kv.Key))
        {
            _runs.TryRemove(id, out _);
        }
    }

    private static bool IsStale(TrackedRun run, DateTimeOffset now)
    {
        var ended = Interlocked.Read(ref run.EndedAtUnixMs);
        return (ended != 0 && now.ToUnixTimeMilliseconds() - ended > (long)ToolCallGrace.TotalMilliseconds)
            || (ended == 0 && run.ConnectionId is null && now - run.TrackedAt > TimeSpan.FromDays(1));
    }

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

    public void ReportProgress(string connectionId, string requestId, string stepKey)
    {
        // Only for a request someone is waiting on: an unknown id is a late or forged report.
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(stepKey) || stepKey.Length > 64
            || !_pending.TryGetValue(requestId, out var pending)) { return; }
        // Only from the runner it was dispatched to: another runner (even in the same tenant) can't drive its Board steps.
        if (!IsOwner(pending.ConnectionId, pending.Runner, connectionId)) { RefuseResult("progress", requestId); return; }
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
        _pending[request.Id] = new Pending<RemoteExecResult>(tcs, connectionId, RunnerOf(connectionId));

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
                    pending.Task.TrySetCanceled(cancellationToken);
                }
                else
                {
                    pending.Task.TrySetException(new TimeoutException($"Remote runner did not respond within {timeout.TotalSeconds:0}s."));
                }
            }
        });

        BindStaged(request.Id, connectionId);
        BindRun(request.Id, connectionId);
        try
        {
            Dispatched?.Invoke(new RemoteDispatch(request, connectionId));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
            // Its runner's last batch of tool calls may still be on the way (a Stop kills the CLI first).
            EndRun(request.Id);
        }
    }

    public IDisposable RegisterRunner(string connectionId, RunnerConnection runner) => RegisterRunner(connectionId, runner, abort: null);

    public IDisposable RegisterRunner(string connectionId, RunnerConnection runner, Action? abort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(runner);
        if (abort is not null) { _aborts[connectionId] = abort; }
        _connections[connectionId] = runner;
        return new Registration(this, connectionId);
    }

    public IReadOnlyCollection<Guid> ConnectedRunners => _connections.Values.Select(c => c.RunnerId).Distinct().ToList();

    public int Disconnect(Guid runnerId)
    {
        var aborted = 0;
        foreach (var connectionId in _connections.Where(kv => kv.Value.RunnerId == runnerId).Select(kv => kv.Key).ToList())
        {
            // Take the abort before unregistering (which forgets it), then out of routing — nothing new is dispatched to
            // it, even before the transport has closed it.
            _aborts.TryRemove(connectionId, out var abort);
            Unregister(connectionId);
            if (abort is not null)
            {
                try { abort(); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // Already closing: nothing left to abort.
                }
            }
            aborted++;
        }
        if (aborted > 0 && _logger is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(_logger,
                "Runner {RunnerId} revoked: {Count} live connection(s) aborted.", runnerId, aborted);
        }
        return aborted;
    }

    public void Complete(RemoteExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove(result.Id, out var pending))
        {
            pending.Task.TrySetResult(result);
        }
    }

    public bool Complete(string connectionId, RemoteExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrEmpty(result.Id) || !_pending.TryGetValue(result.Id, out var pending)) { return false; }
        if (!IsOwner(pending.ConnectionId, pending.Runner, connectionId)) { RefuseResult("request", result.Id); return false; }
        // Remove only the entry that was checked: a re-dispatch under the same id is not resolved by this answer.
        if (!_pending.TryRemove(new System.Collections.Generic.KeyValuePair<string, Pending<RemoteExecResult>>(result.Id, pending))) { return false; }
        return pending.Task.TrySetResult(result);
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
        _pendingToolCalls[toolCall.ToolCallId] = new Pending<RunnerToolResult>(tcs, connectionId, RunnerOf(connectionId));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pendingToolCalls.TryRemove(toolCall.ToolCallId, out var pending))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    pending.Task.TrySetCanceled(cancellationToken);
                }
                else
                {
                    pending.Task.TrySetException(
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
        if (_pendingToolCalls.TryRemove(result.ToolCallId, out var pending))
        {
            pending.Task.TrySetResult(result);
        }
    }

    public bool CompleteToolCall(string connectionId, RunnerToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrEmpty(result.ToolCallId) || !_pendingToolCalls.TryGetValue(result.ToolCallId, out var pending)) { return false; }
        if (!IsOwner(pending.ConnectionId, pending.Runner, connectionId)) { RefuseResult("tool_call", result.ToolCallId); return false; }
        if (!_pendingToolCalls.TryRemove(new System.Collections.Generic.KeyValuePair<string, Pending<RunnerToolResult>>(result.ToolCallId, pending))) { return false; }
        return pending.Task.TrySetResult(result);
    }

    private RunnerConnection? RunnerOf(string connectionId) => _connections.TryGetValue(connectionId, out var runner) ? runner : null;

    // The connection a pending id was dispatched to — or, after that runner's SignalR auto-reconnect gave it a new
    // connection id, a live connection registered for the SAME runner in the SAME tenant (it authenticated with that
    // runner's token). Any other runner, even one in the same tenant, is refused (GAP-06).
    private bool IsOwner(string owner, RunnerConnection? runner, string? connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) { return false; }
        if (string.Equals(owner, connectionId, StringComparison.Ordinal)) { return true; }
        return runner is not null
            && _connections.TryGetValue(connectionId, out var conn)
            && conn.RunnerId == runner.RunnerId
            && string.Equals(conn.TenantId, runner.TenantId, StringComparison.Ordinal);
    }

    // Counted (agentos.remote.result_refused{kind}) and logged — the id is runner-supplied, so it is scrubbed.
    private void RefuseResult(string kind, string? id)
    {
        ResultRefused.Add(1, new System.Collections.Generic.KeyValuePair<string, object?>("kind", kind));
        if (_logger is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger,
                "Runner {Kind} for {Id} refused: it came from a connection the work was not dispatched to.",
                kind, AgentOs.Domain.Tools.SecretRedactor.Bound(AgentOs.SharedKernel.Logging.LogSafe.Scrub(id), 80));
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
        _aborts.TryRemove(connectionId, out _);
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
