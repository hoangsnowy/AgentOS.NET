// EF-backed IRunnerDirectory. Used by the SignalR hub during the pairing handshake, where there is no
// authenticated tenant yet — so the lookup bypasses the tenant query filter (IgnoreQueryFilters) and
// finds the runner by its unguessable id. The caller MUST then verify the presented token against the
// returned TokenHash before trusting TenantId / OwnerUserId. The id alone grants nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

internal sealed class EfRunnerDirectory : IRunnerDirectory
{
    private readonly SessionsDbContext _db;

    public EfRunnerDirectory(SessionsDbContext db) => _db = db;

    public async Task<RunnerIdentity?> FindForPairingAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        return await _db.Runners
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == runnerId)
            .Select(r => new RunnerIdentity(r.Id, r.TenantId, r.OwnerUserId, r.TokenHash, r.Status))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> MarkPairedAsync(Guid runnerId, CancellationToken cancellationToken = default)
    {
        // Same tenant-filter bypass as the lookup: the handshake has no authenticated tenant, and the
        // caller has already verified the presented token against this row's hash.
        // ONE conditional UPDATE (… WHERE Status <> 'Revoked'): a load-then-save would write "Paired" over a revoke that
        // landed in between and un-revoke the runner (GAP-04). The in-memory test provider has no ExecuteUpdate, so it
        // keeps the tracked path (single-threaded there, no race to lose).
        if (_db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            var updated = await _db.Runners
                .IgnoreQueryFilters()
                .Where(r => r.Id == runnerId && r.Status != "Revoked")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "Paired").SetProperty(r => r.LastSeenUtc, now), cancellationToken)
                .ConfigureAwait(false);
            return updated > 0;
        }

        var row = await _db.Runners
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null || string.Equals(row.Status, "Revoked", StringComparison.Ordinal))
        {
            return false;
        }

        row.Status = "Paired";
        row.LastSeenUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RetireReplacedAsync(Guid runnerId, Func<Guid, bool> isConnected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isConnected);
        var row = await _db.Runners
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runnerId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || string.Equals(row.Status, "Revoked", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(row.Label))
        {
            return 0;
        }
        // Same tenant, same member, same name, paired earlier — and not live (two machines that share a name both stay).
        var older = await _db.Runners
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == row.TenantId && r.OwnerUserId == row.OwnerUserId && r.Label == row.Label
                && r.Id != row.Id && r.Status != "Revoked" && r.CreatedAtUtc < row.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var retired = older.Where(r => !isConnected(r.Id)).ToList();
        foreach (var r in retired) { r.Status = "Revoked"; }
        if (retired.Count > 0) { await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        return retired.Count;
    }

    public async Task<IReadOnlyList<Guid>> RevokeForMemberAsync(string tenantId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        // Tenant-explicit (a member is removed from one tenant; the caller may be a circuit without an ambient tenant).
        var rows = await _db.Runners
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.OwnerUserId == ownerUserId && r.Status != "Revoked")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var r in rows) { r.Status = "Revoked"; }
        if (rows.Count > 0) { await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        return rows.Select(r => r.Id).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindStatusesAsync(IReadOnlyCollection<Guid> runnerIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runnerIds);
        if (runnerIds.Count == 0) { return new Dictionary<Guid, string>(); }
        // One WHERE Id IN (…) per revalidation sweep, tenant-unfiltered like the handshake lookup (the sweep spans tenants).
        var ids = runnerIds.ToArray();
        return await _db.Runners
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Status, cancellationToken)
            .ConfigureAwait(false);
    }
}
