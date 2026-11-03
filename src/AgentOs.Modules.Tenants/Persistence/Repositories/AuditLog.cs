// EF-backed IAuditLog + IAuditChainVerifier. Writes are best-effort: a DB outage must not break the surrounding
// signup / invitation flow, so the writer catches every database exception and logs it. Reads are tenant-scoped and
// bounded by `max` — no cross-tenant peek by construction.
// GAP-11b — append-only and tamper-evident:
//   • each write extends its tenant's hash chain (AuditChain): under a per-tenant transaction advisory lock it reads the
//     chain's head, takes the next Sequence and stores PrevHash + Hash. A unique (TenantId, Sequence) index is the
//     backstop — two writers can never both take a position, so the chain never forks (a loser re-reads and retries).
//     Values are clipped to their columns and the timestamp to the microsecond BEFORE hashing, so the stored row is
//     exactly what was hashed;
//   • the app role may only INSERT and SELECT here (REVOKE UPDATE, DELETE, TRUNCATE — RowLevelSecurity.AppendOnlyTables);
//   • RLS (ADR-0012 stage 2): an entry is written under its OWN tenant (it names it — a sign-up is anonymous, a platform
//     admin's export of another tenant is filed under that tenant); "_platform" rows are written and read only inside a
//     platform scope. A tenant read binds a flow that has no tenant of its own to the tenant asked for.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Persistence.Entities;
using AgentOs.SharedKernel.Logging;
using AgentOs.SharedKernel.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentOs.Modules.Tenants.Persistence.Repositories;

internal sealed class EfAuditLog(
    TenantsDbContext db,
    ILogger<EfAuditLog> logger,
    INpgsqlConnectionFactory? connectionFactory = null) : IAuditLog, IAuditChainVerifier
{
    // Writers queue on the chain lock; the retries only cover one that didn't take it (e.g. mid rolling deploy).
    private const int MaxAttempts = 5;
    private const string ChainLockPrefix = "agentos:audit-chain:";

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var scope = WriteScope(entry.TenantId);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                // One writer per chain at a time, across hosts and replicas: a transaction-scoped advisory lock keyed on the
                // tenant serialises "read the head → insert the next link". EF Core has no API for advisory locks, hence
                // this one parameterized raw statement (a system function — no table, no tenant data). The unique
                // (TenantId, Sequence) index stays the backstop: a writer that skipped the lock collides and retries.
                await using var tx = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                    : null;
                if (tx is not null)
                {
                    await db.Database.ExecuteSqlAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({ChainLockPrefix + entry.TenantId}, 0))", ct).ConfigureAwait(false);
                }
                var row = await NextLinkAsync(entry, ct).ConfigureAwait(false);
                db.AuditEvents.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    if (tx is not null) { await tx.CommitAsync(ct).ConfigureAwait(false); }
                    return;
                }
                catch (DbUpdateException ex) when (attempt < MaxAttempts && IsChainCollision(ex))
                {
                    // Another writer took this position first: re-read the head and try the next one.
                    continue;
                }
                finally
                {
                    // The context is shared with the request's other writes: never leave this row tracked (a failed save
                    // would otherwise be retried — with a stale chain position — by the next unrelated SaveChanges).
                    db.Entry(row).State = EntityState.Detached;
                }
            }
        }
        catch (DbUpdateException ex) { Handle(ex); }
        catch (System.Data.Common.DbException ex) { Handle(ex); }
        catch (InvalidOperationException ex) { Handle(ex); }

        void Handle(Exception e) =>
            // The action label is intentionally NOT echoed here: some action constants are named
            // after sensitive operations (e.g. password reset), which trips static-analysis secret
            // heuristics, and the action is already captured in the persisted audit record. Tenant +
            // exception are enough to diagnose a failed best-effort audit write.
            logger.LogWarning(e,
                "Audit write failed for tenant={TenantId} — surrounding op continues.",
                LogSafe.Scrub(entry.TenantId));
    }

    // The next row of the entry's tenant chain: head + 1, linked to the head's hash, hashed over the stored values.
    private async Task<AuditEventEntity> NextLinkAsync(AuditEntry entry, CancellationToken ct)
    {
        var head = await db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == entry.TenantId && e.Sequence != null)
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Hash })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var row = new AuditEventEntity
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            TenantId = entry.TenantId,
            UserId = Clip(entry.UserId, 128),
            Action = Clip(entry.Action, 64)!,
            Target = Clip(entry.Target, 256),
            IpAddress = Clip(entry.IpAddress, 64),
            UserAgent = Clip(entry.UserAgent, 512),
            TimestampUtc = AuditChain.Normalize(entry.TimestampUtc == default ? DateTimeOffset.UtcNow : entry.TimestampUtc),
            Sequence = (head?.Sequence ?? 0) + 1,
            PrevHash = head?.Hash ?? AuditChain.Genesis,
        };
        row.Hash = AuditChain.Compute(LinkOf(row));
        return row;
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = ReadScope(tenantId);

        if (connectionFactory is not null)
        {
            return await ListViaDapperAsync(tenantId, max, ct).ConfigureAwait(false);
        }

        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.TimestampUtc)
            .Take(max)
            .Select(e => new AuditEntry(e.Id, e.TenantId, e.UserId, e.Action, e.Target, e.IpAddress, e.UserAgent, e.TimestampUtc, e.Sequence, e.Hash))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows;
    }

    public async Task<AuditChainReport> VerifyAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        using var scope = ReadScope(tenantId);
        var unchained = await db.AuditEvents.AsNoTracking()
            .LongCountAsync(e => e.TenantId == tenantId && e.Sequence == null, ct).ConfigureAwait(false);
        // Oldest first, streamed off one cursor (the (TenantId, Sequence) index serves it): O(1) memory however long.
        var links = db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Sequence != null)
            .OrderBy(e => e.Sequence)
            .Select(e => new AuditChainLink(e.Sequence!.Value, e.Id, e.TenantId, e.TimestampUtc, e.UserId, e.Action, e.Target,
                e.IpAddress, e.UserAgent, e.PrevHash ?? string.Empty, e.Hash ?? string.Empty))
            .AsAsyncEnumerable();
        return await AuditChain.VerifyAsync(tenantId, links, unchained, ct).ConfigureAwait(false);
    }

    // Dapper fast-path: audit_events under the "tenants" schema; the (TenantId, TimestampUtc) index
    // serves the filter + ordering. LIMIT is clamped to ≥0 to match EF's Take semantics. RLS-bound like the EF path.
    private async Task<IReadOnlyList<AuditEntry>> ListViaDapperAsync(string tenantId, int max, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "TenantId", "UserId", "Action", "Target", "IpAddress", "UserAgent", "TimestampUtc", "Sequence", "Hash"
            FROM tenants.audit_events
            WHERE "TenantId" = @tenantId
            ORDER BY "TimestampUtc" DESC
            LIMIT @max
            """;

        await using var conn = await connectionFactory!.OpenWithTenantAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AuditEventEntity>(
            new CommandDefinition(sql, new { tenantId, max = Math.Max(0, max) }, cancellationToken: ct))
            .ConfigureAwait(false);

        return rows.Select(e => new AuditEntry(
            e.Id, e.TenantId, e.UserId, e.Action, e.Target, e.IpAddress, e.UserAgent, e.TimestampUtc, e.Sequence, e.Hash)).ToList();
    }

    internal static AuditChainLink LinkOf(AuditEventEntity e)
        => new(e.Sequence ?? 0, e.Id, e.TenantId, e.TimestampUtc, e.UserId, e.Action, e.Target, e.IpAddress, e.UserAgent,
            e.PrevHash ?? string.Empty, e.Hash ?? string.Empty);

    // An entry is filed under its own tenant; "_platform" rows only in a platform scope.
    private static IDisposable WriteScope(string tenantId)
        => string.Equals(tenantId, AuditScopes.Platform, StringComparison.Ordinal)
            ? RowLevelSecurity.PlatformScope("audit-platform")
            : RowLevelSecurity.TenantScope(tenantId);

    // A tenant read never widens a flow that acts for a tenant; it binds one that acts for none.
    private static IDisposable? ReadScope(string tenantId)
        => string.Equals(tenantId, AuditScopes.Platform, StringComparison.Ordinal)
            ? RowLevelSecurity.PlatformScope("audit-platform")
            : RowLevelSecurity.TenantScopeIfUnbound(tenantId);

    // A unique violation on (TenantId, Sequence): a concurrent writer extended the chain first.
    private static bool IsChainCollision(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
           && (pg.ConstraintName?.Contains("Sequence", StringComparison.OrdinalIgnoreCase) ?? true);

    // Clip to the column (never splitting a surrogate pair) so the stored value is exactly the hashed one.
    internal static string? Clip(string? value, int max)
    {
        if (value is null || value.Length <= max) { return value; }
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }
}

internal sealed class NullAuditLog : IAuditLog, IAuditChainVerifier
{
    public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>([]);

    public Task<AuditChainReport> VerifyAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(new AuditChainReport(tenantId, 0, 0, null, null, null, AuditChainProblem.None));
}
