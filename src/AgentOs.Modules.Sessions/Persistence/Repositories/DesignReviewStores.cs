// Design gate (ADR-0016) — the two stores of design reviews: EF Core over sessions.design_reviews, and a process-local map
// for the no-database path (standalone Development, its E2E). Every EF query is tenant-explicit (IgnoreQueryFilters + a
// TenantId predicate — a background run or a Blazor circuit has no ambient HTTP tenant), with RLS as the second wall.
// Findings and screenshots are stored as JSON (source-generated).
//
// No whole-row writes after the row exists: the agent's side and the owner's decision are separate, narrow updates (EF
// sends only the columns it changed). A Designer verdict that lands half an hour after the owner pressed "Ask for
// changes" therefore can't erase that decision (which would let the ticket merge), and a decision made from a stale
// page can't erase a verdict.

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
        e.DecisionNote, e.DecidedBy, e.DecidedAtUtc, e.UpdatedAtUtc)
    {
        Retries = e.Retries,
    };

    /// <summary>A new row: every column from <paramref name="r"/>.</summary>
    internal static DesignReviewEntity NewRow(DesignReview r)
    {
        var e = new DesignReviewEntity
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
            TenantId = r.TenantId,
            BoardId = r.BoardId,
            SessionId = r.SessionId,
            IssueNumber = r.IssueNumber,
            Run = r.Run,
            Retries = r.Retries,
        };
        ApplyVerdict(e, new DesignVerdictUpdate(r.Status, r.Summary, r.Findings, r.Shots, r.Error), r.UpdatedAtUtc);
        if (r.Decision is { } d) { ApplyDecision(e, new DesignOwnerDecision(d, r.DecisionNote, r.DecidedBy ?? string.Empty, r.DecidedAtUtc ?? r.UpdatedAtUtc)); }
        return e;
    }

    /// <summary>The agent's side only — the decision columns are never touched here.</summary>
    internal static void ApplyVerdict(DesignReviewEntity e, DesignVerdictUpdate u, DateTimeOffset at)
    {
        e.Status = u.Status.ToString();
        if (u.Summary is not null) { e.Summary = Clip(u.Summary, 4000)!; }
        if (u.Findings is not null)
        {
            e.FindingsJson = JsonSerializer.Serialize(u.Findings.Take(50).Select(f => new FindingRow(Clip(f.Screen, 200)!, Clip(f.Problem, 1000)!, Clip(f.Fix, 1000)!)).ToList(),
                DesignReviewJson.Default.ListFindingRow);
        }
        if (u.Shots is not null)
        {
            e.ShotsJson = JsonSerializer.Serialize(u.Shots.Take(64).Select(s => new ShotRow(s.Path, Clip(s.Screen, 80)!, s.Width, s.Theme)).ToList(),
                DesignReviewJson.Default.ListShotRow);
        }
        e.Error = Clip(u.Error, 2000);
        if (u.CountRetry) { e.Retries++; }
        e.UpdatedAtUtc = at;
    }

    /// <summary>The owner's decision only.</summary>
    internal static void ApplyDecision(DesignReviewEntity e, DesignOwnerDecision d)
    {
        e.Decision = d.Decision.ToString();
        e.DecisionNote = d.Decision == DesignDecision.ChangesAsked ? Clip(d.Note?.Trim(), 4000) : null;
        e.DecidedBy = Clip(d.DecidedBy, 256);
        e.DecidedAtUtc = d.DecidedAtUtc;
        e.UpdatedAtUtc = d.DecidedAtUtc;
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
    private IQueryable<DesignReviewEntity> Of(string tenantId) => db.DesignReviews.IgnoreQueryFilters().Where(r => r.TenantId == tenantId);

    public async Task<DesignReview?> GetAsync(string tenantId, Guid sessionId, int run, CancellationToken ct = default)
    {
        var row = await Of(tenantId).AsNoTracking().FirstOrDefaultAsync(r => r.SessionId == sessionId && r.Run == run, ct).ConfigureAwait(false);
        return row is null ? null : DesignReviewRows.ToDomain(row);
    }

    public async Task<DesignReview?> LatestAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var row = await Of(tenantId).AsNoTracking().Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.Run).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : DesignReviewRows.ToDomain(row);
    }

    public async Task<IReadOnlyList<DesignReview>> ListForTaskAsync(string tenantId, Guid sessionId, CancellationToken ct = default)
    {
        var rows = await Of(tenantId).AsNoTracking().Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.Run).Take(100).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(DesignReviewRows.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<DesignReview>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default)
    {
        var rows = await Of(tenantId).AsNoTracking()
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Take(Math.Clamp(max, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(DesignReviewRows.ToDomain).ToList();
    }

    public async Task<DesignReview> EnsureAsync(DesignReview fresh, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (string.IsNullOrWhiteSpace(fresh.TenantId)) { throw new InvalidOperationException("A design review needs a tenant."); }
        if (await GetAsync(fresh.TenantId, fresh.SessionId, fresh.Run, ct).ConfigureAwait(false) is { } existing) { return existing; }
        var row = DesignReviewRows.NewRow(fresh);
        db.DesignReviews.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return DesignReviewRows.ToDomain(row);
        }
        catch (DbUpdateException)
        {
            // Someone else created it at the same moment (the unique (tenant, task, run) index): theirs stands.
            db.Entry(row).State = EntityState.Detached;
            return await GetAsync(fresh.TenantId, fresh.SessionId, fresh.Run, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The design review couldn't be stored.");
        }
    }

    public async Task<DesignReview?> SaveVerdictAsync(string tenantId, Guid sessionId, int run, DesignVerdictUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Of(tenantId).FirstOrDefaultAsync(r => r.SessionId == sessionId && r.Run == run, ct).ConfigureAwait(false);
        if (row is null) { return null; }
        // Tracked: EF writes only the columns ApplyVerdict changed — never Decision*, whatever the owner did meanwhile.
        DesignReviewRows.ApplyVerdict(row, update, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DesignReviewRows.ToDomain(row);
    }

    public async Task<DesignReview?> DecideAsync(string tenantId, Guid sessionId, int run, DesignOwnerDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var row = await Of(tenantId).FirstOrDefaultAsync(r => r.SessionId == sessionId && r.Run == run, ct).ConfigureAwait(false);
        if (row is null) { return null; }
        DesignReviewRows.ApplyDecision(row, decision);
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
    private readonly Lock _gate = new();

    private DesignReview? Read((string, Guid, int) key)
    {
        lock (_gate) { return _rows.TryGetValue(key, out var row) ? DesignReviewRows.ToDomain(row) : null; }
    }

    public Task<DesignReview?> GetAsync(string tenantId, Guid sessionId, int run, CancellationToken ct = default) =>
        Task.FromResult(Read((tenantId, sessionId, run)));

    public Task<DesignReview?> LatestAsync(string tenantId, Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult(ForTask(tenantId, sessionId).FirstOrDefault());

    public Task<IReadOnlyList<DesignReview>> ListForTaskAsync(string tenantId, Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DesignReview>>(ForTask(tenantId, sessionId));

    private List<DesignReview> ForTask(string tenantId, Guid sessionId)
    {
        lock (_gate)
        {
            return _rows.Where(kv => kv.Key.Tenant == tenantId && kv.Key.Session == sessionId)
                .OrderByDescending(kv => kv.Key.Run).Select(kv => DesignReviewRows.ToDomain(kv.Value)).ToList();
        }
    }

    public Task<IReadOnlyList<DesignReview>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DesignReview>>(_rows.Where(kv => kv.Key.Tenant == tenantId)
                .Select(kv => kv.Value).OrderByDescending(r => r.UpdatedAtUtc).Take(Math.Clamp(max, 1, 200))
                .Select(DesignReviewRows.ToDomain).ToList());
        }
    }

    public Task<DesignReview> EnsureAsync(DesignReview fresh, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (string.IsNullOrWhiteSpace(fresh.TenantId)) { throw new InvalidOperationException("A design review needs a tenant."); }
        lock (_gate)
        {
            var key = (fresh.TenantId, fresh.SessionId, fresh.Run);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = DesignReviewRows.NewRow(fresh);
                _rows[key] = row;
                Trim();
            }
            return Task.FromResult(DesignReviewRows.ToDomain(row));
        }
    }

    public Task<DesignReview?> SaveVerdictAsync(string tenantId, Guid sessionId, int run, DesignVerdictUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (!_rows.TryGetValue((tenantId, sessionId, run), out var row)) { return Task.FromResult<DesignReview?>(null); }
            DesignReviewRows.ApplyVerdict(row, update, DateTimeOffset.UtcNow);
            return Task.FromResult<DesignReview?>(DesignReviewRows.ToDomain(row));
        }
    }

    public Task<DesignReview?> DecideAsync(string tenantId, Guid sessionId, int run, DesignOwnerDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            if (!_rows.TryGetValue((tenantId, sessionId, run), out var row)) { return Task.FromResult<DesignReview?>(null); }
            DesignReviewRows.ApplyDecision(row, decision);
            return Task.FromResult<DesignReview?>(DesignReviewRows.ToDomain(row));
        }
    }

    // Bounded: past MaxReviews the oldest go (called under the gate).
    private void Trim()
    {
        if (_rows.Count <= MaxReviews) { return; }
        foreach (var stale in _rows.OrderBy(kv => kv.Value.UpdatedAtUtc).Take(_rows.Count - MaxReviews).Select(kv => kv.Key).ToList())
        {
            _rows.TryRemove(stale, out _);
        }
    }
}
