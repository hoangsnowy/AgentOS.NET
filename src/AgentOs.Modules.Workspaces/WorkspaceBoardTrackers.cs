// E1.8 — IBoardTrackers over the workspace registry: a board's tracker binding (the row's Tracker JSON) with its token
// (encrypted AppConfig, TrackerBinding.TokenKey). Tenant-explicit: Autopilot passes run without a request context.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Workspaces.Persistence;

namespace AgentOs.Modules.Workspaces;

internal sealed class WorkspaceBoardTrackers(IWorkspaceRepository workspaces, IAppConfigStore credentials) : IBoardTrackers
{
    public async Task<TrackerConnection?> GetAsync(string tenantId, Guid boardId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || boardId == Guid.Empty) { return null; }
        var board = await workspaces.GetForTenantAsync(tenantId, boardId, ct).ConfigureAwait(false);
        if (board?.ReadTracker() is not { } binding) { return null; }
        var token = await credentials.GetForTenantAsync(tenantId, TrackerBinding.TokenKey(board.Id), ct).ConfigureAwait(false);
        // A missing token still routes to the tracker (which then refuses with a clear 401) — never silently to the code
        // host, whose board this isn't.
        return new TrackerConnection(binding, token ?? string.Empty);
    }
}
