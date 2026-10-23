// Hosts the per-ticket MAF workflows (TicketWorkflow) in the background — one per ticket the Autopilot Orchestrator
// starts. While a ticket's workflow runs, Autopilot leaves that task alone (it counts as a running slot); when it ends,
// its trail goes to the Autopilot log and anything unfinished is handed back: out of rounds → marked for a person (the
// Orchestrator agent may retry it with guidance or ask); an agent that couldn't run → the step-by-step path continues
// from where the task stands. A restart drops the in-memory runs; the same step-by-step path picks those tasks up.
// Each flow reads the tenant's Settings › Autopilot bounds when it starts (fix rounds, checks timeout and poll), and
// its round counter starts from the task's persisted fix rounds — one count across engines and restarts.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Runs ticket workflows. Singleton.</summary>
public sealed class TicketWorkflowRunner(IServiceScopeFactory scopes, BoardTaskService tasks, OrchestratorLog log, ILogger<TicketWorkflowRunner> logger,
    RetrospectiveService retrospectives, IPlanLimits planLimits)
{
    private readonly ConcurrentDictionary<(string Tenant, Guid Session), Task> _running = new();

    /// <summary>True while the task's workflow runs.</summary>
    public bool IsRunning(string tenantId, Guid sessionId) => _running.ContainsKey((tenantId, sessionId));

    /// <summary>Start the ticket's workflow; false when one already runs for it.</summary>
    public bool Start(WorkspaceEntity board, string pat, Guid sessionId, int issueNumber, bool mayMerge)
    {
        ArgumentNullException.ThrowIfNull(board);
        var key = (board.TenantId, sessionId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(key, gate.Task)) { return false; }
        _ = Task.Run(async () =>
        {
            try
            {
                var (start, limits) = await SeedAsync(board, sessionId, issueNumber, mayMerge).ConfigureAwait(false);
                var flow = await TicketWorkflow.RunAsync(new BoardTicketSteps(tasks, scopes, board, pat, planLimits),
                        start, limits.ChecksTimeout, limits.ChecksPoll, CancellationToken.None)
                    .ConfigureAwait(false);
                await FinishAsync(board, flow).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ticket workflow for #{Issue} failed", issueNumber);
                log.Add(board.TenantId, board.Id, $"#{issueNumber} · MAF workflow stopped unexpectedly ({ex.Message}) — Autopilot continues step by step.");
            }
            finally
            {
                _running.TryRemove(key, out _);
                gate.TrySetResult();
            }
        });
        return true;
    }

    // The tenant's bounds now, and the flow's round counter from the task's persisted fix rounds.
    private async Task<(TicketFlow Flow, AutopilotLimits Limits)> SeedAsync(WorkspaceEntity board, Guid sessionId, int issueNumber, bool mayMerge)
    {
        using var scope = scopes.CreateScope();
        var limits = await AutopilotSettings.ReadLimitsAsync(scope.ServiceProvider.GetRequiredService<ISettings>(), board.TenantId).ConfigureAwait(false);
        var task = (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListForTenantAsync(board.TenantId).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == sessionId);
        return (Seed(board.TenantId, board.Id, sessionId, issueNumber, mayMerge, task?.FixRounds ?? 0, limits), limits);
    }

    /// <summary>A ticket's flow, its rounds counted from <paramref name="persistedFixRounds"/> under <paramref name="limits"/>.</summary>
    internal static TicketFlow Seed(string tenantId, Guid boardId, Guid sessionId, int issueNumber, bool mayMerge, int persistedFixRounds, AutopilotLimits limits) =>
        new(tenantId, boardId, sessionId, issueNumber, mayMerge) { Round = Math.Max(0, persistedFixRounds), MaxRounds = limits.MaxFixRounds };

    private async Task FinishAsync(WorkspaceEntity board, TicketFlow flow)
    {
        var steps = flow.Trail.Count == 0 ? "" : " (" + string.Join(" → ", flow.Trail) + ")";
        var line = flow.Stage switch
        {
            TicketStage.Merged => $"#{flow.IssueNumber} · MAF workflow: merged{steps}.",
            TicketStage.ReadyToMerge => $"#{flow.IssueNumber} · MAF workflow: reviewed and tested — ready for you to merge{steps}.",
            TicketStage.NeedsPerson => $"#{flow.IssueNumber} · MAF workflow: {flow.Reason}{steps}",
            TicketStage.PlanLimited => $"#{flow.IssueNumber} · MAF workflow paused for your computer's plan limit{steps} — Autopilot picks it up after the reset.",
            _ => $"#{flow.IssueNumber} · MAF workflow paused: {flow.Reason}{steps} — Autopilot continues step by step.",
        };
        log.Add(board.TenantId, board.Id, line);
        if (flow.Stage == TicketStage.Merged)
        {
            // Self-improvement: what this ticket taught becomes the project's practices.
            using var s = scopes.CreateScope();
            var task = (await s.ServiceProvider.GetRequiredService<ISessionRepository>().ListForTenantAsync(board.TenantId).ConfigureAwait(false))
                .FirstOrDefault(t => t.Id == flow.SessionId);
            if (task is { RepoOwner: { } owner, RepoName: { } name })
            {
                retrospectives.AfterMerge(board.TenantId, task.MemberUserId, board.Id, $"{owner}/{name}", task.Title);
            }
            return;
        }
        if (flow.Stage != TicketStage.NeedsPerson) { return; }
        // Same as the rules' "needs a person": the open changes are blocked, which the Orchestrator agent sees as stuck.
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        foreach (var r in (await sessions.ListReposForTenantAsync(board.TenantId, flow.SessionId).ConfigureAwait(false))
                     .Where(r => r.Status is "Done" or "Reviewed" or "ChangesRequested" or "Accepted" or "Rejected"))
        {
            await sessions.UpdateRepoRunResultAsync(board.TenantId, r.Id, "Blocked", r.BranchName, r.PrUrl, flow.Reason).ConfigureAwait(false);
        }
        await sessions.RecomputeSessionStatusForTenantAsync(board.TenantId, flow.SessionId).ConfigureAwait(false);
    }
}
