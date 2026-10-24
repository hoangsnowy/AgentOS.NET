// IBoardRepos over the workspace registry: a board's repositories as "owner/repo" keys (the key project memory uses).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Workspaces.Persistence;

namespace AgentOs.Modules.Workspaces;

internal sealed class WorkspaceBoardRepos(IWorkspaceRepository workspaces) : IBoardRepos
{
    public async Task<IReadOnlyList<string>> RepoKeysAsync(string tenantId, Guid boardId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) { return []; }
        var repos = await workspaces.ListReposForTenantAsync(tenantId, boardId, ct).ConfigureAwait(false);
        return repos.Where(r => r.Owner.Length > 0 && r.Repo.Length > 0).Select(r => $"{r.Owner}/{r.Repo}")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
