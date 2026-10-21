// M5 — creates a PR from an already-pushed branch in the workspace repo.
// The runner pushes the branch; this service calls the GitHub API to open the PR.
// PAT comes from WorkspaceDescriptor.AccessToken (per-workspace credential), never from
// tenant-global IRuntimeOverrides — correct grain for the Board per-workspace flow.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using Microsoft.Extensions.Logging;
using Octokit;

namespace AgentOs.Modules.Integration;

/// <inheritdoc cref="IPrCreationService"/>
public sealed partial class GitHubPrCreationService : IPrCreationService
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

    // Per failed check: enough log for the agent to see the failure, not a whole CI transcript.
    private const int LogTailLines = 120;
    private const int MaxChars = 12_000;

    /// <inheritdoc />
    public async Task<string> GetFailedChecksAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return string.Empty; }
        try
        {
            var client = GitHubClientFactory.Create(workspace.AccessToken, workspace.Host);
            var pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            var runs = await client.Check.Run.GetAllForReference(workspace.Owner, workspace.Repo, pr.Head.Sha).ConfigureAwait(false);
            var report = new System.Text.StringBuilder();
            foreach (var run in runs.CheckRuns)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Conclusion is not { } conclusion
                    || conclusion.Value is not (CheckConclusion.Failure or CheckConclusion.TimedOut or CheckConclusion.Cancelled))
                {
                    continue;
                }
                report.Append("## ").Append(run.Name).Append(" — ").Append(conclusion.StringValue).Append('\n');
                if (!string.IsNullOrWhiteSpace(run.Output?.Summary)) { report.Append(run.Output.Summary.Trim()).Append('\n'); }
                report.Append(await LogTailAsync(client, workspace, run.Id).ConfigureAwait(false)).Append('\n');
                if (report.Length > MaxChars) { break; }
            }
            return report.Length > MaxChars ? report.ToString(0, MaxChars) + "\n[truncated]" : report.ToString();
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed-check lookup failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return string.Empty;
        }
    }

    // A GitHub Actions check run id is its job id; the job log is plain text with a timestamp per line.
    private static async Task<string> LogTailAsync(GitHubClient client, WorkspaceDescriptor workspace, long jobId)
    {
        try
        {
            var log = await client.Actions.Workflows.Jobs.GetLogs(workspace.Owner, workspace.Repo, jobId).ConfigureAwait(false);
            var lines = log.Split('\n');
            var tail = lines.Length > LogTailLines ? lines[^LogTailLines..] : lines;
            return string.Join('\n', tail.Select(StripTimestamp));
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException)
        {
            return "(log not available)";
        }
    }

    // "2026-10-21T01:48:00.6728828Z message" → "message"; ANSI colour codes dropped.
    private static string StripTimestamp(string line)
    {
        var text = line.Length > 29 && line[4] == '-' && line[10] == 'T' && line.IndexOf('Z', 19) is var z and > 0 && z < 32
            ? line[(z + 1)..].TrimStart()
            : line;
        return AnsiColour().Replace(text, string.Empty).TrimEnd('\r');
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiColour();

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
