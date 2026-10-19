// Scheduled runs — the store contract, the record the UI reads, and the tenant-explicit service the Home app calls
// (a Blazor circuit has no HttpContext, so the tenant is always passed in). Persistence is EF + Postgres
// (pipeline.scheduled_runs, tenant query filter + RLS) with an in-memory fallback for the stateless standalone Web.

namespace AgentOs.Modules.Pipeline.Scheduling;

/// <summary>A scheduled run as the UI shows it.</summary>
public sealed record ScheduledRunInfo(
    Guid Id,
    string Story,
    ScheduleSpec Spec,
    bool Enabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc,
    string? LastStatus,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

/// <summary>A due run the worker claimed.</summary>
/// <param name="TenantId">Owner tenant.</param>
/// <param name="Run">The schedule.</param>
/// <param name="FiresAtUtc">The occurrence being run.</param>
public sealed record DueRun(string TenantId, ScheduledRunInfo Run, DateTimeOffset FiresAtUtc);

/// <summary>Persistence for scheduled runs. All UI methods are tenant-explicit.</summary>
public interface IScheduledRunStore
{
    /// <summary>The tenant's schedules, soonest first (disabled last).</summary>
    Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Insert a schedule.</summary>
    Task AddAsync(string tenantId, ScheduledRunInfo run, CancellationToken ct = default);

    /// <summary>Pause / resume; recomputes the next fire time on resume. False when not found.</summary>
    Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Delete. False when not found.</summary>
    Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Claim every enabled schedule due at <paramref name="now"/>, across tenants (the worker is the only caller):
    /// each claim atomically moves NextRunAtUtc to the following occurrence (or clears it for a one-off) under an
    /// optimistic check, so two hosts / replicas never run the same occurrence twice.
    /// </summary>
    Task<IReadOnlyList<DueRun>> ClaimDueAsync(DateTimeOffset now, int max, CancellationToken ct = default);

    /// <summary>Record how a claimed occurrence ended.</summary>
    Task RecordOutcomeAsync(string tenantId, Guid id, DateTimeOffset ranAtUtc, string status, CancellationToken ct = default);
}
