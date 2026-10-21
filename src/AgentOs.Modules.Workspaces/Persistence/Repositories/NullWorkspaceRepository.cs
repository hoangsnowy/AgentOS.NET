// No-op workspace repository used when no database is configured (CI / local stateless runs). It
// satisfies the DI contract so the host boots, but persists nothing and returns empty history.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Persistence;

namespace AgentOs.Modules.Workspaces.Persistence.Repositories;

internal sealed class NullWorkspaceRepository : IWorkspaceRepository
{
    public Task<bool> SetAutopilotForTenantAsync(string tenantId, Guid id, string mode, string? userId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task AddAutopilotEventAsync(string tenantId, Guid boardId, string text, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AutopilotEventEntity>> ListAutopilotEventsAsync(string tenantId, Guid boardId, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AutopilotEventEntity>>(Array.Empty<AutopilotEventEntity>());

    public Task<IReadOnlyList<WorkspaceEntity>> ListAutopilotAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceEntity>>(Array.Empty<WorkspaceEntity>());

    public Task<IReadOnlyList<WorkspaceEntity>> ListAsync(int limit = Page.DefaultLimit, int offset = 0, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceEntity>>(Array.Empty<WorkspaceEntity>());

    public Task<WorkspaceEntity?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<WorkspaceEntity?>(null);

    public Task AddAsync(WorkspaceEntity workspace, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<WorkspaceEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceEntity>>(Array.Empty<WorkspaceEntity>());

    public Task<WorkspaceEntity?> GetForTenantAsync(string tenantId, Guid id, CancellationToken ct = default)
        => Task.FromResult<WorkspaceEntity?>(null);

    public Task<bool> RemoveForTenantAsync(string tenantId, Guid id, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task AddForTenantAsync(WorkspaceEntity workspace, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<WorkspaceRepoEntity>> ListReposForTenantAsync(string tenantId, Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceRepoEntity>>(Array.Empty<WorkspaceRepoEntity>());

    public Task<IReadOnlyList<WorkspaceRepoEntity>> ListAllReposForTenantAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceRepoEntity>>(Array.Empty<WorkspaceRepoEntity>());

    public Task AddRepoForTenantAsync(WorkspaceRepoEntity repo, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RemoveRepoForTenantAsync(string tenantId, Guid repoId, CancellationToken ct = default)
        => Task.FromResult(false);
}
