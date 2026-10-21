// The knowledge-graph store over Postgres. Tenant-explicit on every call (IgnoreQueryFilters + an explicit tenant
// predicate), so it works from the orchestrator and background runs where no HTTP tenant exists; RLS still applies.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Memory.Persistence;

/// <inheritdoc cref="IMemoryStore"/>
internal sealed class EfMemoryStore(MemoryDbContext db, TimeProvider clock) : IMemoryStore
{
    private const int MaxTitle = 400;
    private const int MaxBody = 16_000;

    public async Task<Guid> RecordAsync(string tenantId, MemoryNodeInput node, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(node);
        var now = clock.GetUtcNow();
        var existing = await db.Nodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.Key == node.Key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Title = Clip(node.Title, MaxTitle);
            existing.Body = node.Body is null ? existing.Body : Clip(node.Body, MaxBody);
            existing.RepoKey = node.RepoKey ?? existing.RepoKey;
            existing.SourceRef = node.SourceRef ?? existing.SourceRef;
            existing.RecordedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing.Id;
        }
        var entity = new MemoryNodeEntity
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            RepoKey = node.RepoKey,
            Kind = node.Kind,
            Key = node.Key,
            Title = Clip(node.Title, MaxTitle),
            Body = node.Body is null ? null : Clip(node.Body, MaxBody),
            SourceRef = node.SourceRef,
            ValidFrom = now,
            RecordedAt = now,
        };
        db.Nodes.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.Id;
    }

    public async Task LinkAsync(string tenantId, Guid fromId, Guid toId, string rel, CancellationToken ct = default)
    {
        if (fromId == Guid.Empty || toId == Guid.Empty || fromId == toId) { return; }
        var exists = await db.Edges.IgnoreQueryFilters().AnyAsync(e => e.TenantId == tenantId && e.FromId == fromId && e.ToId == toId
            && e.Rel == rel && e.ValidTo == null, ct).ConfigureAwait(false);
        if (exists) { return; }
        var now = clock.GetUtcNow();
        db.Edges.Add(new MemoryEdgeEntity { Id = Guid.CreateVersion7(now), TenantId = tenantId, FromId = fromId, ToId = toId, Rel = rel, ValidFrom = now, RecordedAt = now });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryNode>> RecallLessonsAsync(string tenantId, string repoKey, string query, int max, CancellationToken ct = default)
    {
        if (max <= 0) { return []; }
        var lessons = db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.RetractedAt == null && n.Kind == MemoryKinds.Lesson
                        && (n.RepoKey == repoKey || n.RepoKey == null));
        var picked = await lessons.Where(n => n.Pinned).OrderByDescending(n => n.RecordedAt).Take(max).ToListAsync(ct).ConfigureAwait(false);
        if (picked.Count < max)
        {
            var rest = lessons.Where(n => !n.Pinned);
            var ranked = string.IsNullOrWhiteSpace(query)
                ? rest.OrderByDescending(n => n.RecordedAt)
                : rest.OrderByDescending(n => n.SearchVector.Rank(EF.Functions.PlainToTsQuery("simple", query))).ThenByDescending(n => n.RecordedAt);
            picked.AddRange(await ranked.Take(max - picked.Count).ToListAsync(ct).ConfigureAwait(false));
        }
        if (picked.Count > 0)
        {
            var ids = picked.Select(n => n.Id).ToList();
            var now = clock.GetUtcNow();
            await db.Nodes.IgnoreQueryFilters().Where(n => n.TenantId == tenantId && ids.Contains(n.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.LastUsedUtc, now).SetProperty(n => n.UseCount, n => n.UseCount + 1), ct)
                .ConfigureAwait(false);
        }
        return picked.Select(ToView).ToList();
    }

    public async Task<MemoryGraph> GraphAsync(string tenantId, string? repoKey, int maxNodes, CancellationToken ct = default)
    {
        var nodes = await db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.RetractedAt == null && (repoKey == null || n.RepoKey == repoKey))
            .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.RecordedAt)
            .Take(Math.Clamp(maxNodes, 1, 500))
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var idList = ids.ToList();
        var edges = await db.Edges.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ValidTo == null && idList.Contains(e.FromId) && idList.Contains(e.ToId))
            .Select(e => new MemoryEdge(e.FromId, e.ToId, e.Rel))
            .ToListAsync(ct).ConfigureAwait(false);
        return new MemoryGraph(nodes.Select(ToView).ToList(), edges);
    }

    public async Task SetPinnedAsync(string tenantId, Guid nodeId, bool pinned, CancellationToken ct = default) =>
        await db.Nodes.IgnoreQueryFilters().Where(n => n.TenantId == tenantId && n.Id == nodeId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Pinned, pinned), ct).ConfigureAwait(false);

    public async Task ForgetAsync(string tenantId, Guid nodeId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.Nodes.IgnoreQueryFilters().Where(n => n.TenantId == tenantId && n.Id == nodeId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.RetractedAt, now), ct).ConfigureAwait(false);
        // Its relations stop holding too (kept for history).
        await db.Edges.IgnoreQueryFilters().Where(e => e.TenantId == tenantId && e.ValidTo == null && (e.FromId == nodeId || e.ToId == nodeId))
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ValidTo, now), ct).ConfigureAwait(false);
    }

    private static MemoryNode ToView(MemoryNodeEntity n) =>
        new(n.Id, n.Kind, n.Key, n.Title, n.Body, n.RepoKey, n.Pinned, n.RecordedAt, n.SourceRef);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
