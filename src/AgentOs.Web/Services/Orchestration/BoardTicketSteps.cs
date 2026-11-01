// The production ITicketSteps: each step of a ticket's MAF workflow is the Board's own agent (BoardTaskService — so
// scoped autonomy, metrics and evidence on the PR are the same as step by step) or a GitHub call.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Ticket workflow steps on a board.</summary>
public sealed class BoardTicketSteps(BoardTaskService tasks, IServiceScopeFactory scopes, WorkspaceEntity board, string pat,
    AgentOs.Modules.RemoteAgent.IPlanLimits? planLimits = null) : ITicketSteps
{
    /// <summary>The board's Autopilot member's plan limit, when their computer reported one that hasn't reset yet.</summary>
    public string? PlanLimited(TicketFlow flow) =>
        planLimits?.Current(board.TenantId, board.AutopilotUserId ?? board.CreatedByUserId ?? string.Empty)?.Sentence;

    public Task<TaskRun> CodeAsync(TicketFlow flow, CancellationToken ct) =>
        tasks.BeginStartAsync(flow.TenantId, flow.SessionId, flow.NextChange, ct: ct);

    public Task<TaskRun> ReviewAsync(TicketFlow flow, CancellationToken ct) => tasks.BeginReviewAsync(flow.TenantId, flow.SessionId, ct: ct);

    // A ticket whose flow runs the Design gate gets its screenshots taken by this test run.
    public Task<TaskRun> TestAsync(TicketFlow flow, CancellationToken ct) =>
        tasks.BeginTestAsync(flow.TenantId, flow.SessionId, captureDesign: flow.Plan.Runs(AgentOs.Modules.Pipeline.Agents.TicketStepKind.Design), ct: ct);

    public async Task<DesignStep> DesignAsync(TicketFlow flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        // The owner may already have decided (Approve design / Ask for changes on Board › Tasks): that wins, nothing runs.
        if (await CurrentReviewAsync(flow, ct).ConfigureAwait(false) is { Decision: not null } decided) { return Step(decided); }
        var run = await tasks.BeginDesignAsync(flow.TenantId, flow.SessionId, ct: ct).ConfigureAwait(false);
        if (run.Status != TaskStart.Started)
        {
            return new DesignStep(AgentOs.Domain.Sessions.DesignGateState.Checking, null, $"The Designer agent couldn't start: {run.Status}.");
        }
        await run.Completion.ConfigureAwait(false);
        return await CurrentReviewAsync(flow, ct).ConfigureAwait(false) is { } review
            ? Step(review)
            : new DesignStep(AgentOs.Domain.Sessions.DesignGateState.NeedsPerson, null, "The design check left no verdict.");

        static DesignStep Step(AgentOs.Domain.Sessions.DesignReview r) => new(r.Gate,
            r.Gate == AgentOs.Domain.Sessions.DesignGateState.ChangesRequested ? r.ChangeRequest() : null,
            string.IsNullOrWhiteSpace(r.Summary) ? r.Error : r.Summary);
    }

    // The task's review for its current change (its fix round now).
    private async Task<AgentOs.Domain.Sessions.DesignReview?> CurrentReviewAsync(TicketFlow flow, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var session = (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListForTenantAsync(flow.TenantId, ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == flow.SessionId);
        return session is null || scope.ServiceProvider.GetService<AgentOs.Domain.Sessions.IDesignReviewStore>() is not { } reviews
            ? null
            : await reviews.GetAsync(flow.TenantId, session.Id, session.FixRounds, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TicketChange>> ChangesAsync(TicketFlow flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        using var scope = scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListReposForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false))
            .Select(r => new TicketChange(r.Id, r.Status, r.PrUrl, r.Error, r.DefaultBranch)).ToList();
    }

    public Task<(PrChecksState Checks, PrMergeState Merge)> PrStateAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync(flow, change, async (prs, desc, pr) =>
            (await prs.GetChecksStateAsync(desc, pr, ct).ConfigureAwait(false), await prs.GetMergeStateAsync(desc, pr, ct).ConfigureAwait(false)), ct);

    public Task<string?> FailedChecksAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync<string?>(flow, change, async (prs, desc, pr) => await prs.GetFailedChecksAsync(desc, pr, ct).ConfigureAwait(false), ct);

    public Task UpdateBranchAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync<string?>(flow, change, (prs, desc, pr) => prs.UpdateBranchAsync(desc, pr, ct), ct);

    public Task<string?> MergeAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync<string?>(flow, change, async (prs, desc, pr) =>
        {
            if (await prs.MergeAsync(desc, pr, ct).ConfigureAwait(false) is { } error) { return error; }
            using var scope = scopes.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var repo = (await sessions.ListReposForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false)).First(r => r.Id == change.RepoId);
            await sessions.UpdateRepoRunResultAsync(flow.TenantId, repo.Id, "Merged", repo.BranchName, repo.PrUrl, null, ct).ConfigureAwait(false);
            await sessions.RecomputeSessionStatusForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false);
            return null;
        }, ct);

    // Run a pull-request call for one change inside a scope (the source-control service and the change's repo).
    private async Task<T> WithPrAsync<T>(TicketFlow flow, TicketChange change,
        Func<IPrCreationService, WorkspaceDescriptor, int, Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Pr is not { } pr) { throw new InvalidOperationException("The change has no pull request."); }
        using var scope = scopes.CreateScope();
        var repo = (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListReposForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false))
            .First(r => r.Id == change.RepoId);
        return await call(scope.ServiceProvider.GetRequiredService<IPrCreationService>(),
            BoardTaskService.RepoDescriptor(board, flow.TenantId, repo.Owner, repo.Repo, repo.DefaultBranch, pat), pr).ConfigureAwait(false);
    }
}
