// Epic E5 — Default evidence sink: a per-tenant ring buffer kept entirely in memory. Bounded so
// a runaway loop can't OOM the host. Production hosts swap this for an EF-backed implementation
// that streams rows into the tools schema; the in-memory variant is what dev / CI sees.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;

namespace AgentOs.Modules.Tools.Evidence;

internal sealed class InMemoryToolInvocationLog : IToolInvocationLog
{
    private const int PerTenantCap = 500;

    private readonly ConcurrentDictionary<string, LinkedList<ToolInvocationEvidence>> _byTenant = new(StringComparer.Ordinal);

    public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bucket = _byTenant.GetOrAdd(entry.TenantId, _ => new LinkedList<ToolInvocationEvidence>());
        lock (bucket)
        {
            bucket.AddFirst(entry);
            while (bucket.Count > PerTenantCap)
            {
                bucket.RemoveLast();
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !_byTenant.TryGetValue(tenantId, out var bucket))
        {
            return Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Array.Empty<ToolInvocationEvidence>());
        }

        ToolInvocationEvidence[] snapshot;
        lock (bucket)
        {
            snapshot = bucket.Take(Math.Max(1, limit)).ToArray();
        }
        return Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(snapshot);
    }

    public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId, ToolEvidenceFilter filter, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>(Snapshot(tenantId).Where(filter.Matches).Take(Math.Max(1, limit)).ToArray());
    }

    public Task<IReadOnlyList<ToolCallsByRole>> SummarizeMemberComputerAsync(
        string tenantId, DateTimeOffset? sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolCallsByRole.Fold(Snapshot(tenantId), ToolEvidenceOrigins.MemberComputer, sinceUtc));

    // The tenant's whole bucket, newest first (bounded by PerTenantCap).
    private ToolInvocationEvidence[] Snapshot(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !_byTenant.TryGetValue(tenantId, out var bucket))
        {
            return [];
        }
        lock (bucket)
        {
            return [.. bucket];
        }
    }
}
