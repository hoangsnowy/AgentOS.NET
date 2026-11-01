// The project orchestrator: for a board with Autopilot on, it does what a person did by hand on the lab — reads the
// board, decides (AutopilotPlanner), and acts through the same BoardTaskService the Board uses. Every decision is
// logged in plain words for the Autopilot tab. One step = one pass over a board; ProjectOrchestratorWorker runs a
// step per autopilot board every minute, and "Check now" runs one on demand. Its bounds (tasks at once, fix rounds,
// retries) are the tenant's Settings › Autopilot, read every pass. When the member's computer reports a Claude/Codex
// plan limit (IPlanLimits), a pass does nothing until the plan resets — no new work, no retries, no fix rounds — and
// then picks up the work the limit cut short.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Integration;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>
/// Orchestrator decisions per board (newest first) for the Autopilot tab. Written through to the database
/// (workspaces.autopilot_events) so the history survives a restart; a bounded in-memory copy serves reads.
/// </summary>
public sealed class OrchestratorLog(IServiceScopeFactory scopes, ILogger<OrchestratorLog> logger)
{
    private const int Keep = 200;
    private readonly ConcurrentDictionary<(string, Guid), LinkedList<(DateTimeOffset At, string Text)>> _entries = new();
    private readonly ConcurrentDictionary<(string, Guid), bool> _loaded = new();

    public void Add(string tenantId, Guid boardId, string text)
    {
        var list = _entries.GetOrAdd((tenantId, boardId), _ => new());
        lock (list)
        {
            list.AddFirst((DateTimeOffset.UtcNow, text));
            while (list.Count > Keep) { list.RemoveLast(); }
        }
        _ = PersistAsync(tenantId, boardId, text);
    }

    /// <summary>The newest decisions; the first read for a board loads its history from the database.</summary>
    public async Task<IReadOnlyList<(DateTimeOffset At, string Text)>> RecentAsync(string tenantId, Guid boardId, int max = 50)
    {
        var key = (tenantId, boardId);
        if (_loaded.TryAdd(key, true))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var stored = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
                    .ListAutopilotEventsAsync(tenantId, boardId, Keep).ConfigureAwait(false);
                var list = _entries.GetOrAdd(key, _ => new());
                lock (list)
                {
                    var merged = Merge(stored.Select(e => (e.AtUtc, e.Text)).ToList(), list.ToList(), Keep);
                    list.Clear();
                    foreach (var e in merged) { list.AddLast(e); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _loaded.TryRemove(key, out _);
                logger.LogWarning(ex, "Couldn't load the Autopilot history of board {Board}", boardId);
            }
        }
        if (!_entries.TryGetValue(key, out var entries)) { return []; }
        lock (entries) { return entries.Take(max).ToList(); }
    }

    /// <summary>
    /// Stored history plus what was added in memory since start, newest first. A decision added before the first read is
    /// in both — stamped separately (here, then by the database), so equal text a few seconds apart is the same line.
    /// </summary>
    internal static List<(DateTimeOffset At, string Text)> Merge(
        IReadOnlyList<(DateTimeOffset At, string Text)> stored, IReadOnlyList<(DateTimeOffset At, string Text)> memory, int keep)
    {
        var byText = stored.ToLookup(e => e.Text, e => e.At, StringComparer.Ordinal);
        var fresh = memory.Where(m => !byText[m.Text].Any(at => (at - m.At).Duration() <= SameLine));
        return [.. stored.Concat(fresh).OrderByDescending(e => e.At).Take(keep)];
    }

    private static readonly TimeSpan SameLine = TimeSpan.FromSeconds(30);

    private async Task PersistAsync(string tenantId, Guid boardId, string text)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().AddAutopilotEventAsync(tenantId, boardId, text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't store an Autopilot decision for board {Board}", boardId);
        }
    }
}

/// <summary>One orchestration pass over a board.</summary>
public sealed partial class ProjectOrchestrator(
    IServiceScopeFactory scopes,
    BoardTaskService tasks,
    TaskStopRegistry stops,
    IRemoteAgentBroker broker,
    OrchestratorLog log,
    ILogger<ProjectOrchestrator> logger,
    AgentOs.Web.Services.Preview.PreviewService previews,
    TicketWorkflowRunner workflows,
    RetrospectiveService retrospectives,
    IPlanLimits planLimits,
    TicketFlowService flows) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => _oneAtATime.Dispose();

    // Tasks seen "Running" with no live run here, by pass: one pass of grace (a run registers right after it is marked).
    private readonly ConcurrentDictionary<Guid, int> _orphanSeen = new();
    // Boards whose testing is held for a running "Try the app" preview (logged once per hold, not every pass).
    private readonly ConcurrentDictionary<Guid, bool> _heldForPreview = new();
    // Boards waiting out their member's plan limit (logged once when the wait starts, once when it ends).
    private readonly PlanLimitPause _planPause = new();
    // Ticket bodies (for dependencies) change rarely; read each at most every 10 minutes.
    private readonly ConcurrentDictionary<(Guid, int), (DateTimeOffset At, IReadOnlyList<int> Deps)> _deps = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public static AutopilotMode ParseMode(string? value) =>
        Enum.TryParse<AutopilotMode>(value, ignoreCase: true, out var mode) ? mode : AutopilotMode.Off;

    /// <summary>Run one pass over <paramref name="board"/>; returns the decisions taken (also logged).</summary>
    public async Task<IReadOnlyList<AutopilotAction>> StepAsync(WorkspaceEntity board, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        var mode = ParseMode(board.Autopilot);
        if (mode == AutopilotMode.Off) { return []; }
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var member = board.AutopilotUserId ?? board.CreatedByUserId ?? string.Empty;
            // The member's AI plan hit its limit: nothing starts, nothing is retried, until it resets.
            if (WaitsForPlan(board, member)) { return []; }
            using var identity = AmbientIdentity.Push(board.TenantId, member);
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var tenant = board.TenantId;
            var pat = await sp.GetRequiredService<IAppConfigStore>().GetForTenantAsync(tenant, board.CredentialRef, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pat))
            {
                log.Add(tenant, board.Id, "Paused: GitHub access has expired — reconnect the project.");
                return [];
            }
            var sessionsRepo = sp.GetRequiredService<ISessionRepository>();
            var prs = sp.GetRequiredService<IPrCreationService>();
            var limits = await AutopilotSettings.ReadLimitsAsync(sp.GetRequiredService<ISettings>(), tenant, ct).ConfigureAwait(false);
            var boardDesc = new BoardDescriptor(board.Id, tenant, board.Kind, board.ProjectOwner, board.ProjectScope,
                board.ProjectNumber, board.ProjectNodeId, pat, board.Project);
            var tickets = (await sp.GetRequiredService<IBoardTicketService>().ListTicketsAsync(boardDesc, ct).ConfigureAwait(false)).Items
                .Where(t => t.Kind != BoardTicketKind.DraftIssue && t.Number is not null && t.RepoOwner is not null && t.RepoName is not null)
                .ToList();
            var sessions = (await sessionsRepo.ListForTenantAsync(tenant, ct).ConfigureAwait(false)).Where(s => s.WorkspaceId == board.Id).ToList();
            var repos = (await sessionsRepo.ListAllReposForTenantAsync(tenant, ct).ConfigureAwait(false)).ToLookup(r => r.SessionId);
            if (await RecoverOrphansAsync(board, sessions, repos, sessionsRepo, ct).ConfigureAwait(false))
            {
                sessions = (await sessionsRepo.ListForTenantAsync(tenant, ct).ConfigureAwait(false)).Where(s => s.WorkspaceId == board.Id).ToList();
                repos = (await sessionsRepo.ListAllReposForTenantAsync(tenant, ct).ConfigureAwait(false)).ToLookup(r => r.SessionId);
            }
            await CloseSupersededAsync(board, pat, sessions, repos, sp, ct).ConfigureAwait(false);

            // Proposed changes: merged ones are recorded; open ones get their check state. Every task carries its
            // persisted fix rounds — the one counter the caps read, whichever engine sent the change back — and its
            // ticket flow (the version it started with, for its ticket's labels), so the rules skip what the flow skips.
            var labels = TicketLabels(tickets);
            var taskFlows = new Dictionary<int, EffectiveTicketFlow>();
            var planTasks = new List<PlanTask>();
            foreach (var s in sessions.Where(s => s.Status is "Failed" && !workflows.IsRunning(tenant, s.Id) && CutShortByPlanLimit(s, repos)))
            {
                planTasks.Add(new PlanTask(s.Id, s.IssueNumber ?? 0, "Failed", [], s.FixRounds) { CutShortByPlanLimit = true });
            }
            foreach (var s in sessions.Where(s => s.Status is "Running" or "Done"))
            {
                // Its MAF workflow drives it: a running slot, and no step-by-step moves on it.
                if (workflows.IsRunning(tenant, s.Id))
                {
                    planTasks.Add(new PlanTask(s.Id, s.IssueNumber ?? 0, "Running", [], s.FixRounds));
                    continue;
                }
                var open = new List<PlanChange>();
                foreach (var r in repos[s.Id].Where(r => r.Status is "Done" or "Reviewing" or "Reviewed" or "ChangesRequested" or "Testing" or "Accepted" or "Rejected"
                    && PrNumber(r.PrUrl) is not null))
                {
                    var desc = BoardTaskService.RepoDescriptor(board, tenant, r.Owner, r.Repo, r.DefaultBranch, pat);
                    var number = PrNumber(r.PrUrl)!.Value;
                    if (s.Status == "Done" && await prs.GetStateAsync(desc, number, ct).ConfigureAwait(false) == PrState.Merged)
                    {
                        await sessionsRepo.UpdateRepoRunResultAsync(tenant, r.Id, "Merged", r.BranchName, r.PrUrl, null, ct).ConfigureAwait(false);
                        await sessionsRepo.RecomputeSessionStatusForTenantAsync(tenant, s.Id, ct).ConfigureAwait(false);
                        continue;
                    }
                    var checks = s.Status == "Done" ? await prs.GetChecksStateAsync(desc, number, ct).ConfigureAwait(false) : PrChecksState.Pending;
                    var report = checks == PrChecksState.Failed ? await prs.GetFailedChecksAsync(desc, number, ct).ConfigureAwait(false) : null;
                    var testing = r.Status switch
                    {
                        "Testing" => ChangeTesting.Testing,
                        "Accepted" => ChangeTesting.Accepted,
                        "Rejected" => ChangeTesting.Rejected,
                        _ => ChangeTesting.Untested,
                    };
                    // A change waits for review only while it is fresh ("Done"); once tested it was reviewed (or predates review).
                    var review = r.Status switch
                    {
                        "Done" => ChangeReview.NotReviewed,
                        "Reviewing" => ChangeReview.Reviewing,
                        "ChangesRequested" => ChangeReview.ChangesRequested,
                        _ => ChangeReview.Approved,
                    };
                    var merge = s.Status == "Done" ? await prs.GetMergeStateAsync(desc, number, ct).ConfigureAwait(false) : PrMergeState.Unknown;
                    var findings = testing == ChangeTesting.Rejected || review == ChangeReview.ChangesRequested ? r.Error : null;
                    open.Add(new PlanChange(r.Id, number, checks, report, testing, findings, merge, r.DefaultBranch, review));
                }
                var flow = await TaskFlowAsync(board, s.TicketFlowVersion, taskFlows, ct).ConfigureAwait(false);
                var plan = flow.Definition.Resolve(LabelsOf(s, labels));
                // The Design gate reads the review of the task's CURRENT change (its fix round): a fix round starts afresh.
                var reviews = plan.Runs(TicketStepKind.Design) ? sp.GetService<AgentOs.Domain.Sessions.IDesignReviewStore>() : null;
                var design = reviews is null ? null : await reviews.GetAsync(tenant, s.Id, s.FixRounds, ct).ConfigureAwait(false);
                // A check a restart cut short reads "Checking" with nothing running: it waits for the Designer again.
                if (design is { Status: AgentOs.Domain.Sessions.DesignReviewStatus.Checking, Decision: null } && reviews is not null && !stops.IsRunning(tenant, s.Id))
                {
                    design = await reviews.SaveAsync(design with { Status = AgentOs.Domain.Sessions.DesignReviewStatus.Pending, UpdatedAtUtc = DateTimeOffset.UtcNow }, ct)
                        .ConfigureAwait(false);
                }
                planTasks.Add(new PlanTask(s.Id, s.IssueNumber ?? 0, s.Status, open, s.FixRounds)
                {
                    Flow = plan,
                    Design = design?.Gate ?? AgentOs.Domain.Sessions.DesignGateState.NotChecked,
                    DesignFindings = design?.Gate == AgentOs.Domain.Sessions.DesignGateState.ChangesRequested ? design.ChangeRequest() : null,
                });
            }

            // Tickets: a ticket with any task that isn't closed is the person's or already handled — never doubled.
            var liveIssues = sessions.Where(s => s.Status != "Closed").Select(s => (s.IssueNumber, s.RepoOwner, s.RepoName)).ToHashSet();
            var resolver = sp.GetRequiredService<ISourceProviderResolver>();
            resolver.TryResolve(board.Kind, out var provider);
            var planTickets = new List<PlanTicket>();
            foreach (var t in tickets)
            {
                var open = !string.Equals(t.State, "CLOSED", StringComparison.OrdinalIgnoreCase);
                var ai = t.Labels.Contains(StandardLabels.AiReady);
                var live = liveIssues.Contains((t.Number, t.RepoOwner, t.RepoName));
                var deps = open && ai && !live ? await DependenciesAsync(provider, board, tenant, pat, t, ct).ConfigureAwait(false) : [];
                var priority = t.Labels.Contains("p0") ? 0 : t.Labels.Contains("p1") ? 1 : 2;
                planTickets.Add(new PlanTicket(t.Number!.Value, open, ai, priority, deps, live));
            }

            // The Testing agent needs the member's computer (a browser, containers, the repo's toolchain).
            var runners = await sp.GetRequiredService<IRunnerRepository>().ListForTenantAsync(tenant, ct).ConfigureAwait(false);
            var canTest = runners.Any(r => r.OwnerUserId == member && broker.IsConnected(r.Id));
            // Someone is trying the app on a computer: its ports are taken, and a test run would start a second copy of
            // the same app on them. Testing waits; coding and merging go on.
            if (previews.IsActiveOnBoard(tenant, board.Id))
            {
                canTest = false;
                if (_heldForPreview.TryAdd(board.Id, true)) { log.Add(tenant, board.Id, "Testing waits while the app is being tried — it resumes when the preview stops."); }
            }
            else
            {
                _heldForPreview.TryRemove(board.Id, out _);
            }
            var actions = await DecideAsync(board, mode, member, pat, tickets, planTickets, planTasks, sessions, repos, canTest, limits, sp, ct).ConfigureAwait(false);
            // Deciding may itself have hit the limit (the Orchestrator agent answers on the member's computer).
            if (WaitsForPlan(board, member)) { return []; }
            foreach (var action in actions)
            {
                await ExecuteAsync(action, board, member, pat, tickets, sp, ct).ConfigureAwait(false);
            }
            return actions;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Autopilot pass failed for board {Board}", board.Id);
            log.Add(board.TenantId, board.Id, $"Couldn't read the project this time ({ex.Message}); will try again.");
            return [];
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task ExecuteAsync(AutopilotAction action, WorkspaceEntity board, string member, string pat,
        IReadOnlyList<BoardTicket> tickets, IServiceProvider sp, CancellationToken ct)
    {
        var tenant = board.TenantId;
        switch (action)
        {
            case AutopilotAction.StartTicket start:
            {
                var ticket = tickets.First(t => t.Number == start.Number);
                var curated = (await sp.GetRequiredService<IWorkspaceRepository>().ListReposForTenantAsync(tenant, board.Id, ct).ConfigureAwait(false))
                    .FirstOrDefault(r => r.Owner == ticket.RepoOwner && r.Repo == ticket.RepoName);
                // The member's own computer when it is connected (their AI plan), else the servers.
                var runners = await sp.GetRequiredService<IRunnerRepository>().ListForTenantAsync(tenant, ct).ConfigureAwait(false);
                var onComputer = runners.Any(r => r.OwnerUserId == member && broker.IsConnected(r.Id));
                var session = await tasks.CreateAsync(tenant, member, board.Id, ticket.Number, $"#{ticket.Number} {ticket.Title}", ticket.ItemNodeId,
                    ticket.Kind.ToString(), [new BoardTaskService.TaskRepo(ticket.RepoOwner!, ticket.RepoName!, curated?.DefaultBranch ?? "main", curated?.Id)],
                    onComputer, ticketType: ticket.Labels.FirstOrDefault(l => l.StartsWith("type:", StringComparison.OrdinalIgnoreCase)), ct: ct).ConfigureAwait(false);
                // The ticket runs the board's ticket flow as it is now — snapshotted on the task, so a later change of the
                // flow never changes a ticket under way (MAF workflow or, after a restart, the step-by-step passes).
                var flow = await flows.ResolveAsync(tenant, board.Id, ct).ConfigureAwait(false);
                LogRefused(board, flow);
                await sp.GetRequiredService<ISessionRepository>().SetTicketFlowVersionForTenantAsync(tenant, session.Id, flow.Version, ct).ConfigureAwait(false);
                var plan = flow.Definition.Resolve(ticket.Labels);
                var flowNote = flow.Version == 0 && plan.Skipped.Count == 0 ? "" : $" · ticket flow v{flow.Version}: {plan.Summary}";
                // A MAF workflow runs the ticket's whole agent flow (TicketWorkflow); step by step, the passes drive it.
                var engine = await sp.GetRequiredService<AgentOs.Domain.Configuration.ISettings>()
                    .GetAsync(AgentOs.Modules.Pipeline.Agents.AutopilotSettings.TicketEngineKey, tenant, ct).ConfigureAwait(false);
                if (AgentOs.Modules.Pipeline.Agents.AutopilotSettings.UsesMafWorkflow(engine.Value)
                    && workflows.Start(board, pat, session.Id, start.Number, ParseMode(board.Autopilot) == AutopilotMode.BuildAndMerge, plan, flow.Version))
                {
                    log.Add(tenant, board.Id, start.Why + (onComputer ? " (on your computer)" : " (on our servers)") + " — a MAF workflow runs it" + flowNote + ".");
                    break;
                }
                var started = await tasks.StartAsync(tenant, session.Id, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, started == TaskStart.Started
                    ? start.Why + (onComputer ? " (on your computer)" : " (on our servers)") + flowNote
                    : $"Couldn't start #{start.Number}: {started}.");
                break;
            }
            case AutopilotAction.ResumeTask resume:
            {
                // A plain restart: no change request, so no fix round is counted.
                var resumed = await tasks.StartAsync(tenant, resume.SessionId, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, resumed == TaskStart.Started ? resume.Why : $"Couldn't pick #{resume.IssueNumber} up again: {resumed}.");
                break;
            }
            case AutopilotAction.FixChecks fix:
                // The round is counted (persisted) by the restart itself: BoardTaskService increments FixRounds.
                var restarted = await tasks.StartAsync(tenant, fix.SessionId,
                    "The pull request's automated checks failed. Fix the cause (not the test, unless the test itself is wrong) so they pass:\n\n" + fix.Report,
                    ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, restarted == TaskStart.Started ? fix.Why : $"Couldn't send #{fix.IssueNumber} back: {restarted}.");
                break;
            case AutopilotAction.UpdateBranch update:
            {
                var sessions = sp.GetRequiredService<ISessionRepository>();
                var repo = (await sessions.ListReposForTenantAsync(tenant, update.SessionId, ct).ConfigureAwait(false)).First(r => r.Id == update.RepoId);
                var error = await sp.GetRequiredService<IPrCreationService>().UpdateBranchAsync(
                    BoardTaskService.RepoDescriptor(board, tenant, repo.Owner, repo.Repo, repo.DefaultBranch, pat), update.PrNumber, ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, error is null ? update.Why : $"Couldn't update #{update.IssueNumber}'s branch: {error}");
                break;
            }
            case AutopilotAction.RunReview review:
            {
                var reviewed = await tasks.ReviewAsync(tenant, review.SessionId, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, reviewed == TaskStart.Started ? review.Why : $"Couldn't review #{review.IssueNumber}: {reviewed}.");
                break;
            }
            case AutopilotAction.RunTests test:
            {
                // A ticket whose flow runs the Design gate gets its screenshots taken by this test run.
                var tested = await tasks.TestAsync(tenant, test.SessionId, captureDesign: await RunsDesignAsync(board, test.SessionId, tickets, sp, ct).ConfigureAwait(false),
                    ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, tested == TaskStart.Started ? test.Why : $"Couldn't test #{test.IssueNumber}: {tested}.");
                break;
            }
            case AutopilotAction.RunDesign check:
            {
                var checking = await tasks.DesignAsync(tenant, check.SessionId, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, checking == TaskStart.Started ? check.Why : $"Couldn't check #{check.IssueNumber}'s design: {checking}.");
                break;
            }
            case AutopilotAction.Merge merge:
            {
                var sessions = sp.GetRequiredService<ISessionRepository>();
                var repo = (await sessions.ListReposForTenantAsync(tenant, merge.SessionId, ct).ConfigureAwait(false)).First(r => r.Id == merge.RepoId);
                var desc = BoardTaskService.RepoDescriptor(board, tenant, repo.Owner, repo.Repo, repo.DefaultBranch, pat);
                var error = await sp.GetRequiredService<IPrCreationService>().MergeAsync(desc, merge.PrNumber, ct).ConfigureAwait(false);
                if (error is null)
                {
                    await sessions.UpdateRepoRunResultAsync(tenant, repo.Id, "Merged", repo.BranchName, repo.PrUrl, null, ct).ConfigureAwait(false);
                    await sessions.RecomputeSessionStatusForTenantAsync(tenant, merge.SessionId, ct).ConfigureAwait(false);
                    log.Add(tenant, board.Id, merge.Why);
                    // Self-improvement: what this ticket taught becomes the project's practices.
                    retrospectives.AfterMerge(tenant, member, board.Id, $"{repo.Owner}/{repo.Repo}", $"#{merge.IssueNumber}");
                }
                else
                {
                    log.Add(tenant, board.Id, $"Couldn't merge #{merge.IssueNumber} (PR #{merge.PrNumber}): {error}");
                }
                break;
            }
            case AutopilotAction.NeedsPerson ask:
            {
                var sessions = sp.GetRequiredService<ISessionRepository>();
                foreach (var r in (await sessions.ListReposForTenantAsync(tenant, ask.SessionId, ct).ConfigureAwait(false)).Where(r => HoldsAProposedChange(r.Status)))
                {
                    await sessions.UpdateRepoRunResultAsync(tenant, r.Id, "Blocked", r.BranchName, r.PrUrl, ask.Reason, ct).ConfigureAwait(false);
                }
                await sessions.RecomputeSessionStatusForTenantAsync(tenant, ask.SessionId, ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, ask.Why);
                break;
            }
        }
    }

    // A repo whose proposed change is waiting on Autopilot — opened, reviewed or tested (the statuses a session counts as
    // "Done", minus Merged). NeedsPerson blocks exactly these: it used to block only "Done", so a change the Reviewer had
    // asked to fix ("ChangesRequested") stayed Done, never reached the Orchestrator agent's stuck list, and the same
    // "Stopped fixing … It needs you." line was logged on every pass.
    internal static bool HoldsAProposedChange(string? status) =>
        status is "Done" or "Accepted" or "Rejected" or "Reviewed" or "ChangesRequested";

    // A task still "Running" whose run is not alive in this process lost it — the host restarted mid-way (a lab update,
    // a deploy). It would otherwise read "Working…" forever. A test that was cut short goes back to untested (it runs
    // again); coding that was cut short is restarted from scratch (Try again). Returns whether anything changed.
    private async Task<bool> RecoverOrphansAsync(WorkspaceEntity board, IReadOnlyList<RemoteSessionEntity> sessions,
        ILookup<Guid, SessionRepoEntity> repos, ISessionRepository sessionsRepo, CancellationToken ct)
    {
        var tenant = board.TenantId;
        var changed = false;
        foreach (var s in sessions.Where(s => s.Status == "Running"))
        {
            if (stops.IsRunning(tenant, s.Id) || workflows.IsRunning(tenant, s.Id)) { _orphanSeen.TryRemove(s.Id, out _); continue; }
            if (_orphanSeen.AddOrUpdate(s.Id, 1, (_, n) => n + 1) < 2) { continue; }
            _orphanSeen.TryRemove(s.Id, out _);
            var children = repos[s.Id].ToList();
            var wasCoding = children.Any(r => r.Status is "Pending" or "Running");
            foreach (var r in children.Where(r => r.Status is "Testing" or "Reviewing"))
            {
                await sessionsRepo.UpdateRepoRunResultAsync(tenant, r.Id, "Done", r.BranchName, r.PrUrl, null, ct).ConfigureAwait(false);
            }
            await sessionsRepo.RecomputeSessionStatusForTenantAsync(tenant, s.Id, ct).ConfigureAwait(false);
            if (wasCoding)
            {
                var restarted = await tasks.StartAsync(tenant, s.Id, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, restarted == TaskStart.Started
                    ? $"#{s.IssueNumber} was cut short by a restart — started it again."
                    : $"#{s.IssueNumber} was cut short by a restart and couldn't restart: {restarted}.");
            }
            else
            {
                log.Add(tenant, board.Id, $"#{s.IssueNumber}'s review or test was cut short by a restart — it will run again.");
            }
            changed = true;
        }
        return changed;
    }

    // True while the board's member waits out a plan limit (and logs the start and the end of the wait once each).
    private bool WaitsForPlan(WorkspaceEntity board, string member)
    {
        var (paused, line) = _planPause.Observe(board.Id, planLimits.Current(board.TenantId, member));
        if (line is not null) { log.Add(board.TenantId, board.Id, line); }
        return paused;
    }

    // A failed task whose run the member's plan limit cut short (its error is the CLI's limit notice) — nothing failed.
    internal static bool CutShortByPlanLimit(RemoteSessionEntity session, ILookup<Guid, SessionRepoEntity> repos) =>
        PlanLimitDetector.IsPlanLimit(session.Error) || repos[session.Id].Any(r => r.Status == "Failed" && PlanLimitDetector.IsPlanLimit(r.Error));

    private async Task<IReadOnlyList<int>> DependenciesAsync(ISourceProvider? provider, WorkspaceEntity board, string tenant, string pat, BoardTicket t, CancellationToken ct)
    {
        if (t.Number is not { } number) { return []; }
        var key = (board.Id, number);
        if (_deps.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10)) { return cached.Deps; }
        if (provider is null) { return []; }
        string? body;
        try
        {
            body = await provider.ReadTicketBodyAsync(BoardTaskService.RepoDescriptor(board, tenant, t.RepoOwner!, t.RepoName!, "main", pat), number, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
        {
            return cached.Deps ?? [];
        }
        var deps = TicketDependencies.Parse(body);
        _deps[key] = (DateTimeOffset.UtcNow, deps);
        return deps;
    }

    // Each ticket's labels by (repo, number) — what a task's ticket flow is resolved for.
    internal static Dictionary<(string Owner, string Repo, int Number), IReadOnlyList<string>> TicketLabels(IReadOnlyList<BoardTicket> tickets)
    {
        var map = new Dictionary<(string, string, int), IReadOnlyList<string>>();
        foreach (var t in tickets.Where(t => t.Number.HasValue && t.RepoOwner is not null && t.RepoName is not null))
        {
            map.TryAdd((t.RepoOwner!, t.RepoName!, t.Number.GetValueOrDefault()), t.Labels);
        }
        return map;
    }

    // A task's ticket labels now; without its ticket on the board, the type label it was created with.
    internal static IReadOnlyList<string> LabelsOf(RemoteSessionEntity task, IReadOnlyDictionary<(string Owner, string Repo, int Number), IReadOnlyList<string>> labels) =>
        task is { IssueNumber: { } n, RepoOwner: { } o, RepoName: { } r } && labels.TryGetValue((o, r, n), out var l) ? l
        : task.TicketType is { Length: > 0 } type ? [type] : [];

    // True when the task's ticket flow (the version it started with, for its ticket's labels) runs the Design gate — its
    // test run then takes the screenshots the Designer checks.
    private async Task<bool> RunsDesignAsync(WorkspaceEntity board, Guid sessionId, IReadOnlyList<BoardTicket> tickets, IServiceProvider sp, CancellationToken ct)
    {
        var session = (await sp.GetRequiredService<ISessionRepository>().ListForTenantAsync(board.TenantId, ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == sessionId);
        if (session is null) { return false; }
        var flow = await flows.ForTaskAsync(board.TenantId, board.Id, session.TicketFlowVersion, ct).ConfigureAwait(false);
        return flow.Definition.Resolve(LabelsOf(session, TicketLabels(tickets))).Runs(TicketStepKind.Design);
    }

    // The flow a task runs (the version it started with), once per version per pass.
    private async Task<EffectiveTicketFlow> TaskFlowAsync(WorkspaceEntity board, int? snapshot, Dictionary<int, EffectiveTicketFlow> memo, CancellationToken ct)
    {
        var key = snapshot ?? -1;
        if (memo.TryGetValue(key, out var known)) { return known; }
        var flow = await flows.ForTaskAsync(board.TenantId, board.Id, snapshot, ct).ConfigureAwait(false);
        LogRefused(board, flow);
        memo[key] = flow;
        return flow;
    }

    private readonly ConcurrentDictionary<(Guid Board, string Why), bool> _refusedLogged = new();

    // A stored flow that breaks a safety rule runs as the built-in one: said once per board in the Autopilot log.
    private void LogRefused(WorkspaceEntity board, EffectiveTicketFlow flow)
    {
        if (flow.Refused is { } why && _refusedLogged.TryAdd((board.Id, why), true)) { log.Add(board.TenantId, board.Id, why); }
    }

    private static int? PrNumber(string? url)
    {
        if (string.IsNullOrEmpty(url)) { return null; }
        var i = url.LastIndexOf("/pull/", StringComparison.Ordinal);
        return i >= 0 && int.TryParse(url.AsSpan(i + "/pull/".Length).TrimEnd('/'), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>Runs a pass over every autopilot board each minute. A board's failure never stops the others.</summary>
public sealed class ProjectOrchestratorWorker(IServiceScopeFactory scopes, ProjectOrchestrator orchestrator, ILogger<ProjectOrchestratorWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                IReadOnlyList<WorkspaceEntity> boards;
                using (var scope = scopes.CreateScope())
                {
                    boards = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().ListAutopilotAsync(stoppingToken).ConfigureAwait(false);
                }
                foreach (var board in boards)
                {
                    await orchestrator.StepAsync(board, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Autopilot tick failed; retrying next tick.");
            }
        }
    }
}
