// RBAC v2 (E1.2) — per-board (workspace) membership. IWorkspaceMembershipStore persists who holds which role on which
// board; IWorkspaceAccess turns a user's realm roles + those rows into the permissions they hold on each board via the
// single matrix (PermissionMatrix.ForWorkspace). Every method is tenant-explicit (a Blazor circuit has no
// ITenantContext); the EF store adds an explicit TenantId predicate on top of the global filter, and RLS binds the
// connection to the ambient tenant as the second wall.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Workspaces.Access;

/// <summary>Persistence of board memberships, tenant-explicit.</summary>
public interface IWorkspaceMembershipStore
{
    /// <summary>Every membership in the tenant (all boards), for computing a user's access to many boards in one read.</summary>
    Task<IReadOnlyList<WorkspaceMember>> ListForTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The memberships of one board.</summary>
    Task<IReadOnlyList<WorkspaceMember>> ListAsync(string tenantId, Guid workspaceId, CancellationToken ct = default);

    /// <summary>Grant <paramref name="role"/> on a board (insert or replace). Throws <see cref="ArgumentException"/> for a
    /// role a membership can't carry.</summary>
    Task<WorkspaceMember> SetAsync(string tenantId, Guid workspaceId, string userId, string role, string? actorUserId, CancellationToken ct = default);

    /// <summary>Remove a membership. False when there was none.</summary>
    Task<bool> RemoveAsync(string tenantId, Guid workspaceId, string userId, CancellationToken ct = default);
}

internal sealed class WorkspaceAccess(IWorkspaceMembershipStore store) : IWorkspaceAccess
{
    public async Task<WorkspaceAccessMap> ForUserAsync(string tenantId, IEnumerable<string> roles, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var materialised = roles as IReadOnlyCollection<string> ?? [.. roles];
        // An empty tenant (a token with no tenant claim) reads nothing; callers refuse an empty tenant before this.
        var rows = string.IsNullOrWhiteSpace(tenantId) ? [] : await store.ListForTenantAsync(tenantId, ct).ConfigureAwait(false);
        return WorkspaceAccessMap.Build(materialised, userId, rows);
    }
}

internal static class WorkspaceMemberMapping
{
    public static WorkspaceMember ToRecord(WorkspaceMemberEntity e) => new(e.WorkspaceId, e.UserId, e.Role, e.AddedByUserId, e.AddedAtUtc);

    public static void Validate(string tenantId, string userId, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!UserRoles.IsWorkspaceAssignable(role))
        {
            throw new ArgumentException(
                $"A board role must be one of: {string.Join(", ", UserRoles.WorkspaceAssignable)}.", nameof(role));
        }
        if (userId.Length > 64) { throw new ArgumentException("User id is too long.", nameof(userId)); }
    }
}

internal sealed class EfWorkspaceMembershipStore(WorkspacesDbContext db, TimeProvider clock) : IWorkspaceMembershipStore
{
    public async Task<IReadOnlyList<WorkspaceMember>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rows = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(WorkspaceMemberMapping.ToRecord).ToList();
    }

    public async Task<IReadOnlyList<WorkspaceMember>> ListAsync(string tenantId, Guid workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rows = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId)
            .OrderBy(x => x.AddedAtUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(WorkspaceMemberMapping.ToRecord).ToList();
    }

    public async Task<WorkspaceMember> SetAsync(string tenantId, Guid workspaceId, string userId, string role, string? actorUserId, CancellationToken ct = default)
    {
        WorkspaceMemberMapping.Validate(tenantId, userId, role);
        // The board must exist IN THIS TENANT — never attach a membership to another tenant's board id.
        var boardExists = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(w => w.TenantId == tenantId && w.Id == workspaceId, ct).ConfigureAwait(false);
        if (!boardExists) { throw new KeyNotFoundException("No such board in this workspace."); }

        var row = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.UserId == userId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            var now = clock.GetUtcNow();
            row = new WorkspaceMemberEntity
            {
                Id = Guid.CreateVersion7(now), TenantId = tenantId, WorkspaceId = workspaceId, UserId = userId,
                Role = role, AddedByUserId = actorUserId is { Length: <= 64 } ? actorUserId : null, AddedAtUtc = now,
            };
            db.WorkspaceMembers.Add(row);
        }
        else
        {
            row.Role = role;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return WorkspaceMemberMapping.ToRecord(row);
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid workspaceId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var row = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.UserId == userId, ct)
            .ConfigureAwait(false);
        if (row is null) { return false; }
        db.WorkspaceMembers.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Process-local store for the stateless standalone Web (no connection string) — same semantics.</summary>
internal sealed class InMemoryWorkspaceMembershipStore(TimeProvider clock) : IWorkspaceMembershipStore
{
    private readonly ConcurrentDictionary<(string Tenant, Guid Board, string User), WorkspaceMember> _rows = new();

    public Task<IReadOnlyList<WorkspaceMember>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        IReadOnlyList<WorkspaceMember> rows = [.. _rows.Where(kv => kv.Key.Tenant == tenantId).Select(kv => kv.Value)];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<WorkspaceMember>> ListAsync(string tenantId, Guid workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        IReadOnlyList<WorkspaceMember> rows =
            [.. _rows.Where(kv => kv.Key.Tenant == tenantId && kv.Key.Board == workspaceId).Select(kv => kv.Value).OrderBy(m => m.AddedAtUtc)];
        return Task.FromResult(rows);
    }

    public Task<WorkspaceMember> SetAsync(string tenantId, Guid workspaceId, string userId, string role, string? actorUserId, CancellationToken ct = default)
    {
        WorkspaceMemberMapping.Validate(tenantId, userId, role);
        var row = _rows.AddOrUpdate((tenantId, workspaceId, userId),
            _ => new WorkspaceMember(workspaceId, userId, role, actorUserId, clock.GetUtcNow()),
            (_, existing) => existing with { Role = role });
        return Task.FromResult(row);
    }

    public Task<bool> RemoveAsync(string tenantId, Guid workspaceId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Task.FromResult(_rows.TryRemove((tenantId, workspaceId, userId), out _));
    }
}
