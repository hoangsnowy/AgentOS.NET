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
public sealed class BoardTicketSteps(BoardTaskService tasks, IServiceScopeFactory scopes, WorkspaceEntity board, string pat) : ITicketSteps
{
    public Task<TaskRun> CodeAsync(TicketFlow flow, CancellationToken ct) =>
        tasks.BeginStartAsync(flow.TenantId, flow.SessionId, flow.NextChange, ct: ct);

    public Task<TaskRun> ReviewAsync(TicketFlow flow, CancellationToken ct) => tasks.BeginReviewAsync(flow.TenantId, flow.SessionId, ct: ct);

    public Task<TaskRun> TestAsync(TicketFlow flow, CancellationToken ct) => tasks.BeginTestAsync(flow.TenantId, flow.SessionId, ct: ct);

    public async Task<IReadOnlyList<TicketChange>> ChangesAsync(TicketFlow flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        using var scope = scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISessionRepository>().ListReposForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false))
            .Select(r => new TicketChange(r.Id, r.Status, r.PrUrl, r.Error, r.DefaultBranch)).ToList();
    }

    public Task<(PrChecksState Checks, PrMergeState Merge)> PrStateAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync(flow, change, ct, async (prs, desc, pr) =>
            (await prs.GetChecksStateAsync(desc, pr, ct).ConfigureAwait(false), await prs.GetMergeStateAsync(desc, pr, ct).ConfigureAwait(false)));

    public Task<string?> FailedChecksAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync(flow, change, ct, (prs, desc, pr) => prs.GetFailedChecksAsync(desc, pr, ct));

    public Task UpdateBranchAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync<string?>(flow, change, ct, (prs, desc, pr) => prs.UpdateBranchAsync(desc, pr, ct));

    public Task<string?> MergeAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
        WithPrAsync(flow, change, ct, async (prs, desc, pr) =>
        {
            if (await prs.MergeAsync(desc, pr, ct).ConfigureAwait(false) is { } error) { return error; }
            using var scope = scopes.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var repo = (await sessions.ListReposForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false)).First(r => r.Id == change.RepoId);
            await sessions.UpdateRepoRunResultAsync(flow.TenantId, repo.Id, "Merged", repo.BranchName, repo.PrUrl, null, ct).ConfigureAwait(false);
            await sessions.RecomputeSessionStatusForTenantAsync(flow.TenantId, flow.SessionId, ct).ConfigureAwait(false);
            return (string?)null;
        });

    // Run a pull-request call for one change inside a scope (the source-control service and the change's repo).
    private async Task<T> WithPrAsync<T>(TicketFlow flow, TicketChange change, CancellationToken ct,
        Func<IPrCreationService, WorkspaceDescriptor, int, Task<T>> call)
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
