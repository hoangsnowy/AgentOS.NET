// The repositories a board works on, as "owner/repo" keys — what project memory (lessons, practices) is keyed by. Lets
// an agent that sees a whole board (the Autopilot lead) read the practices of its projects without depending on the
// Workspaces module. Tenant-explicit: background runs have no request context.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Workspaces;

/// <summary>Lists a board's repositories.</summary>
public interface IBoardRepos
{
    /// <summary>The <c>owner/repo</c> keys of board <paramref name="boardId"/> in <paramref name="tenantId"/> (empty when unknown).</summary>
    Task<IReadOnlyList<string>> RepoKeysAsync(string tenantId, Guid boardId, CancellationToken ct = default);
}
