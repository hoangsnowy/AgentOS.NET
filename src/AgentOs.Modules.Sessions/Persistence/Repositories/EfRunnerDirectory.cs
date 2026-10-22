// EF-backed IRunnerDirectory. Used by the SignalR hub during the pairing handshake, where there is no
// authenticated tenant yet — so the lookup bypasses the tenant query filter (IgnoreQueryFilters) and
// finds the runner by its unguessable id. The caller MUST then verify the presented token against the
// returned TokenHash before trusting TenantId / OwnerUserId. The id alone grants nothing.

using System;
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
        // caller has already verified the presented token against this row's hash. Tracked load +
        // SaveChanges rather than ExecuteUpdate — the connect path touches exactly one row, so the
        // extra round-trip is irrelevant, and it keeps the provider surface small.
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
}
