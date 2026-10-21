// M5 — creates a PR from an already-pushed branch in the workspace repo.
// The runner pushes the branch; this service calls the GitHub API to open the PR.
// PAT comes from WorkspaceDescriptor.AccessToken (per-workspace credential), never from
// tenant-global IRuntimeOverrides — correct grain for the Board per-workspace flow.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using Microsoft.Extensions.Logging;
using Octokit;

namespace AgentOs.Modules.Integration;

/// <inheritdoc cref="IPrCreationService"/>
public sealed class GitHubPrCreationService : IPrCreationService
{
    private readonly ILogger<GitHubPrCreationService> _logger;

    public GitHubPrCreationService(ILogger<GitHubPrCreationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PrState> GetStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return PrState.Unknown; }
        try
        {
            var client = GitHubClientFactory.Create(workspace.AccessToken, workspace.Host);
            ct.ThrowIfCancellationRequested();
            var pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            return pr.Merged ? PrState.Merged
                : pr.State.Value == ItemState.Closed ? PrState.Closed
                : PrState.Open;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "PR state lookup failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return PrState.Unknown;
        }
    }

    /// <inheritdoc />
    public async Task<PrCreationResult> CreateFromBranchAsync(
        WorkspaceDescriptor workspace,
        string branch,
        string title,
        string body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (workspace.Kind != SourceProviderKind.GitHub)
        {
            return new PrCreationResult(false, null, null,
                $"PR creation not supported for provider '{workspace.Kind}' yet.");
        }

        var client = GitHubClientFactory.Create(workspace.AccessToken, workspace.Host);

        ct.ThrowIfCancellationRequested();
        try
        {
            var pr = await client.PullRequest.Create(
                workspace.Owner,
                workspace.Repo,
                new NewPullRequest(title, branch, workspace.DefaultBranch) { Body = body })
                .ConfigureAwait(false);

            _logger.LogInformation(
                "M5: opened PR #{Number} ({Url}) for branch '{Branch}' on {Owner}/{Repo}",
                pr.Number, pr.HtmlUrl, branch, workspace.Owner, workspace.Repo);

            return new PrCreationResult(true, pr.Number, pr.HtmlUrl, null);
        }
        catch (AuthorizationException ex)
        {
            _logger.LogWarning(ex, "M5: GitHub rejected PAT for {Owner}/{Repo}", workspace.Owner, workspace.Repo);
            return new PrCreationResult(false, null, null,
                $"GitHub rejected the PAT for {workspace.Owner}/{workspace.Repo}. Check the token's 'repo' scope.");
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "M5: branch '{Branch}' not found on {Owner}/{Repo}", branch, workspace.Owner, workspace.Repo);
            return new PrCreationResult(false, null, null,
                $"Branch '{branch}' was not found on {workspace.Owner}/{workspace.Repo}. Ensure the runner pushed it.");
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "M5: GitHub API error creating PR for {Owner}/{Repo}", workspace.Owner, workspace.Repo);
            return new PrCreationResult(false, null, null, $"GitHub API error: {ex.Message}");
        }
    }
}
