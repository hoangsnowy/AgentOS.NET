// Design gate (ADR-0016) — the two stores of design reviews: EF Core over sessions.design_reviews, and a process-local map
// for the no-database path (standalone Development, its E2E). Every EF query is tenant-explicit (IgnoreQueryFilters + a
// TenantId predicate — a background run or a Blazor circuit has no ambient HTTP tenant), with RLS as the second wall.
// Findings and screenshots are stored as JSON (source-generated), never parsed from anything an agent controls directly.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Sessions.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

/// <summary>Maps a design review to and from its row.</summary>
internal static class DesignReviewRows
{
    internal sealed record FindingRow(string Screen, string Problem, string Fix);

    internal sealed record ShotRow(string Path, string Screen, int Width, string Theme);

    internal static DesignReview ToDomain(DesignReviewEntity e) => new(
        e.Id, e.TenantId, e.BoardId, e.SessionId, e.IssueNumber, e.Run,
        Enum.TryParse<DesignReviewStatus>(e.Status, out var status) ? status : DesignReviewStatus.NotRun,
        e.Summary,
        Read(e.FindingsJson, DesignReviewJson.Default.ListFindingRow).Select(f => new DesignFinding(f.Screen, f.Problem, f.Fix)).ToList(),
        Read(e.ShotsJson, DesignReviewJson.Default.ListShotRow).Select(s => new DesignShot(s.Path, s.Screen, s.Width, s.Theme)).ToList(),
        e.Error,
        Enum.TryParse<DesignDecision>(e.Decision, out var decision) ? decision : null,
        e.DecisionNote, e.DecidedBy, e.DecidedAtUtc, e.UpdatedAtUtc);

    internal static void Apply(DesignReview r, DesignReviewEntity e)
    {
        e.TenantId = r.TenantId;
        e.BoardId = r.BoardId;
        e.SessionId = r.SessionId;
        e.IssueNumber = r.IssueNumber;
        e.Run = r.Run;
        e.Status = r.Status.ToString();
        e.Summary = Clip(r.Summary, 4000) ?? string.Empty;
        e.FindingsJson = JsonSerializer.Serialize(r.Findings.Take(50).Select(f => new FindingRow(Clip(f.Screen, 200)!, Clip(f.Problem, 1000)!, Clip(f.Fix, 1000)!)).ToList(),
            DesignReviewJson.Default.ListFindingRow);
        e.ShotsJson = JsonSerializer.Serialize(r.Shots.Take(64).Select(s => new ShotRow(s.Path, Clip(s.Screen, 80)!, s.Width, s.Theme)).ToList(),
            DesignReviewJson.Default.ListShotRow);
        e.Error = Clip(r.Error, 2000);
        e.Decision = r.Decision?.ToString();
        e.DecisionNote = Clip(r.DecisionNote, 4000);
        e.DecidedBy = Clip(r.DecidedBy, 256);
        e.DecidedAtUtc = r.DecidedAtUtc;
        e.UpdatedAtUtc = r.UpdatedAtUtc;
    }

    private static List<T> Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> type)
    {
        try { return JsonSerializer.Deserialize(json, type) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DesignReviewRows.FindingRow>))]
[JsonSerializable(typeof(List<DesignReviewRows.ShotRow>))]
internal sealed partial class DesignReviewJson : JsonSerializerContext;

/// <summary><see cref="IDesignReviewStore"/> over <c>sessions.design_reviews</c>.</summary>
internal sealed class EfDesignReviewStore(SessionsDbContext db) : IDesignReviewStore
{
    public async Task<DesignReview?> GetAsync(string tenantId, Guid sessionId, int run, CancellationToken ct = default)
    {
        var row = await db.DesignReviews.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.SessionId == sessionId && r.Run == run, ct).ConfigureAwait(false);
        return row is null ? null : DesignReviewRows.ToDomain(row);
    }

    public async Task<DesignReview?> LatestAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var row = await db.DesignReviews.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.SessionId == sessionId)
            .OrderByDescending(r => r.Run)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : DesignReviewRows.ToDomain(row);
    }

    public async Task<IReadOnlyList<DesignReview>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default)
    {
        var rows = await db.DesignReviews.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Take(Math.Clamp(max, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(DesignReviewRows.ToDomain).ToList();
    }

    public async Task<DesignReview> SaveAsync(DesignReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (string.IsNullOrWhiteSpace(review.TenantId)) { throw new InvalidOperationException("A design review needs a tenant."); }
        var row = await db.DesignReviews.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == review.TenantId && r.SessionId == review.SessionId && r.Run == review.Run, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new DesignReviewEntity { Id = review.Id == Guid.Empty ? Guid.NewGuid() : review.Id };
            db.DesignReviews.Add(row);
        }
        DesignReviewRows.Apply(review, row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DesignReviewRows.ToDomain(row);
    }
}

/// <summary><see cref="IDesignReviewStore"/> in process memory (no database): keyed by (tenant, task, run), bounded.</summary>
internal sealed class InMemoryDesignReviewStore : IDesignReviewStore
{
    /// <summary>Most reviews kept (all tenants) — the oldest go first.</summary>
    internal const int MaxReviews = 2000;

    private readonly ConcurrentDictionary<(string Tenant, Guid Session, int Run), DesignReviewEntity> _rows = new();

    public Task<DesignReview?> GetAsync(string tenantId, Guid sessionId, int run, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue((tenantId, sessionId, run), out var row) ? DesignReviewRows.ToDomain(row) : null);

    public Task<DesignReview?> LatestAsync(string tenantId, Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult(_rows.Where(kv => kv.Key.Tenant == tenantId && kv.Key.Session == sessionId)
            .OrderByDescending(kv => kv.Key.Run).Select(kv => DesignReviewRows.ToDomain(kv.Value)).FirstOrDefault());

    public Task<IReadOnlyList<DesignReview>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DesignReview>>(_rows.Where(kv => kv.Key.Tenant == tenantId)
            .Select(kv => kv.Value).OrderByDescending(r => r.UpdatedAtUtc).Take(Math.Clamp(max, 1, 200))
            .Select(DesignReviewRows.ToDomain).ToList());

    public Task<DesignReview> SaveAsync(DesignReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (string.IsNullOrWhiteSpace(review.TenantId)) { throw new InvalidOperationException("A design review needs a tenant."); }
        var key = (review.TenantId, review.SessionId, review.Run);
        var row = new DesignReviewEntity { Id = _rows.TryGetValue(key, out var old) ? old.Id : review.Id == Guid.Empty ? Guid.NewGuid() : review.Id };
        DesignReviewRows.Apply(review, row);
        _rows[key] = row;
        if (_rows.Count > MaxReviews)
        {
            foreach (var stale in _rows.OrderBy(kv => kv.Value.UpdatedAtUtc).Take(_rows.Count - MaxReviews).Select(kv => kv.Key).ToList())
            {
                _rows.TryRemove(stale, out _);
            }
        }
        return Task.FromResult(DesignReviewRows.ToDomain(row));
    }
}
