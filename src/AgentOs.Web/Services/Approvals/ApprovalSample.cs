// Approval policies (E1.5) — sample approval requests for the standalone desktop. `dotnet run --project src/AgentOs.Web`
// has no database and therefore no tasks, so the Approvals panel on Board › Tasks could never be seen (or end-to-end
// tested) there. In Development with dev auto-login and no database ONLY (like DesignGateSample), this seeds clearly-
// labelled samples into the in-memory approval store: a merge and a plan waiting for an approver (Approve / Reject work
// on them for real — the in-memory store, the audit sink) and a pull request an approver already rejected, with its
// reason. Never registered anywhere else.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentOs.Web.Services.Approvals;

/// <summary>Seeds the standalone desktop's sample approval requests (Development + dev auto-login + no database only).</summary>
public sealed class ApprovalSample(IServiceScopeFactory scopes) : IHostedService
{
    /// <summary>The sample project (board) id.</summary>
    public static readonly Guid BoardId = new("5a3c1e00-de51-4e00-9a7e-0000000000b1");

    /// <summary>Who the samples say asked.</summary>
    public const string RequestedBy = "Autopilot (sample)";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<IApprovalStore>() is not { } store) { return; }
        var tenant = ITenantContext.DefaultTenantId;
        var now = DateTimeOffset.UtcNow;
        await store.RequestAsync(ApprovalRequest.New(tenant, BoardId, new Guid("5a3c1e00-de51-4e00-9a7e-0000000000a7"), 21, ApprovalGate.Merge, 0,
            "#21 Sample: Add a dark mode toggle (standalone Development only)", "https://github.com/octo-org/shop/pull/42", RequestedBy, now.AddMinutes(-12)),
            cancellationToken).ConfigureAwait(false);
        await store.RequestAsync(ApprovalRequest.New(tenant, BoardId, new Guid("5a3c1e00-de51-4e00-9a7e-0000000000a8"), 22, ApprovalGate.Plan, 0,
            "#22 Sample: Export orders as CSV (standalone Development only)", "https://github.com/octo-org/shop/issues/22", RequestedBy, now.AddMinutes(-4)),
            cancellationToken).ConfigureAwait(false);
        var rejected = await store.RequestAsync(ApprovalRequest.New(tenant, BoardId, new Guid("5a3c1e00-de51-4e00-9a7e-0000000000a9"), 23, ApprovalGate.PullRequest, 0,
            "#23 Sample: Drop the legacy users table (standalone Development only)",
            "https://github.com/octo-org/shop/compare/main...agentos/issue-23", RequestedBy, now.AddMinutes(-40)), cancellationToken).ConfigureAwait(false);
        await store.DecideAsync(tenant, rejected.Id, new ApprovalDecision(false, "sample approver",
            "Not before the data migration is reviewed — the table still has readers.", now.AddMinutes(-30)), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
