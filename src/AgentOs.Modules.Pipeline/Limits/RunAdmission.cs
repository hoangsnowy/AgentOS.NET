// Per-tenant run admission (E0.6). Two limits, both read live from Settings (platform scope — a tenant must not
// be able to raise its own ceiling):
//   • concurrent runs  — an Interlocked counter per tenant, decremented when the lease is disposed;
//   • runs per minute  — a sliding 60 s window per tenant: a queue of start timestamps, pruned on each admit
//                        (O(1) amortised, memory bounded by the limit).
// One ConcurrentDictionary entry per tenant; each tenant's window has its own lock, so tenants never contend.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Pipeline;

namespace AgentOs.Modules.Pipeline.Limits;

/// <summary>Declares the run limits.</summary>
public sealed class RunLimitSettings : ISettingsContributor
{
    /// <summary>UI category.</summary>
    public const string Category = "Platform";

    /// <summary>Most runs one tenant may have in flight at once.</summary>
    public const string MaxConcurrent = "Pipeline:Limits:MaxConcurrentRuns";

    /// <summary>Most runs one tenant may start per minute.</summary>
    public const string PerMinute = "Pipeline:Limits:RunsPerMinute";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(MaxConcurrent, SettingKind.WholeNumber, Category, "Concurrent runs per tenant")
        {
            Scope = SettingScope.Platform, DefaultValue = "4", Min = 1, Max = 100,
            Help = "Pipeline, Workflow and Quick runs one tenant may have in flight at once. Protects other tenants from one tenant's load.",
        };
        yield return new(PerMinute, SettingKind.WholeNumber, Category, "Runs per minute per tenant")
        {
            Scope = SettingScope.Platform, DefaultValue = "30", Min = 1, Max = 1000,
            Help = "Burst limit on starting runs. The monthly budget cap is separate and still applies.",
        };
    }
}

/// <summary>In-process <see cref="IRunAdmission"/>. Singleton.</summary>
public sealed class RunAdmission(ISettings? settings = null, TimeProvider? clock = null) : IRunAdmission
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, TenantState> _tenants = new(StringComparer.Ordinal);

    private sealed class TenantState
    {
        public int Running;
        public readonly Queue<long> Starts = new();
        public readonly Lock Gate = new();
    }

    /// <inheritdoc />
    public async ValueTask<RunAdmissionLease> AdmitAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var maxConcurrent = settings is null ? 4 : await settings.GetIntAsync(RunLimitSettings.MaxConcurrent, null, 4, cancellationToken).ConfigureAwait(false);
        var perMinute = settings is null ? 30 : await settings.GetIntAsync(RunLimitSettings.PerMinute, null, 30, cancellationToken).ConfigureAwait(false);
        return Admit(tenantId, Math.Max(1, maxConcurrent), Math.Max(1, perMinute));
    }

    internal RunAdmissionLease Admit(string tenantId, int maxConcurrent, int perMinute)
    {
        var state = _tenants.GetOrAdd(tenantId, static _ => new TenantState());
        var now = _clock.GetTimestamp();
        lock (state.Gate)
        {
            while (state.Starts.Count > 0 && _clock.GetElapsedTime(state.Starts.Peek(), now) >= Window)
            {
                state.Starts.Dequeue();
            }
            if (state.Running >= maxConcurrent)
            {
                return RunAdmissionLease.Deny(
                    $"This workspace already has {state.Running} run(s) in progress (limit {maxConcurrent}). Wait for one to finish, or ask a platform admin to raise the limit.");
            }
            if (state.Starts.Count >= perMinute)
            {
                return RunAdmissionLease.Deny(
                    $"This workspace started {state.Starts.Count} runs in the last minute (limit {perMinute}). Try again shortly.");
            }
            state.Running++;
            state.Starts.Enqueue(now);
        }
        return RunAdmissionLease.Grant(() => { lock (state.Gate) { state.Running--; } });
    }

    /// <summary>Runs in flight for <paramref name="tenantId"/> (diagnostics / About).</summary>
    public int Running(string tenantId) => _tenants.TryGetValue(tenantId, out var s) ? Volatile.Read(ref s.Running) : 0;
}
