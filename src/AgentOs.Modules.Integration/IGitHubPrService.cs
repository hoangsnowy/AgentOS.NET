// AgentOs.Application/Integration/IGitHubPrService.cs
// Opens a GitHub pull request that contains the generated code + tests from a pipeline run.

using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration;

/// <summary>Pushes a <see cref="PipelineResult"/>'s generated code + tests to a target GitHub repo and opens a PR.</summary>
public interface IGitHubPrService
{
    /// <summary>
    /// Create a new branch (timestamped) from the base branch, commit every file in
    /// <see cref="PipelineResult.Code"/> and <see cref="PipelineResult.Tests"/>, and open a PR.
    /// PAT and target repo are read from <c>IRuntimeOverrides</c> (the Settings page) — the FALLBACK
    /// used only when no connected workspace is selected. Prefer the workspace overload.
    /// </summary>
    Task<GitHubPrResult> OpenPrAsync(PipelineResult result, string title, string body, CancellationToken ct);

    /// <summary>
    /// Same as above, but targets a connected <see cref="WorkspaceDescriptor"/> (a connected board's
    /// repo) using the per-workspace PAT — the unified repo/token source. GitHub-only.
    /// </summary>
    Task<GitHubPrResult> OpenPrAsync(PipelineResult result, WorkspaceDescriptor workspace, string title, string body, CancellationToken ct);

    /// <summary>Same, opened as a draft when <paramref name="draft"/> is set (the approval policy's draft pull requests, E1.5).</summary>
    Task<GitHubPrResult> OpenPrAsync(PipelineResult result, WorkspaceDescriptor workspace, string title, string body, bool draft, CancellationToken ct) =>
        draft
            ? throw new System.InvalidOperationException("This service can't open draft pull requests, and the approval policy asks for drafts.")
            : OpenPrAsync(result, workspace, title, body, ct);
}

/// <summary>Result of <see cref="IGitHubPrService.OpenPrAsync"/>.</summary>
/// <param name="Number">PR number assigned by GitHub.</param>
/// <param name="HtmlUrl">Browser URL of the PR.</param>
/// <param name="Branch">Name of the branch the PR is opened from.</param>
public sealed record GitHubPrResult(int Number, string HtmlUrl, string Branch);
