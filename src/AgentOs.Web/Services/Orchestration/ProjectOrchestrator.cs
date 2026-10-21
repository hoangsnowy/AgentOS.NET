// The project orchestrator: for a board with Autopilot on, it does what a person did by hand on the lab — reads the
// board, decides (AutopilotPlanner), and acts through the same BoardTaskService the Board uses. Every decision is
// logged in plain words for the Autopilot tab. One step = one pass over a board; ProjectOrchestratorWorker runs a
// step per autopilot board every minute, and "Check now" runs one on demand.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Integration;
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

/// <summary>Recent orchestrator decisions per board (newest first), for the Autopilot tab. Bounded, in memory.</summary>
public sealed class OrchestratorLog
{
    private const int Keep = 200;
    private readonly ConcurrentDictionary<(string, Guid), LinkedList<(DateTimeOffset At, string Text)>> _entries = new();

    public void Add(string tenantId, Guid boardId, string text)
    {
        var list = _entries.GetOrAdd((tenantId, boardId), _ => new());
        lock (list)
        {
            list.AddFirst((DateTimeOffset.UtcNow, text));
            while (list.Count > Keep) { list.RemoveLast(); }
        }
    }

    public IReadOnlyList<(DateTimeOffset At, string Text)> Recent(string tenantId, Guid boardId, int max = 50)
    {
        if (!_entries.TryGetValue((tenantId, boardId), out var list)) { return []; }
        lock (list) { return list.Take(max).ToList(); }
    }
}

/// <summary>One orchestration pass over a board.</summary>
public sealed partial class ProjectOrchestrator(
    IServiceScopeFactory scopes,
    BoardTaskService tasks,
    IRemoteAgentBroker broker,
    OrchestratorLog log,
    ILogger<ProjectOrchestrator> logger)
{
    // Automatic fix rounds per task (in memory: a restart grants at most another MaxFixRounds — bounded either way).
    private readonly ConcurrentDictionary<Guid, int> _fixRounds = new();
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
            var boardDesc = new BoardDescriptor(board.Id, tenant, board.Kind, board.ProjectOwner, board.ProjectScope,
                board.ProjectNumber, board.ProjectNodeId, pat, board.Project);
            var tickets = (await sp.GetRequiredService<IBoardTicketService>().ListTicketsAsync(boardDesc, ct).ConfigureAwait(false)).Items
                .Where(t => t.Kind != BoardTicketKind.DraftIssue && t.Number is not null && t.RepoOwner is not null && t.RepoName is not null)
                .ToList();
            var sessions = (await sessionsRepo.ListForTenantAsync(tenant, ct).ConfigureAwait(false)).Where(s => s.WorkspaceId == board.Id).ToList();
            var repos = (await sessionsRepo.ListAllReposForTenantAsync(tenant, ct).ConfigureAwait(false)).ToLookup(r => r.SessionId);

            // Proposed changes: merged ones are recorded; open ones get their check state.
            var planTasks = new List<PlanTask>();
            foreach (var s in sessions.Where(s => s.Status is "Running" or "Done"))
            {
                var open = new List<PlanChange>();
                foreach (var r in repos[s.Id].Where(r => r.Status == "Done" && PrNumber(r.PrUrl) is not null))
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
                    open.Add(new PlanChange(r.Id, number, checks, report));
                }
                planTasks.Add(new PlanTask(s.Id, s.IssueNumber ?? 0, s.Status, open, _fixRounds.GetValueOrDefault(s.Id)));
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

            var actions = AutopilotPlanner.Decide(mode, planTickets, planTasks);
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
                var runners = await sp.GetRequiredService<AgentOs.Modules.Sessions.Persistence.IRunnerRepository>().ListForTenantAsync(tenant, ct).ConfigureAwait(false);
                var onComputer = runners.Any(r => r.OwnerUserId == member && broker.IsConnected(r.Id));
                var session = await tasks.CreateAsync(tenant, member, board.Id, ticket.Number, $"#{ticket.Number} {ticket.Title}", ticket.ItemNodeId,
                    ticket.Kind.ToString(), [new BoardTaskService.TaskRepo(ticket.RepoOwner!, ticket.RepoName!, curated?.DefaultBranch ?? "main", curated?.Id)],
                    onComputer, ticketType: ticket.Labels.FirstOrDefault(l => l.StartsWith("type:", StringComparison.OrdinalIgnoreCase)), ct: ct).ConfigureAwait(false);
                var started = await tasks.StartAsync(tenant, session.Id, ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, started == TaskStart.Started
                    ? start.Why + (onComputer ? " (on your computer)" : " (on our servers)")
                    : $"Couldn't start #{start.Number}: {started}.");
                break;
            }
            case AutopilotAction.FixChecks fix:
                _fixRounds[fix.SessionId] = fix.Round;
                var restarted = await tasks.StartAsync(tenant, fix.SessionId,
                    "The pull request's automated checks failed. Fix the cause (not the test, unless the test itself is wrong) so they pass:\n\n" + fix.Report,
                    ct: ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, restarted == TaskStart.Started ? fix.Why : $"Couldn't send #{fix.IssueNumber} back: {restarted}.");
                break;
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
                    _fixRounds.TryRemove(merge.SessionId, out _);
                    log.Add(tenant, board.Id, merge.Why);
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
                foreach (var r in (await sessions.ListReposForTenantAsync(tenant, ask.SessionId, ct).ConfigureAwait(false)).Where(r => r.Status == "Done"))
                {
                    await sessions.UpdateRepoRunResultAsync(tenant, r.Id, "Blocked", r.BranchName, r.PrUrl, ask.Reason, ct).ConfigureAwait(false);
                }
                await sessions.RecomputeSessionStatusForTenantAsync(tenant, ask.SessionId, ct).ConfigureAwait(false);
                log.Add(tenant, board.Id, ask.Why);
                break;
            }
        }
    }

    private async Task<IReadOnlyList<int>> DependenciesAsync(ISourceProvider? provider, WorkspaceEntity board, string tenant, string pat, BoardTicket t, CancellationToken ct)
    {
        var key = (board.Id, t.Number!.Value);
        if (_deps.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10)) { return cached.Deps; }
        if (provider is null) { return []; }
        string? body;
        try
        {
            body = await provider.ReadTicketBodyAsync(BoardTaskService.RepoDescriptor(board, tenant, t.RepoOwner!, t.RepoName!, "main", pat), t.Number.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Octokit.ApiException or InvalidOperationException or TaskCanceledException)
        {
            return cached.Deps ?? [];
        }
        var deps = TicketDependencies.Parse(body);
        _deps[key] = (DateTimeOffset.UtcNow, deps);
        return deps;
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
