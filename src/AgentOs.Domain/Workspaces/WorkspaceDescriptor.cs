// M2 — the resolved, ready-to-use coordinates of a connected repository, handed to an
// ISourceProvider for a single operation. This is the transient runtime view: it carries the
// access token (resolved just-in-time from the encrypted store), and is NEVER persisted — the
// Workspaces module stores only a CredentialRef and rehydrates the token per call.

using System;

namespace AgentOs.Domain.Workspaces;

/// <summary>
/// Runtime coordinates of a connected repo for one provider operation. The <see cref="AccessToken"/>
/// is resolved per call and must not be logged or persisted.
/// </summary>
/// <param name="Id">Workspace id (correlates to the persisted row), or <see cref="Guid.Empty"/> for an unsaved probe.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="Kind">Which provider backs this repo.</param>
/// <param name="Owner">GitHub owner / org, or Azure DevOps organization.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Project">Azure DevOps project (ignored by GitHub).</param>
/// <param name="DefaultBranch">Base branch (e.g. <c>main</c>).</param>
/// <param name="AccessToken">PAT / OAuth token, resolved just-in-time. Transient.</param>
/// <param name="Host">Base host for enterprise/self-hosted (e.g. <c>https://github.example.com</c>); null = the provider's public host.</param>
public sealed record WorkspaceDescriptor(
    Guid Id,
    string TenantId,
    SourceProviderKind Kind,
    string Owner,
    string Repo,
    string? Project,
    string DefaultBranch,
    string AccessToken,
    string? Host = null)
{
    /// <summary>The external tracker a ticket call addresses (E1.8) — set by the tracker routing when it hands a ticket
    /// read/write to the Azure DevOps Boards or Jira provider. Null for a repository call.</summary>
    public TrackerBinding? Tracker { get; init; }

    /// <summary>Validates the required coordinates. Throws <see cref="ArgumentException"/> if invalid.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(TenantId));
        }
        if (string.IsNullOrWhiteSpace(Owner))
        {
            throw new ArgumentException("Owner is required.", nameof(Owner));
        }
        if (string.IsNullOrWhiteSpace(Repo))
        {
            throw new ArgumentException("Repo is required.", nameof(Repo));
        }
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new ArgumentException("AccessToken is required.", nameof(AccessToken));
        }
        if (Kind == SourceProviderKind.AzureDevOps && string.IsNullOrWhiteSpace(Project))
        {
            throw new ArgumentException("Project is required for Azure DevOps.", nameof(Project));
        }
    }

    /// <summary>
    /// Build a repo-level descriptor for one repo under a <paramref name="board"/>. This is the
    /// insulation point that lets per-repo provider calls (validate, read-context, PR creation) keep
    /// their existing signatures while the workspace is now a board that spans many repos.
    /// </summary>
    public static WorkspaceDescriptor ForRepo(BoardDescriptor board, string owner, string repo, string defaultBranch)
    {
        ArgumentNullException.ThrowIfNull(board);
        return new WorkspaceDescriptor(
            board.Id, board.TenantId, board.Kind, owner, repo, board.Project,
            string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch, board.AccessToken, board.Host);
    }
}
