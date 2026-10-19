// IScheduledRunStore implementations. EF (Postgres): tenant-explicit methods use IgnoreQueryFilters() + an explicit
// TenantId predicate (the global filter reads ITenantContext, blank off-request), and RLS still binds each
// connection to the ambient tenant. ClaimDueAsync is the one deliberate cross-tenant read — the scheduler must see
// every tenant's due rows; it runs with no ambient tenant (RLS stage 1 is permissive then) and each claim is an
// optimistic single-row UPDATE (… WHERE Id = @id AND NextRunAtUtc = @seen), so only one host wins an occurrence.
// In-memory: the stateless standalone Web (no connection string) — same semantics, process-local.

using System.Collections.Concurrent;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Pipeline.Scheduling;

internal static class ScheduledRunMapping
{
    public static ScheduleSpec SpecOf(ScheduledRunEntity e) =>
        e.Kind == nameof(ScheduleKind.Once) ? new ScheduleSpec(ScheduleKind.Once, null, e.RunAtUtc) : new ScheduleSpec(ScheduleKind.Cron, e.Cron, null);

    public static ScheduledRunInfo ToInfo(ScheduledRunEntity e) =>
        new(e.Id, e.Story, SpecOf(e), e.Enabled, e.NextRunAtUtc, e.LastRunAtUtc, e.LastStatus, e.CreatedBy, e.CreatedAtUtc);

    public static ScheduledRunEntity ToEntity(string tenantId, ScheduledRunInfo r) => new()
    {
        Id = r.Id,
        TenantId = tenantId,
        Story = r.Story,
        Kind = r.Spec.Kind.ToString(),
        Cron = r.Spec.Cron,
        RunAtUtc = r.Spec.RunAtUtc,
        Enabled = r.Enabled,
        NextRunAtUtc = r.NextRunAtUtc,
        LastRunAtUtc = r.LastRunAtUtc,
        LastStatus = r.LastStatus,
        CreatedBy = r.CreatedBy,
        CreatedAtUtc = r.CreatedAtUtc,
    };

    // Soonest first; paused / finished ones last.
    public static IReadOnlyList<ScheduledRunInfo> Order(IEnumerable<ScheduledRunInfo> runs) =>
        runs.OrderBy(r => r.NextRunAtUtc is null ? 1 : 0).ThenBy(r => r.NextRunAtUtc).ThenByDescending(r => r.CreatedAtUtc).ToList();
}

internal sealed class EfScheduledRunStore(PipelineDbContext db) : IScheduledRunStore
{
    public async Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        var rows = await db.ScheduledRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync(ct).ConfigureAwait(false);
        return ScheduledRunMapping.Order(rows.Select(ScheduledRunMapping.ToInfo));
    }

    public async Task AddAsync(string tenantId, ScheduledRunInfo run, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(run);
        db.ScheduledRuns.Add(ScheduledRunMapping.ToEntity(tenantId, run));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, DateTimeOffset now, CancellationToken ct = default)
    {
        var e = await db.ScheduledRuns.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct).ConfigureAwait(false);
        if (e is null) { return false; }
        e.Enabled = enabled;
        e.NextRunAtUtc = enabled ? ScheduledRunMapping.SpecOf(e).NextAfter(now) : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        await db.ScheduledRuns.IgnoreQueryFilters().Where(x => x.Id == id && x.TenantId == tenantId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

    public async Task<IReadOnlyList<DueRun>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        var due = await db.ScheduledRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Enabled && x.NextRunAtUtc != null && x.NextRunAtUtc <= now)
            .OrderBy(x => x.NextRunAtUtc).Take(max).ToListAsync(ct).ConfigureAwait(false);
        var claimed = new List<DueRun>(due.Count);
        foreach (var e in due)
        {
            var seen = e.NextRunAtUtc!.Value;
            // A missed occurrence (host was down) runs once; the next one is computed from NOW — no catch-up storm.
            var next = ScheduledRunMapping.SpecOf(e).NextAfter(now);
            var stillEnabled = next is not null;
            var won = await db.ScheduledRuns.IgnoreQueryFilters()
                .Where(x => x.Id == e.Id && x.NextRunAtUtc == seen)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAtUtc, next).SetProperty(x => x.Enabled, stillEnabled), ct)
                .ConfigureAwait(false);
            if (won == 1) { claimed.Add(new DueRun(e.TenantId, ScheduledRunMapping.ToInfo(e), seen)); }
        }
        return claimed;
    }

    public async Task RecordOutcomeAsync(string tenantId, Guid id, DateTimeOffset ranAtUtc, string status, CancellationToken ct = default) =>
        await db.ScheduledRuns.IgnoreQueryFilters().Where(x => x.Id == id && x.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAtUtc, ranAtUtc).SetProperty(x => x.LastStatus, status), ct)
            .ConfigureAwait(false);
}

internal sealed class InMemoryScheduledRunStore : IScheduledRunStore
{
    private readonly ConcurrentDictionary<Guid, (string TenantId, ScheduledRunInfo Run)> _rows = new();
    private readonly Lock _claim = new();

    public Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(ScheduledRunMapping.Order(_rows.Values.Where(r => r.TenantId == tenantId).Select(r => r.Run)));

    public Task AddAsync(string tenantId, ScheduledRunInfo run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _rows[run.Id] = (tenantId, run);
        return Task.CompletedTask;
    }

    public Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!_rows.TryGetValue(id, out var row) || row.TenantId != tenantId) { return Task.FromResult(false); }
        _rows[id] = (tenantId, row.Run with { Enabled = enabled, NextRunAtUtc = enabled ? row.Run.Spec.NextAfter(now) : null });
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(id, out var row) && row.TenantId == tenantId && _rows.TryRemove(id, out _));

    public Task<IReadOnlyList<DueRun>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        lock (_claim)
        {
            var claimed = new List<DueRun>();
            foreach (var (id, row) in _rows.Where(r => r.Value.Run is { Enabled: true, NextRunAtUtc: { } at } && at <= now)
                         .OrderBy(r => r.Value.Run.NextRunAtUtc).Take(max).ToList())
            {
                var next = row.Run.Spec.NextAfter(now);
                _rows[id] = (row.TenantId, row.Run with { NextRunAtUtc = next, Enabled = next is not null });
                claimed.Add(new DueRun(row.TenantId, row.Run, row.Run.NextRunAtUtc!.Value));
            }
            return Task.FromResult<IReadOnlyList<DueRun>>(claimed);
        }
    }

    public Task RecordOutcomeAsync(string tenantId, Guid id, DateTimeOffset ranAtUtc, string status, CancellationToken ct = default)
    {
        if (_rows.TryGetValue(id, out var row) && row.TenantId == tenantId)
        {
            _rows[id] = (tenantId, row.Run with { LastRunAtUtc = ranAtUtc, LastStatus = status });
        }
        return Task.CompletedTask;
    }
}
