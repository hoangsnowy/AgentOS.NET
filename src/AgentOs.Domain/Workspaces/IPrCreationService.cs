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
}
