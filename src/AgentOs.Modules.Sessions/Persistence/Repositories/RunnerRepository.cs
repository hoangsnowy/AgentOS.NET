// Runner repository. Reads run as Dapper SQL over a raw connection (Dapper-only — no EF read fallback);
// EF owns writes. The real repo is registered only with a connection string (else NullRunnerRepository),
// so the factory is always present in production; a null factory throws. TENANT SAFETY: the EF global
// query filter does not apply on the Dapper path, so every read carries an explicit
// `WHERE "TenantId" = @tenantId`. Columns are PascalCase (quoted); table is sessions.runners.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

internal sealed class RunnerRepository : IRunnerRepository
{
    private readonly SessionsDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly INpgsqlConnectionFactory? _conn;

    public RunnerRepository(SessionsDbContext db, ITenantContext tenant, INpgsqlConnectionFactory? conn = null)
    {
        _db = db;
        _tenant = tenant;
        _conn = conn;
    }

    private INpgsqlConnectionFactory Conn =>
        _conn ?? throw new InvalidOperationException("RunnerRepository reads require a database connection (Dapper-only; no EF fallback).");

    public async Task<IReadOnlyList<RunnerEntity>> ListAsync(int limit = Page.DefaultLimit, int offset = 0, CancellationToken ct = default)
    {
        var lim = Page.ClampLimit(limit);
        var off = Page.ClampOffset(offset);
        const string sql = """
            SELECT * FROM sessions.runners
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            LIMIT @lim OFFSET @off
            """;
        return await QueryRunnersAsync(_tenant.TenantId, sql, new { tenantId = _tenant.TenantId, lim, off }, ct).ConfigureAwait(false);
    }

    public async Task<RunnerEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """SELECT * FROM sessions.runners WHERE "Id" = @id AND "TenantId" = @tenantId""";
        // RLS (ADR-0012 stage 2): Dapper reads switch to the restricted role like the EF context — a plain connection ran as
        // the login user, which an owner login (Azure) sees nothing through once "no tenant" means nothing.
        await using var conn = await Conn.OpenForTenantAsync(_tenant.TenantId, ct).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<RunnerEntity>(
            new CommandDefinition(sql, new { id, tenantId = _tenant.TenantId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task AddAsync(RunnerEntity runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.TenantId = _tenant.TenantId;
        _db.Runners.Add(runner);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        var row = await _db.Runners.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == _tenant.TenantId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = status;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RunnerEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM sessions.runners
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            """;
        return await QueryRunnersAsync(tenantId, sql, new { tenantId }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RunnerEntity>> QueryRunnersAsync(string tenantId, string sql, object parms, CancellationToken ct)
    {
        await using var conn = await Conn.OpenForTenantAsync(tenantId, ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RunnerEntity>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task AddForTenantAsync(RunnerEntity runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        // Tenant-scoped write seam (caller stamps TenantId). Fail loudly on an empty value rather than persist
        // an orphan row that no tenant's query filter matches — a silent cross-tenant-isolation hole.
        if (string.IsNullOrWhiteSpace(runner.TenantId))
        {
            throw new InvalidOperationException("RunnerEntity.TenantId must be set before AddForTenantAsync.");
        }
        _db.Runners.Add(runner);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SetStatusForTenantAsync(string tenantId, Guid id, string status, CancellationToken ct = default)
    {
        var row = await _db.Runners
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = status;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
