// Approval policies (E1.5) on the Board: reads a project's policy (Settings › Autopilot › Approvals, per project line),
// checks a gate for a task (FAIL CLOSED — ApprovalRules), asks for an approval, and records an approver's decision on
// Board › Tasks: only the approver or admin role may decide, every decision is audited (who, when, what, note), an
// approved pull-request gate opens the held pull requests, and a rejection stops the task (its open changes are blocked
// with the approver's reason — a person decides what next). Singleton; every call is tenant-explicit (a circuit or a
// background pass has no ambient tenant).

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Governance;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Governance;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.Approvals;

/// <summary>Who decides on Board › Tasks (read from the circuit's AuthenticationState).</summary>
/// <param name="UserId">Their stable id (audited).</param>
/// <param name="Label">How they are shown ("alice").</param>
/// <param name="CanApprove">They hold the approver or admin role.</param>
public sealed record ApprovalActor(string UserId, string Label, bool CanApprove);

/// <summary>The outcome of a decision.</summary>
public sealed record ApprovalOutcome(bool Ok, string? Error, ApprovalRequest? Request)
{
    internal static ApprovalOutcome Fail(string error, ApprovalRequest? request = null) => new(false, error, request);
}

/// <summary>Approval gates of Board tasks.</summary>
public sealed class ApprovalGateService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ApprovalGateService> logger)
{
    /// <summary>Who an automatic request is made by.</summary>
    public const string Autopilot = "Autopilot";

    /// <summary>Raised (with the tenant) when a request is made or decided — Board › Tasks refreshes. Called off the circuit.</summary>
    public event Action<string>? Changed;

    /// <summary>The policy of <paramref name="board"/>, read now.</summary>
    public async Task<ApprovalPolicy> PolicyAsync(WorkspaceEntity board, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<ISettings>() is not { } settings) { return ApprovalPolicy.None; }
        return await ApprovalSettings.ReadPolicyAsync(settings, board.TenantId, board.Name, board.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Where <paramref name="gate"/> stands for a task at fix round <paramref name="fixRounds"/> — read only.</summary>
    public async Task<ApprovalCheck> CheckAsync(WorkspaceEntity board, ApprovalPolicy policy, Guid sessionId, ApprovalGate gate, int fixRounds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Requires(gate)) { return ApprovalCheck.Proceed; }
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
        var current = await store.GetAsync(board.TenantId, sessionId, gate, ApprovalRequest.RunFor(gate, fixRounds), ct).ConfigureAwait(false);
        // Approved needs no count; otherwise know whether anybody could approve (fail closed on a known zero).
        var approvers = current?.Status == ApprovalStatus.Approved ? null : await ApproversAsync(scope.ServiceProvider, board.TenantId, ct).ConfigureAwait(false);
        return ApprovalRules.Evaluate(policy, gate, current, approvers);
    }

    /// <summary>Ask for an approval of <paramref name="gate"/> for <paramref name="session"/>'s current change (idempotent).</summary>
    public async Task<ApprovalRequest> RequestAsync(WorkspaceEntity board, RemoteSessionEntity session, ApprovalGate gate, string? link,
        string requestedBy = Autopilot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(session);
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
        var stored = await store.RequestAsync(ApprovalRequest.New(board.TenantId, board.Id, session.Id, session.IssueNumber ?? 0, gate,
            session.FixRounds, session.Title, link, requestedBy, clock.GetUtcNow()), ct).ConfigureAwait(false);
        Notify(board.TenantId);
        return stored;
    }

    /// <summary>Check <paramref name="gate"/> and, when it was never asked, ask now — the result then reads "wait".</summary>
    public async Task<ApprovalCheck> EnsureAsync(WorkspaceEntity board, RemoteSessionEntity session, ApprovalGate gate, string? link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var policy = await PolicyAsync(board, ct).ConfigureAwait(false);
        var check = await CheckAsync(board, policy, session.Id, gate, session.FixRounds, ct).ConfigureAwait(false);
        if (check.Verdict != ApprovalVerdict.Request) { return check; }
        var asked = await RequestAsync(board, session, gate, link, Autopilot, ct).ConfigureAwait(false);
        return new ApprovalCheck(ApprovalVerdict.Wait, $"Waiting for an approver to approve {ApprovalPolicy.Describe(gate)}.", asked);
    }

    /// <summary>
    /// An approver decides request <paramref name="approvalId"/>. Refused unless <paramref name="actor"/> may approve; a
    /// request already decided is left as it is (and reported). Audited. An approved pull-request gate opens the held pull
    /// requests now; a rejection blocks the task's open changes with the approver's reason.
    /// </summary>
    public async Task<ApprovalOutcome> DecideAsync(string tenantId, Guid approvalId, bool approve, string? note, ApprovalActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.CanApprove) { return ApprovalOutcome.Fail("Only an approver or an admin can approve or reject."); }
        if (!approve && string.IsNullOrWhiteSpace(note)) { return ApprovalOutcome.Fail("Say why you reject it — the reason goes to whoever picks the task up."); }
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IApprovalStore>();
        var before = await store.FindAsync(tenantId, approvalId, ct).ConfigureAwait(false);
        if (before is null) { return ApprovalOutcome.Fail("That approval request no longer exists."); }
        if (before.Status != ApprovalStatus.Pending)
        {
            return ApprovalOutcome.Fail($"Already {before.Status.ToString().ToLowerInvariant()} by {before.DecidedBy}.", before);
        }
        var decided = await store.DecideAsync(tenantId, approvalId,
            new ApprovalDecision(approve, string.IsNullOrWhiteSpace(actor.Label) ? actor.UserId : actor.Label, note, clock.GetUtcNow()), ct).ConfigureAwait(false);
        if (decided is null) { return ApprovalOutcome.Fail("That approval request no longer exists."); }
        if (decided.DecidedAtUtc != null && decided.Status != (approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected))
        {
            // Someone else decided first.
            return ApprovalOutcome.Fail($"Already {decided.Status.ToString().ToLowerInvariant()} by {decided.DecidedBy}.", decided);
        }

        if (sp.GetService<IAuditLog>() is { } audit)
        {
            await audit.WriteAsync(new AuditEntry(Guid.NewGuid(), tenantId, actor.UserId, AuditActions.ApprovalDecided,
                AuditTarget(decided), null, null, decided.DecidedAtUtc ?? clock.GetUtcNow()), ct).ConfigureAwait(false);
        }
        logger.LogInformation("Approval {Gate} of task {Session} {Status} by {User}", decided.Gate, decided.SessionId, decided.Status, actor.UserId);

        string? follow = null;
        if (decided.Status == ApprovalStatus.Rejected)
        {
            await BlockAsync(sp, decided, ct).ConfigureAwait(false);
        }
        else if (decided.Gate == ApprovalGate.PullRequest)
        {
            follow = await sp.GetRequiredService<BoardTasks.BoardTaskService>().OpenApprovedPullRequestsAsync(tenantId, decided.SessionId, ct).ConfigureAwait(false);
        }
        Notify(tenantId);
        return new ApprovalOutcome(follow is null, follow, decided);
    }

    /// <summary>"#12 merge approved: looks good" — what the audit row says (the note clipped).</summary>
    internal static string AuditTarget(ApprovalRequest r)
    {
        var note = string.IsNullOrWhiteSpace(r.Note) ? "" : ": " + (r.Note.Length <= 200 ? r.Note : r.Note[..200] + "…");
        return string.Create(CultureInfo.InvariantCulture,
            $"#{r.IssueNumber} {ApprovalPolicy.Label(r.Gate).ToLowerInvariant()} {r.Status.ToString().ToLowerInvariant()} (task {r.SessionId}, change {r.Run}){note}");
    }

    // A rejection stops the task: every change that isn't merged is blocked with the approver's reason, so Autopilot never
    // moves it on its own; a person decides (Try again / Ask for changes / Close).
    private static async Task BlockAsync(IServiceProvider sp, ApprovalRequest rejected, CancellationToken ct)
    {
        var sessions = sp.GetRequiredService<ISessionRepository>();
        foreach (var r in (await sessions.ListReposForTenantAsync(rejected.TenantId, rejected.SessionId, ct).ConfigureAwait(false))
                     .Where(r => r.Status is not ("Merged" or "Closed")))
        {
            await sessions.UpdateRepoRunResultAsync(rejected.TenantId, r.Id, "Blocked", r.BranchName, r.PrUrl, rejected.RejectionReason, ct).ConfigureAwait(false);
        }
        await sessions.RecomputeSessionStatusForTenantAsync(rejected.TenantId, rejected.SessionId, ct).ConfigureAwait(false);
    }

    private static async Task<int?> ApproversAsync(IServiceProvider sp, string tenantId, CancellationToken ct) =>
        sp.GetService<IApproverDirectory>() is { } directory ? await directory.CountApproversAsync(tenantId, ct).ConfigureAwait(false) : null;

    /// <summary>The branch comparison a person reads before a pull request exists (GitHub); null elsewhere.</summary>
    public static string? CompareLink(SourceProviderKind kind, string owner, string repo, string baseBranch, string branch) =>
        kind == SourceProviderKind.GitHub && !string.IsNullOrWhiteSpace(branch)
            ? $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/compare/{Path(baseBranch)}...{Path(branch)}"
            : null;

    // A branch name keeps its slashes in a URL path ("agentos/123"); every segment is escaped.
    private static string Path(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

    private void Notify(string tenantId)
    {
        try
        {
            Changed?.Invoke(tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "An approvals view failed to refresh");
        }
    }
}
