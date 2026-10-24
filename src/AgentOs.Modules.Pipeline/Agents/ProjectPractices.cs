// The project's practices (what the Retrospective agent distilled from reviews, failing checks and acceptance tests),
// read by every agent that works on a project: Coding (tickets), Reviewer, Testing (tickets) and the Autopilot lead.
// One helper so they all read the same list the same way — bounded, fenced as data, best-effort.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Reads a project's practices as a prompt block.</summary>
public static class ProjectPractices
{
    /// <summary>Most repositories whose practices one prompt carries (a board's agent reads at most this many).</summary>
    public const int MaxRepos = 5;

    /// <summary>
    /// The practices of <paramref name="repoKeys"/> (<c>owner/repo</c>) as one <see cref="ProjectMemory.PracticesBlock"/>,
    /// or empty when there are none. Never throws (a memory failure is no practices); strictly per tenant.
    /// </summary>
    public static async Task<string> BlockAsync(IMemoryStore? memory, string tenantId, IEnumerable<string> repoKeys,
        ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repoKeys);
        ArgumentNullException.ThrowIfNull(logger);
        if (memory is null || string.IsNullOrWhiteSpace(tenantId)) { return string.Empty; }
        try
        {
            var all = new List<MemoryNode>();
            foreach (var repo in repoKeys.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRepos))
            {
                all.AddRange(await memory.ListAsync(tenantId, repo, MemoryKinds.Practice, RetrospectiveAgent.MaxPractices, ct).ConfigureAwait(false));
            }
            return ProjectMemory.PracticesBlock(all);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the project's practices failed; the agent works without them.");
            return string.Empty;
        }
    }
}
