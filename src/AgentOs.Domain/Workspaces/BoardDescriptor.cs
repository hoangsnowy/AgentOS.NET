// Board reshape — runtime coordinates of a connected PLANNING BOARD (GitHub Projects v2 today, an
// Azure DevOps board later) handed to an ISourceProvider for a single board operation. This is the
// board-level peer of WorkspaceDescriptor (which stays repo-level): a board spans many repos, so
// per-repo provider calls take WorkspaceDescriptor.ForRepo(board, …). The AccessToken is resolved
// just-in-time from the encrypted store and is NEVER persisted or logged.

using System;

namespace AgentOs.Domain.Workspaces;

/// <summary>
/// Runtime coordinates of a connected planning board for one provider operation. A board is a
/// GitHub Projects v2 board (org- or user-owned) or, later, an Azure DevOps board. The
/// <see cref="AccessToken"/> is transient.
/// </summary>
/// <param name="Id">Workspace (board) id correlating to the persisted row, or <see cref="Guid.Empty"/> for an unsaved probe.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="Kind">Which provider backs this board.</param>
/// <param name="ProjectOwner">Login of the org/user that owns the board (GitHub) or the ADO organization.</param>
/// <param name="ProjectScope"><c>org</c> or <c>user</c> — how to resolve the board owner on GitHub.</param>
/// <param name="ProjectNumber">Board number (GitHub Projects v2 <c>number</c>). Null for a degenerate repo-only board with no board attached yet.</param>
/// <param name="ProjectNodeId">Cached GraphQL node id of the board, resolved by <see cref="ISourceProvider.ValidateBoardAsync"/>.</param>
/// <param name="AccessToken">PAT / OAuth token, resolved just-in-time. Transient.</param>
/// <param name="Project">Azure DevOps project that owns the board (ignored by GitHub).</param>
/// <param name="Host">Base host for enterprise/self-hosted; null = the provider's public host.</param>
public sealed record BoardDescriptor(
    Guid Id,
    string TenantId,
    SourceProviderKind Kind,
    string ProjectOwner,
    string ProjectScope,
    int? ProjectNumber,
    string? ProjectNodeId,
    string AccessToken,
    string? Project = null,
    string? Host = null)
{
    /// <summary>The external tracker this descriptor reads tickets from (E1.8), when it addresses a tracker — set by the
    /// tracker routing, read by the Azure DevOps Boards / Jira providers. Null for a GitHub Projects board.</summary>
    public TrackerBinding? Tracker { get; init; }

    /// <summary>Validates the coordinates needed to reach the board. Throws <see cref="ArgumentException"/> if invalid.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(TenantId));
        }
        if (string.IsNullOrWhiteSpace(ProjectOwner))
        {
            throw new ArgumentException("ProjectOwner is required.", nameof(ProjectOwner));
        }
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new ArgumentException("AccessToken is required.", nameof(AccessToken));
        }
        if (ProjectScope is not ("org" or "user"))
        {
            throw new ArgumentException("ProjectScope must be 'org' or 'user'.", nameof(ProjectScope));
        }
        if (Kind is SourceProviderKind.AzureDevOps or SourceProviderKind.Jira && string.IsNullOrWhiteSpace(Project))
        {
            throw new ArgumentException(Kind == SourceProviderKind.Jira ? "Project key is required for Jira." : "Project is required for Azure DevOps.", nameof(Project));
        }
    }
}
