// Approval policies (E1.5) — the two stores of approval requests: EF Core over sessions.approvals, and a process-local map
// for the no-database path (standalone Development, its E2E). Every EF query is tenant-explicit (IgnoreQueryFilters + a
// TenantId predicate — a background pass or a Blazor circuit has no ambient HTTP tenant), with RLS as the second wall.
// RLS stage 2 (ADR-0012): a flow with no tenant of its own (the Autopilot pass, a circuit event that lost its identity)
// sees NOTHING, so every EF path binds the flow to the tenant it names — TenantScopeIfUnbound, which fills a missing
// tenant and never overrides the flow's own (a flow bound to tenant B that names A still sees nothing of A).
// A decision is a narrow conditional write: only a PENDING request changes, so two approvers clicking at once (or a
// stale page) can never flip a decision that was already made.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence.Repositories;

/// <summary>Maps an approval request to and from its row.</summary>
internal static class ApprovalRows
{
    internal static ApprovalRequest ToDomain(ApprovalEntity e) => new(
        e.Id, e.TenantId, e.BoardId, e.SessionId, e.IssueNumber,
        Enum.TryParse<ApprovalGate>(e.Gate, out var gate) ? gate : ApprovalGate.Merge,
        e.Run,
        // An unreadable status reads as pending: it never counts as an approval.
        Enum.TryParse<ApprovalStatus>(e.Status, out var status) ? status : ApprovalStatus.Pending,
        e.Title, e.Link, e.RequestedBy, e.RequestedAtUtc, e.DecidedBy, e.DecidedAtUtc, e.Note);

    internal static ApprovalEntity NewRow(ApprovalRequest r) => new()
    {
        Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
        TenantId = r.TenantId,
        BoardId = r.BoardId,
        SessionId = r.SessionId,
        IssueNumber = r.IssueNumber,
        Gate = r.Gate.ToString(),
        Run = r.Run,
        // A new request is always pending — a caller can't store a pre-approved one.
        Status = nameof(ApprovalStatus.Pending),
        Title = Clip(r.Title, 300) ?? string.Empty,
        Link = Clip(r.Link, 2048),
        RequestedBy = Clip(r.RequestedBy, 256) ?? string.Empty,
        RequestedAtUtc = r.RequestedAtUtc,
    };

    /// <summary>Apply a decision to a pending row; false (unchanged) when it was already decided.</summary>
    internal static bool Apply(ApprovalEntity e, ApprovalDecision d)
    {
        if (!string.Equals(e.Status, nameof(ApprovalStatus.Pending), StringComparison.Ordinal)) { return false; }
        if (string.IsNullOrWhiteSpace(d.DecidedBy)) { throw new InvalidOperationException("A decision needs the person who made it."); }
        e.Status = d.Approve ? nameof(ApprovalStatus.Approved) : nameof(ApprovalStatus.Rejected);
        e.DecidedBy = Clip(d.DecidedBy.Trim(), 256);
        e.DecidedAtUtc = d.AtUtc;
        e.Note = Clip(d.Note?.Trim() is { Length: > 0 } n ? n : null, ApprovalRequest.MaxNote);
        return true;
    }

    internal static void EnsureTenant(ApprovalRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (string.IsNullOrWhiteSpace(r.TenantId)) { throw new InvalidOperationException("An approval request needs a tenant."); }
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary><see cref="IApprovalStore"/> over <c>sessions.approvals</c>.</summary>
internal sealed class EfApprovalStore(SessionsDbContext db) : IApprovalStore
{
    private IQueryable<ApprovalEntity> Of(string tenantId) => db.Approvals.IgnoreQueryFilters().Where(r => r.TenantId == tenantId);

    public async Task<ApprovalRequest?> GetAsync(string tenantId, Guid sessionId, ApprovalGate gate, int run, CancellationToken ct = default)
    {
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var name = gate.ToString();
        var row = await Of(tenantId).AsNoTracking().FirstOrDefaultAsync(r => r.SessionId == sessionId && r.Gate == name && r.Run == run, ct)
            .ConfigureAwait(false);
        return row is null ? null : ApprovalRows.ToDomain(row);
    }

    public async Task<ApprovalRequest?> FindAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var row = await Of(tenantId).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : ApprovalRows.ToDomain(row);
    }

    public async Task<ApprovalRequest> RequestAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        ApprovalRows.EnsureTenant(request);
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(request.TenantId);
        if (await GetAsync(request.TenantId, request.SessionId, request.Gate, request.Run, ct).ConfigureAwait(false) is { } existing) { return existing; }
        var row = ApprovalRows.NewRow(request);
        db.Approvals.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ApprovalRows.ToDomain(row);
        }
        catch (DbUpdateException)
        {
            // Another pass asked at the same moment (the unique (tenant, task, gate, run) index): theirs stands.
            db.Entry(row).State = EntityState.Detached;
            return await GetAsync(request.TenantId, request.SessionId, request.Gate, request.Run, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The approval request couldn't be stored.");
        }
    }

    public async Task<ApprovalRequest?> DecideAsync(string tenantId, Guid id, ApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var row = await Of(tenantId).FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (row is null) { return null; }
        if (!ApprovalRows.Apply(row, decision)) { return ApprovalRows.ToDomain(row); }
        // Conditional write: the row is still pending in the database, or nothing changes (someone decided first).
        var pending = nameof(ApprovalStatus.Pending);
        var changed = await db.Approvals.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.Id == id && r.Status == pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, row.Status)
                .SetProperty(r => r.DecidedBy, row.DecidedBy)
                .SetProperty(r => r.DecidedAtUtc, row.DecidedAtUtc)
                .SetProperty(r => r.Note, row.Note), ct)
            .ConfigureAwait(false);
        db.Entry(row).State = EntityState.Detached;
        return changed == 1 ? ApprovalRows.ToDomain(row) : await FindAsync(tenantId, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default)
    {
        using var bound = RowLevelSecurity.TenantScopeIfUnbound(tenantId);
        var rows = await Of(tenantId).AsNoTracking()
            .OrderByDescending(r => r.RequestedAtUtc)
            .Take(Math.Clamp(max, 1, 200))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ApprovalRows.ToDomain).ToList();
    }
}

/// <summary><see cref="IApprovalStore"/> in process memory (no database): keyed by (tenant, task, gate, run), bounded.</summary>
internal sealed class InMemoryApprovalStore : IApprovalStore
{
    /// <summary>Most requests kept (all tenants) — the oldest go first.</summary>
    internal const int MaxRequests = 2000;

    private readonly ConcurrentDictionary<(string Tenant, Guid Session, ApprovalGate Gate, int Run), ApprovalEntity> _rows = new();
    private readonly Lock _gate = new();

    public Task<ApprovalRequest?> GetAsync(string tenantId, Guid sessionId, ApprovalGate gate, int run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue((tenantId, sessionId, gate, run), out var row) ? ApprovalRows.ToDomain(row) : null);
        }
    }

    public Task<ApprovalRequest?> FindAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.Values.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id);
            return Task.FromResult(row is null ? null : ApprovalRows.ToDomain(row));
        }
    }

    public Task<ApprovalRequest> RequestAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        ApprovalRows.EnsureTenant(request);
        lock (_gate)
        {
            var key = (request.TenantId, request.SessionId, request.Gate, request.Run);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = ApprovalRows.NewRow(request);
                _rows[key] = row;
                foreach (var old in _rows.OrderBy(kv => kv.Value.RequestedAtUtc).Take(Math.Max(0, _rows.Count - MaxRequests)).Select(kv => kv.Key).ToList())
                {
                    _rows.TryRemove(old, out _);
                }
            }
            return Task.FromResult(ApprovalRows.ToDomain(row));
        }
    }

    public Task<ApprovalRequest?> DecideAsync(string tenantId, Guid id, ApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            var row = _rows.Values.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id);
            if (row is null) { return Task.FromResult<ApprovalRequest?>(null); }
            ApprovalRows.Apply(row, decision);
            return Task.FromResult<ApprovalRequest?>(ApprovalRows.ToDomain(row));
        }
    }

    public Task<IReadOnlyList<ApprovalRequest>> ListRecentAsync(string tenantId, int max = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ApprovalRequest>>(_rows.Values.Where(r => r.TenantId == tenantId)
                .OrderByDescending(r => r.RequestedAtUtc).Take(Math.Clamp(max, 1, 200)).Select(ApprovalRows.ToDomain).ToList());
        }
    }
}
