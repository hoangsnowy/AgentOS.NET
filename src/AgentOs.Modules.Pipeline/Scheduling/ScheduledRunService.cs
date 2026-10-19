// The tenant-explicit API the Home app uses to create, pause and delete scheduled runs. Validates the story and the
// schedule (≥ 15-minute cadence, future one-offs), caps schedules per tenant, and records every change in the audit
// trail (Settings → Activity log) as a "Pipeline:Schedule:<id>" entry — creating a schedule commits future spend.

using AgentOs.Domain.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Pipeline.Scheduling;

/// <summary>Result of a schedule write.</summary>
public sealed record ScheduleWriteResult(bool Ok, string? Error, ScheduledRunInfo? Run = null)
{
    /// <summary>A rejected write with a user-facing reason.</summary>
    public static ScheduleWriteResult Fail(string error) => new(false, error);
}

/// <summary>Scheduled runs for the desktop (tenant passed explicitly — a circuit has no HttpContext).</summary>
public interface IScheduledRunService
{
    /// <summary>The tenant's schedules.</summary>
    Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Create a schedule for <paramref name="story"/>.</summary>
    Task<ScheduleWriteResult> CreateAsync(string tenantId, string story, ScheduleSpec spec, string actor, CancellationToken ct = default);

    /// <summary>Pause or resume.</summary>
    Task<ScheduleWriteResult> SetEnabledAsync(string tenantId, Guid id, bool enabled, string actor, CancellationToken ct = default);

    /// <summary>Delete.</summary>
    Task<ScheduleWriteResult> DeleteAsync(string tenantId, Guid id, string actor, CancellationToken ct = default);
}

internal sealed class ScheduledRunService(IScheduledRunStore store, TimeProvider clock, IServiceProvider services) : IScheduledRunService
{
    /// <summary>Most schedules one tenant may hold (each is recurring spend).</summary>
    public const int MaxPerTenant = 20;

    /// <summary>Longest story accepted.</summary>
    public const int MaxStoryLength = 4000;

    public Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(string tenantId, CancellationToken ct = default) => store.ListAsync(tenantId, ct);

    public async Task<ScheduleWriteResult> CreateAsync(string tenantId, string story, ScheduleSpec spec, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(spec);
        var text = story?.Trim() ?? string.Empty;
        if (text.Length == 0) { return ScheduleWriteResult.Fail("Describe what the agents should build."); }
        if (text.Length > MaxStoryLength) { return ScheduleWriteResult.Fail($"Keep the story under {MaxStoryLength} characters."); }
        var now = clock.GetUtcNow();
        if (spec.Validate(now) is { } error) { return ScheduleWriteResult.Fail(error); }
        if ((await store.ListAsync(tenantId, ct).ConfigureAwait(false)).Count >= MaxPerTenant)
        {
            return ScheduleWriteResult.Fail($"A workspace can hold {MaxPerTenant} schedules — delete one first.");
        }

        var run = new ScheduledRunInfo(Guid.NewGuid(), text, spec, Enabled: true, spec.NextAfter(now), null, null,
            string.IsNullOrWhiteSpace(actor) ? "admin" : actor, now);
        await store.AddAsync(tenantId, run, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, run.Id, actor, null, $"{spec.Describe()} · {Preview(text)}", ct).ConfigureAwait(false);
        return new ScheduleWriteResult(true, null, run);
    }

    public async Task<ScheduleWriteResult> SetEnabledAsync(string tenantId, Guid id, bool enabled, string actor, CancellationToken ct = default)
    {
        if (!await store.SetEnabledAsync(tenantId, id, enabled, clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            return ScheduleWriteResult.Fail("That schedule no longer exists.");
        }
        await AuditAsync(tenantId, id, actor, enabled ? "paused" : "active", enabled ? "active" : "paused", ct).ConfigureAwait(false);
        return new ScheduleWriteResult(true, null);
    }

    public async Task<ScheduleWriteResult> DeleteAsync(string tenantId, Guid id, string actor, CancellationToken ct = default)
    {
        if (!await store.DeleteAsync(tenantId, id, ct).ConfigureAwait(false))
        {
            return ScheduleWriteResult.Fail("That schedule no longer exists.");
        }
        await AuditAsync(tenantId, id, actor, "scheduled", null, ct).ConfigureAwait(false);
        return new ScheduleWriteResult(true, null);
    }

    private static string Preview(string story) => story.Length <= 80 ? story : story[..80] + "…";

    // The audit sink lives in the Tenants module (optional here: absent in a bare Pipeline host).
    private ValueTask AuditAsync(string tenantId, Guid id, string actor, string? oldValue, string? newValue, CancellationToken ct) =>
        services.GetService<ISettingsAuditSink>() is { } sink
            ? sink.RecordAsync(new SettingChange($"Pipeline:Schedule:{id:N}", SettingScope.Tenant, tenantId,
                string.IsNullOrWhiteSpace(actor) ? "admin" : actor, oldValue, newValue, clock.GetUtcNow()), ct)
            : ValueTask.CompletedTask;
}
