// Session repository. Reads run as Dapper SQL over a raw connection (Dapper-only — no EF read fallback);
// EF owns writes + status updates. The real repo is registered only with a connection string (else the
// no-op repo), so the factory is always present in production; a null factory throws. TENANT SAFETY: the
// EF global query filter does not apply on the Dapper path, so every read carries an explicit
// `WHERE "TenantId" = @tenantId`. Columns are PascalCase (quoted); tables are sessions.sessions + session_repos.

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

internal sealed class SessionRepository : ISessionRepository
{
    private readonly SessionsDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly INpgsqlConnectionFactory? _conn;

    public SessionRepository(SessionsDbContext db, ITenantContext tenant, INpgsqlConnectionFactory? conn = null)
    {
        _db = db;
        _tenant = tenant;
        _conn = conn;
    }

    private INpgsqlConnectionFactory Conn =>
        _conn ?? throw new InvalidOperationException("SessionRepository reads require a database connection (Dapper-only; no EF fallback).");

    public async Task<IReadOnlyList<RemoteSessionEntity>> ListAsync(int limit = Page.DefaultLimit, int offset = 0, CancellationToken ct = default)
    {
        var lim = Page.ClampLimit(limit);
        var off = Page.ClampOffset(offset);
        const string sql = """
            SELECT * FROM sessions.sessions
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            LIMIT @lim OFFSET @off
            """;
        return await QuerySessionsAsync(sql, new { tenantId = _tenant.TenantId, lim, off }, ct).ConfigureAwait(false);
    }

    public async Task<RemoteSessionEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """SELECT * FROM sessions.sessions WHERE "Id" = @id AND "TenantId" = @tenantId""";
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<RemoteSessionEntity>(
            new CommandDefinition(sql, new { id, tenantId = _tenant.TenantId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task AddAsync(RemoteSessionEntity session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TenantId = _tenant.TenantId;
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CloseAsync(Guid id, DateTimeOffset closedAtUtc, CancellationToken ct = default)
    {
        var row = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == _tenant.TenantId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = "Closed";
        row.ClosedAtUtc = closedAtUtc;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RemoteSessionEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM sessions.sessions
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC
            """;
        return await QuerySessionsAsync(sql, new { tenantId }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RemoteSessionEntity>> QuerySessionsAsync(string sql, object parms, CancellationToken ct)
    {
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RemoteSessionEntity>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task AddForTenantAsync(RemoteSessionEntity session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Tenant-scoped write seam (caller stamps TenantId). Fail loudly on an empty value rather than persist
        // an orphan row that no tenant's query filter matches — a silent cross-tenant-isolation hole.
        if (string.IsNullOrWhiteSpace(session.TenantId))
        {
            throw new InvalidOperationException("RemoteSessionEntity.TenantId must be set before AddForTenantAsync.");
        }
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CloseForTenantAsync(string tenantId, Guid id, DateTimeOffset closedAtUtc, CancellationToken ct = default)
    {
        var row = await _db.Sessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = "Closed";
        row.ClosedAtUtc = closedAtUtc;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateRunResultAsync(
        string tenantId, Guid id, string status, string? prUrl, string? errorMessage,
        CancellationToken ct = default)
    {
        var row = await _db.Sessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = status;
        row.PrUrl = prUrl;
        row.Error = errorMessage;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Multi-repo ───────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SessionRepoEntity>> ListReposForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM sessions.session_repos
            WHERE "TenantId" = @tenantId AND "SessionId" = @sessionId
            ORDER BY "Owner", "Repo"
            """;
        return await QueryReposAsync(sql, new { tenantId, sessionId }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionRepoEntity>> ListAllReposForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        const string sql = """SELECT * FROM sessions.session_repos WHERE "TenantId" = @tenantId""";
        return await QueryReposAsync(sql, new { tenantId }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SessionRepoEntity>> QueryReposAsync(string sql, object parms, CancellationToken ct)
    {
        await using var conn = Conn.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionRepoEntity>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task AddRepoForTenantAsync(SessionRepoEntity repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(repo.TenantId))
        {
            throw new InvalidOperationException("SessionRepoEntity.TenantId must be set before AddRepoForTenantAsync.");
        }
        _db.SessionRepos.Add(repo);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateRepoRunResultAsync(
        string tenantId, Guid sessionRepoId, string status, string? branchName, string? prUrl, string? errorMessage,
        CancellationToken ct = default)
    {
        var row = await _db.SessionRepos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == sessionRepoId && r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        row.Status = status;
        row.BranchName = branchName;
        row.PrUrl = prUrl;
        row.Error = errorMessage;
        if (status is "Done" or "Failed")
        {
            row.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task IncrementFixRoundsForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId, ct).ConfigureAwait(false);
        if (session is null) { return; }
        session.FixRounds++;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DecrementFixRoundsForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId, ct).ConfigureAwait(false);
        if (session is null || session.FixRounds <= 0) { return; }
        session.FixRounds--;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<DeliverySummary> GetDeliveryForTenantAsync(string tenantId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var merged = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.MergedAtUtc != null && (since == null || s.MergedAtUtc >= since))
            .Select(s => new { s.FixRounds, s.CreatedAtUtc, s.MergedAtUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        var stuck = await _db.Sessions.IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId && (s.Status == "Failed" || s.Status == "Blocked"), ct).ConfigureAwait(false);
        return DeliverySummary.From(merged.Select(m => (m.FixRounds, (m.MergedAtUtc!.Value - m.CreatedAtUtc).TotalHours)).ToList(), stuck);
    }

    public async Task<bool> RecomputeSessionStatusForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        var repos = await _db.SessionRepos
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.SessionId == sessionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (repos.Count == 0)
        {
            return false;
        }

        session.Status = repos.Any(r => r.Status == "Failed") ? "Failed"
            : repos.Any(r => r.Status == "Blocked") ? "Blocked"   // QA gate refused the PR (plan 0.5)
            : repos.All(r => r.Status == "Merged") ? "Merged"               // every proposed change is in
            // Done = every repo has a proposed change, tested or not (Accepted / Rejected by the Testing agent).
            // (or reviewed: Reviewed / ChangesRequested by the Reviewer agent).
            : repos.All(r => r.Status is "Done" or "Merged" or "Accepted" or "Rejected" or "Reviewed" or "ChangesRequested") ? "Done"
            : "Running";
        if (session.Status == "Merged") { session.MergedAtUtc ??= DateTimeOffset.UtcNow; }

        // Mirror the first PR + first error onto the parent for back-compat display.
        session.PrUrl = repos.FirstOrDefault(r => !string.IsNullOrEmpty(r.PrUrl))?.PrUrl;
        session.Error = repos.FirstOrDefault(r => !string.IsNullOrEmpty(r.Error))?.Error;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
