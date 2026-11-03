// GAP-11 — where invitations live. EF (Postgres, tenants.invitations, under RLS) when a database is configured; a
// process-local store for the stateless standalone Web, with the same semantics. Every state change is ONE conditional
// write, so two people racing for one link can't both get in, and a revoke can't be overwritten by an accept:
//   • TryClaim: pending → consumed (… WHERE not accepted AND not revoked AND not expired) — the accept's first step;
//   • Release: undo a claim whose account creation failed (only while no account is bound to it);
//   • Complete: bind the created account to the consumed invitation;
//   • TryRevoke: pending/expired → revoked (… WHERE not accepted AND not revoked).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.Modules.Tenants.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Tenants.Invitations;

/// <summary>One invitation as stored.</summary>
internal sealed record InvitationRecord(
    Guid Id,
    string TenantId,
    string Email,
    string EmailNormalized,
    string Role,
    string? CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? AcceptedAtUtc = null,
    string? AcceptedUserId = null,
    DateTimeOffset? RevokedAtUtc = null,
    string? RevokedBy = null)
{
    public InvitationStatus StatusAt(DateTimeOffset now) => InvitationRules.StatusOf(ExpiresAtUtc, AcceptedAtUtc, RevokedAtUtc, now);

    public InvitationInfo ToInfo(DateTimeOffset now) =>
        new(Id, TenantId, Email, Role, CreatedBy, CreatedAtUtc, ExpiresAtUtc, AcceptedAtUtc, RevokedAtUtc, StatusAt(now));
}

/// <summary>Invitation persistence (see the file header).</summary>
internal interface IInvitationStore
{
    Task AddAsync(InvitationRecord invitation, CancellationToken ct = default);

    Task<InvitationRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>The tenant's invitations, newest first.</summary>
    Task<IReadOnlyList<InvitationRecord>> ListAsync(string tenantId, int max, CancellationToken ct = default);

    /// <summary>Consume a pending invitation. False when it was already used, revoked or expired — or another accept won.</summary>
    Task<bool> TryClaimAsync(string tenantId, Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Bind the account the accept created to the consumed invitation.</summary>
    Task CompleteAsync(string tenantId, Guid id, string userId, CancellationToken ct = default);

    /// <summary>Undo a claim whose accept failed before an account existed, so the invitee can try again.</summary>
    Task ReleaseAsync(string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Withdraw an invitation that was not used yet. False when it was used or already revoked (or doesn't exist).</summary>
    Task<bool> TryRevokeAsync(string tenantId, Guid id, DateTimeOffset now, string? revokedBy, CancellationToken ct = default);
}

/// <summary>tenants.invitations through the module's DbContext (RLS-bound like every tenant table).</summary>
internal sealed class EfInvitationStore(TenantsDbContext db) : IInvitationStore
{
    public async Task AddAsync(InvitationRecord invitation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var entity = new InvitationEntity
        {
            Id = invitation.Id,
            TenantId = invitation.TenantId,
            Email = invitation.Email,
            EmailNormalized = invitation.EmailNormalized,
            Role = invitation.Role,
            CreatedBy = invitation.CreatedBy,
            CreatedAtUtc = invitation.CreatedAtUtc,
            ExpiresAtUtc = invitation.ExpiresAtUtc,
        };
        db.Invitations.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // The context is shared with the rest of the request's scope: never leave this row tracked (a failed save
            // would otherwise be retried by the next SaveChanges of an unrelated write).
            db.Entry(entity).State = EntityState.Detached;
        }
    }

    public async Task<InvitationRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
        => await db.Invitations.AsNoTracking()
            .Where(i => i.Id == id && i.TenantId == tenantId)
            .Select(ToRecord)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<InvitationRecord>> ListAsync(string tenantId, int max, CancellationToken ct = default)
        => await db.Invitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(Math.Clamp(max, 1, 500))
            .Select(ToRecord)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> TryClaimAsync(string tenantId, Guid id, DateTimeOffset now, CancellationToken ct = default)
        => await db.Invitations
            .Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAtUtc, now), ct)
            .ConfigureAwait(false) == 1;

    public async Task CompleteAsync(string tenantId, Guid id, string userId, CancellationToken ct = default)
        => await db.Invitations
            .Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedAtUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedUserId, userId), ct)
            .ConfigureAwait(false);

    public async Task ReleaseAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        DateTimeOffset? unclaimed = null;
        await db.Invitations
            .Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedUserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAtUtc, unclaimed), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryRevokeAsync(string tenantId, Guid id, DateTimeOffset now, string? revokedBy, CancellationToken ct = default)
        => await db.Invitations
            .Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAtUtc, now).SetProperty(i => i.RevokedBy, revokedBy), ct)
            .ConfigureAwait(false) == 1;

    private static readonly System.Linq.Expressions.Expression<Func<InvitationEntity, InvitationRecord>> ToRecord = i =>
        new InvitationRecord(i.Id, i.TenantId, i.Email, i.EmailNormalized, i.Role, i.CreatedBy, i.CreatedAtUtc, i.ExpiresAtUtc,
            i.AcceptedAtUtc, i.AcceptedUserId, i.RevokedAtUtc, i.RevokedBy);
}

/// <summary>Process-local invitations for a run with no database (the standalone Web) — same rules, one lock.</summary>
internal sealed class InMemoryInvitationStore : IInvitationStore
{
    private readonly ConcurrentDictionary<Guid, InvitationRecord> _rows = new();
    private readonly Lock _gate = new();

    public Task AddAsync(InvitationRecord invitation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        _rows[invitation.Id] = invitation;
        return Task.CompletedTask;
    }

    public Task<InvitationRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
        => Task.FromResult(_rows.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null);

    public Task<IReadOnlyList<InvitationRecord>> ListAsync(string tenantId, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InvitationRecord>>(
            [.. _rows.Values.Where(r => r.TenantId == tenantId).OrderByDescending(r => r.CreatedAtUtc).Take(Math.Clamp(max, 1, 500))]);

    public Task<bool> TryClaimAsync(string tenantId, Guid id, DateTimeOffset now, CancellationToken ct = default)
        => Task.FromResult(Update(tenantId, id,
            r => r.StatusAt(now) == InvitationStatus.Pending,
            r => r with { AcceptedAtUtc = now }));

    public Task CompleteAsync(string tenantId, Guid id, string userId, CancellationToken ct = default)
    {
        _ = Update(tenantId, id, r => r.AcceptedAtUtc is not null, r => r with { AcceptedUserId = userId });
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        _ = Update(tenantId, id, r => r.AcceptedUserId is null, r => r with { AcceptedAtUtc = null });
        return Task.CompletedTask;
    }

    public Task<bool> TryRevokeAsync(string tenantId, Guid id, DateTimeOffset now, string? revokedBy, CancellationToken ct = default)
        => Task.FromResult(Update(tenantId, id,
            r => r.AcceptedAtUtc is null && r.RevokedAtUtc is null,
            r => r with { RevokedAtUtc = now, RevokedBy = revokedBy }));

    private bool Update(string tenantId, Guid id, Func<InvitationRecord, bool> when, Func<InvitationRecord, InvitationRecord> change)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.TenantId != tenantId || !when(row)) { return false; }
            _rows[id] = change(row);
            return true;
        }
    }
}
