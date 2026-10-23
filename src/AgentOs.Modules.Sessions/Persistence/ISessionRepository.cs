// M3 — session persistence contract, scoped to the current tenant by the DbContext query filter.
// Writes stamp TenantId from ITenantContext.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.SharedKernel.Persistence;

namespace AgentOs.Modules.Sessions.Persistence;

/// <summary>CRUD for remote sessions (member × workspace), scoped to the current tenant.</summary>
public interface ISessionRepository
{
    /// <summary>List the current tenant's sessions, newest first, bounded by offset/limit pagination.</summary>
    Task<IReadOnlyList<RemoteSessionEntity>> ListAsync(int limit = Page.DefaultLimit, int offset = 0, CancellationToken ct = default);
    Task<RemoteSessionEntity?> GetAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(RemoteSessionEntity session, CancellationToken ct = default);

    /// <summary>Mark a session Closed with the given timestamp. Returns false if not found in this tenant.</summary>
    Task<bool> CloseAsync(Guid id, DateTimeOffset closedAtUtc, CancellationToken ct = default);

    // ---- Tenant-explicit overloads: bypass the ambient query filter for callers without an
    // ITenantContext (a Blazor Server circuit has no HttpContext). The entity passed to
    // AddForTenantAsync must already carry its TenantId. ----

    Task<IReadOnlyList<RemoteSessionEntity>> ListForTenantAsync(string tenantId, CancellationToken ct = default);
    Task AddForTenantAsync(RemoteSessionEntity session, CancellationToken ct = default);
    Task<bool> CloseForTenantAsync(string tenantId, Guid id, DateTimeOffset closedAtUtc, CancellationToken ct = default);

    /// <summary>Updates status + run-result fields after the issue-work agent completes (M5).
    /// Bypasses the ambient query filter — tenant must be supplied explicitly.</summary>
    Task<bool> UpdateRunResultAsync(
        string tenantId, Guid id, string status, string? prUrl, string? errorMessage,
        CancellationToken ct = default);

    // ---- Multi-repo: the repos a session targets, each with its own branch / PR / status. ----

    /// <summary>List a session's target repos, for an explicit tenant.</summary>
    Task<IReadOnlyList<SessionRepoEntity>> ListReposForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default);

    /// <summary>List ALL session-repo rows for a tenant (the Sessions tab groups them by session).</summary>
    Task<IReadOnlyList<SessionRepoEntity>> ListAllReposForTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Persist a target repo under a session (its <see cref="SessionRepoEntity.TenantId"/> is set by the caller).</summary>
    Task AddRepoForTenantAsync(SessionRepoEntity repo, CancellationToken ct = default);

    /// <summary>Write back one repo's run result (status + branch + PR + error).</summary>
    Task<bool> UpdateRepoRunResultAsync(
        string tenantId, Guid sessionRepoId, string status, string? branchName, string? prUrl, string? errorMessage,
        CancellationToken ct = default);

    /// <summary>Roll the child repo statuses up into the parent session: any Failed → Failed; all Done → Done;
    /// else Running. Also mirrors the first repo's PR onto the parent for back-compat display.</summary>
    Task<bool> RecomputeSessionStatusForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Count one more round of fixes on the task (its change went back to the Coding agent).</summary>
    Task IncrementFixRoundsForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Take back a round that never happened (the member's AI plan was at its limit) — never below zero.</summary>
    Task DecrementFixRoundsForTenantAsync(string tenantId, Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Delivery of the tenant's tasks merged since <paramref name="since"/> (null = all time).</summary>
    Task<DeliverySummary> GetDeliveryForTenantAsync(string tenantId, DateTimeOffset? since = null, CancellationToken ct = default) =>
        Task.FromResult(DeliverySummary.Empty);
}
