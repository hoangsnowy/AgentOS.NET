// Workspace repository. Reads run as hand-tuned Dapper SQL over a raw connection (Dapper-only — no EF
// read fallback). EF still owns writes + change-tracking. The real repo is registered ONLY when a
// connection string is configured (else the module swaps in NullWorkspaceRepository), so the factory is
// always present in production; a null factory throws rather than silently degrading. TENANT SAFETY:
// the EF global query filter is NOT in play on the Dapper path, so EVERY read carries an explicit
// `WHERE "TenantId" = @tenantId` — ambient reads resolve the tenant from ITenantContext, the *ForTenant
// overloads take it as a parameter. Columns are PascalCase (quoted); tables are snake_case.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Workspaces.Persistence.Repositories;

internal sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly WorkspacesDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly INpgsqlConnectionFactory? _conn;

    public WorkspaceRepository(WorkspacesDbContext db, ITenantContext tenant, INpgsqlConnectionFactory? conn = null)
    {
        _db = db;
        _tenant = tenant;
        _conn = conn;
    }

    private INpgsqlConnectionFactory Conn =>
        _conn ?? throw new InvalidOperationException("WorkspaceRepository reads require a database connection (Dapper-only; no EF fallback).");

    public async Task<IReadOnlyList<WorkspaceEntity>> ListAsync(int limit = Page.DefaultLimit, int offset = 0, CancellationToken ct = default)
    {
        var lim = Page.ClampLimit(limit);
        var off = Page.ClampOffset(offset);
        const string sql = """
            SELECT * FROM workspaces.workspaces
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            LIMIT @lim OFFSET @off
            """;
        return await QueryListAsync(sql, new { tenantId = _tenant.TenantId, lim, off }, ct).ConfigureAwait(false);
    }

    public async Task<WorkspaceEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """SELECT * FROM workspaces.workspaces WHERE "Id" = @id AND "TenantId" = @tenantId""";
        return await QuerySingleAsync(sql, new { id, tenantId = _tenant.TenantId }, ct).ConfigureAwait(false);
    }

    public async Task AddAsync(WorkspaceEntity workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.TenantId = _tenant.TenantId;
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM workspaces.workspaces
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            """;
        return await QueryListAsync(sql, new { tenantId }, ct).ConfigureAwait(false);
    }

    public async Task<WorkspaceEntity?> GetForTenantAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        const string sql = """SELECT * FROM workspaces.workspaces WHERE "TenantId" = @tenantId AND "Id" = @id""";
        return await QuerySingleAsync(sql, new { tenantId, id }, ct).ConfigureAwait(false);
    }

    public async Task<bool> SetAutopilotForTenantAsync(string tenantId, Guid id, string mode, string? userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var row = await _db.Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == id && w.TenantId == tenantId, ct).ConfigureAwait(false);
        if (row is null) { return false; }
        row.Autopilot = mode;
        row.AutopilotUserId = mode == "Off" ? null : userId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<WorkspaceEntity>> ListAutopilotAsync(CancellationToken ct = default)
    {
        // Cross-tenant on purpose: the project orchestrator is a platform worker. Explicit bypass, not an empty tenant.
        using var platform = RowLevelSecurity.Bypass();
        return await _db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Autopilot != "Off")
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddForTenantAsync(WorkspaceEntity workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        // Tenant-scoped write seam (caller stamps TenantId). Fail loudly on an empty value rather than persist
        // an orphan row that no tenant's query filter matches — a silent cross-tenant-isolation hole.
        if (string.IsNullOrWhiteSpace(workspace.TenantId))
        {
            throw new InvalidOperationException("WorkspaceEntity.TenantId must be set before AddForTenantAsync.");
        }
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == id && w.TenantId == _tenant.TenantId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        _db.Workspaces.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveForTenantAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        var row = await _db.Workspaces
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Id == id, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        _db.Workspaces.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Repos under a board ──────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkspaceRepoEntity>> ListReposForTenantAsync(string tenantId, Guid workspaceId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM workspaces.workspace_repos
            WHERE "TenantId" = @tenantId AND "WorkspaceId" = @workspaceId
            ORDER BY "AddedAtUtc"
            """;
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<WorkspaceRepoEntity>(
            new CommandDefinition(sql, new { tenantId, workspaceId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WorkspaceRepoEntity>> ListAllReposForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        // One round-trip for every board's repos; IX_workspace_repos_TenantId_WorkspaceId serves the
        // filter + board grouping. The caller buckets by WorkspaceId in memory (no 1+N over boards).
        const string sql = """
            SELECT * FROM workspaces.workspace_repos
            WHERE "TenantId" = @tenantId
            ORDER BY "WorkspaceId", "AddedAtUtc"
            """;
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<WorkspaceRepoEntity>(
            new CommandDefinition(sql, new { tenantId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task AddRepoForTenantAsync(WorkspaceRepoEntity repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(repo.TenantId))
        {
            throw new InvalidOperationException("WorkspaceRepoEntity.TenantId must be set before AddRepoForTenantAsync.");
        }
        _db.WorkspaceRepos.Add(repo);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveRepoForTenantAsync(string tenantId, Guid repoId, CancellationToken ct = default)
    {
        var row = await _db.WorkspaceRepos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == repoId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        _db.WorkspaceRepos.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Dapper read helpers (tenant-scoped by the caller's SQL) ───────────────────────────────────
    private async Task<IReadOnlyList<WorkspaceEntity>> QueryListAsync(string sql, object parms, CancellationToken ct)
    {
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<WorkspaceEntity>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<WorkspaceEntity?> QuerySingleAsync(string sql, object parms, CancellationToken ct)
    {
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<WorkspaceEntity>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
    }
}
