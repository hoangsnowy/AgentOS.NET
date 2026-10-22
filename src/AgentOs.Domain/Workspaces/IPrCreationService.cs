// M5 — domain seam for creating a PR from an already-pushed branch. The runner pushes the branch;
// the server creates the PR via the source-control API. Implemented by Integration module.

using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Workspaces;

/// <summary>Result of <see cref="IPrCreationService.CreateFromBranchAsync"/>.</summary>
/// <param name="Ok">True when the PR was created successfully.</param>
/// <param name="Number">PR number assigned by the provider. Null on failure.</param>
/// <param name="HtmlUrl">Browser URL of the created PR. Null on failure.</param>
/// <param name="Error">Failure reason; null when <see cref="Ok"/> is true.</param>
public sealed record PrCreationResult(bool Ok, int? Number, string? HtmlUrl, string? Error);

/// <summary>The combined state of a pull request's checks on its latest commit.</summary>
public enum PrChecksState
{
    /// <summary>No checks reported (none configured, or not started yet).</summary>
    None,
    /// <summary>At least one check still running, none failed.</summary>
    Pending,
    /// <summary>At least one check failed, timed out or was cancelled.</summary>
    Failed,
    /// <summary>Every check completed successfully (or was skipped/neutral).</summary>
    Passed,
}

/// <summary>Whether a pull request can merge into its base as it stands.</summary>
public enum PrMergeState
{
    /// <summary>Not computed yet, or not known.</summary>
    Unknown,
    /// <summary>Up to date and mergeable.</summary>
    Clean,
    /// <summary>Its base moved on; no conflicts — the branch only needs updating.</summary>
    Behind,
    /// <summary>It conflicts with its base.</summary>
    Conflicts,
}

/// <summary>Where an opened pull request stands now (<see cref="IPrCreationService.GetStateAsync"/>).</summary>
public enum PrState
{
    /// <summary>Not known — unsupported provider, network or API failure.</summary>
    Unknown,
    /// <summary>Open, waiting for review.</summary>
    Open,
    /// <summary>Merged into its base branch.</summary>
    Merged,
    /// <summary>Closed without merging.</summary>
    Closed,
}

/// <summary>
/// Creates a pull request from an already-pushed branch in the workspace's repository.
/// PAT comes from <see cref="WorkspaceDescriptor.AccessToken"/> — never from tenant-global settings.
/// </summary>
public interface IPrCreationService
{
    Task<PrCreationResult> CreateFromBranchAsync(
        WorkspaceDescriptor workspace,
        string branch,
        string title,
        string body,
        CancellationToken ct = default);

    /// <summary>The current state of pull request <paramref name="number"/> — so a task whose change was merged
    /// stops reading "Ready for review". Never throws; <see cref="PrState.Unknown"/> when it can't tell.</summary>
    Task<PrState> GetStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult(PrState.Unknown);

    /// <summary>The failed checks of pull request <paramref name="number"/>'s latest commit — name, summary and the tail of
    /// each log — as plain text for the agent to fix (Board › Ask for changes › Fix the failing checks). Empty when nothing
    /// failed or the provider can't tell. Never throws.</summary>
    Task<string> GetFailedChecksAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    /// <summary>The combined state of pull request <paramref name="number"/>'s checks. Never throws; None when unknown.</summary>
    Task<PrChecksState> GetChecksStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult(PrChecksState.None);

    /// <summary>Whether pull request <paramref name="number"/> can merge as it stands. Never throws; Unknown when it can't tell.</summary>
    Task<PrMergeState> GetMergeStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult(PrMergeState.Unknown);

    /// <summary>Bring a behind pull request up to date with its base (the provider merges the base into the branch).
    /// Null on success, else the reason.</summary>
    Task<string?> UpdateBranchAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult<string?>("Updating a branch is not supported for this provider.");

    /// <summary>True when <paramref name="branch"/> exists on the remote. Null when it couldn't be checked.</summary>
    Task<bool?> BranchExistsAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default) =>
        Task.FromResult<bool?>(null);

    /// <summary>Post a comment on pull request <paramref name="number"/> (the Testing agent's evidence). Never throws.</summary>
    Task<bool> CommentAsync(WorkspaceDescriptor workspace, int number, string body, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>Squash-merge pull request <paramref name="number"/> and delete its branch. Null on success, else the reason.</summary>
    Task<string?> MergeAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default) =>
        Task.FromResult<string?>("Merging is not supported for this provider.");
}
