// ITicketFlowStore implementations. EF (Postgres): every method is tenant-explicit — IgnoreQueryFilters() plus an
// explicit TenantId predicate (the global filter reads ITenantContext, which a circuit or the Autopilot worker
// doesn't have) — and RLS binds each connection to the ambient tenant as the second wall. Version numbers are
// max + 1 per tenant under a unique (TenantId, Version) index; two concurrent saves race on it and the loser retries.
// In-memory: the stateless standalone Web (no connection string) — same semantics, process-local.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Workspaces.Persistence.Repositories;

internal static class TicketFlowMapping
{
    public const int MaxHistory = 100;

    public static TicketFlowVersion ToRecord(TicketFlowVersionEntity e) =>
        new(e.BoardId, e.Version, e.DefinitionJson, e.CreatedByUserId, e.CreatedByName, e.CreatedAtUtc, e.Note);

    public static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

internal sealed class EfTicketFlowStore(WorkspacesDbContext db) : ITicketFlowStore
{
    public async Task<TicketFlowVersion?> CurrentAsync(string tenantId, Guid boardId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var row = await db.TicketFlows.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BoardId == boardId)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : TicketFlowMapping.ToRecord(row);
    }

    public async Task<TicketFlowVersion?> GetVersionAsync(string tenantId, int version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var row = await db.TicketFlows.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Version == version, ct).ConfigureAwait(false);
        return row is null ? null : TicketFlowMapping.ToRecord(row);
    }

    public async Task<IReadOnlyList<TicketFlowVersion>> HistoryAsync(string tenantId, Guid boardId, int max = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var rows = await db.TicketFlows.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BoardId == boardId)
            .OrderByDescending(x => x.Version).Take(Math.Clamp(max, 1, TicketFlowMapping.MaxHistory))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(TicketFlowMapping.ToRecord).ToList();
    }

    public async Task<TicketFlowVersion> AppendAsync(string tenantId, Guid boardId, string? definitionJson, string? userId, string? userName,
        string? note, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        for (var attempt = 0; ; attempt++)
        {
            var last = await db.TicketFlows.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId).MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0;
            var now = DateTimeOffset.UtcNow;
            var row = new TicketFlowVersionEntity
            {
                Id = Guid.CreateVersion7(now),
                TenantId = tenantId,
                BoardId = boardId,
                Version = last + 1,
                DefinitionJson = definitionJson,
                CreatedByUserId = TicketFlowMapping.Clip(userId, 64),
                CreatedByName = TicketFlowMapping.Clip(userName, 200),
                CreatedAtUtc = now,
                Note = TicketFlowMapping.Clip(note, 200),
            };
            db.TicketFlows.Add(row);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return TicketFlowMapping.ToRecord(row);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another save took this number first (unique TenantId + Version): take the next one.
                db.Entry(row).State = EntityState.Detached;
            }
            catch (DbUpdateException ex)
            {
                // Still losing the race after four tries: tell the person instead of failing the circuit.
                db.Entry(row).State = EntityState.Detached;
                throw new TicketFlowSaveConflictException(ex);
            }
        }
    }
}

internal sealed class InMemoryTicketFlowStore : ITicketFlowStore
{
    private readonly List<(string TenantId, TicketFlowVersion Version)> _rows = [];
    private readonly Lock _gate = new();

    public Task<TicketFlowVersion?> CurrentAsync(string tenantId, Guid boardId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Where(r => r.TenantId == tenantId && r.Version.BoardId == boardId)
                .Select(r => r.Version).MaxBy(v => v.Version));
        }
    }

    public Task<TicketFlowVersion?> GetVersionAsync(string tenantId, int version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Where(r => r.TenantId == tenantId && r.Version.Version == version).Select(r => r.Version).FirstOrDefault());
        }
    }

    public Task<IReadOnlyList<TicketFlowVersion>> HistoryAsync(string tenantId, Guid boardId, int max = 20, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TicketFlowVersion>>(_rows.Where(r => r.TenantId == tenantId && r.Version.BoardId == boardId)
                .Select(r => r.Version).OrderByDescending(v => v.Version).Take(Math.Clamp(max, 1, TicketFlowMapping.MaxHistory)).ToList());
        }
    }

    public Task<TicketFlowVersion> AppendAsync(string tenantId, Guid boardId, string? definitionJson, string? userId, string? userName,
        string? note, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (_gate)
        {
            var next = _rows.Where(r => r.TenantId == tenantId).Select(r => r.Version.Version).DefaultIfEmpty(0).Max() + 1;
            var version = new TicketFlowVersion(boardId, next, definitionJson, TicketFlowMapping.Clip(userId, 64), TicketFlowMapping.Clip(userName, 200),
                DateTimeOffset.UtcNow, TicketFlowMapping.Clip(note, 200));
            _rows.Add((tenantId, version));
            return Task.FromResult(version);
        }
    }
}
