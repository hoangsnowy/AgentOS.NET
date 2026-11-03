// M5 — creates a PR from an already-pushed branch in the workspace repo.
// The runner pushes the branch; this service calls the GitHub API to open the PR.
// PAT comes from WorkspaceDescriptor.AccessToken (per-workspace credential), never from
// tenant-global IRuntimeOverrides — correct grain for the Board per-workspace flow.
// Repo safety (E1.5, GAP-18): a pull request is never opened FROM the default branch, drafts are opened when the
// approval policy asks for them, and a merge is refused when it comes from the default branch or the base branch's
// protection (reviews, code owners, required checks — read through the API by GitHubMergeGuard) would refuse it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Governance;
using AgentOs.SharedKernel.Logging;
using Microsoft.Extensions.Logging;
using Octokit;

namespace AgentOs.Modules.Integration;

/// <inheritdoc cref="IPrCreationService"/>
public sealed partial class GitHubPrCreationService : IPrCreationService
{
    private readonly ILogger<GitHubPrCreationService> _logger;
    private readonly Func<string?, string?, IGitHubClient> _clients;
    private readonly Func<WorkspaceDescriptor, string, CancellationToken, Task> _markReady;

    public GitHubPrCreationService(ILogger<GitHubPrCreationService> logger)
        : this(logger, GitHubClientFactory.Create, MarkReadyForReviewAsync)
    {
    }

    /// <summary>For tests: a client factory (a mocked GitHub) and the draft → ready call (GraphQL-only on GitHub).</summary>
    internal GitHubPrCreationService(ILogger<GitHubPrCreationService> logger, Func<string?, string?, IGitHubClient> clients,
        Func<WorkspaceDescriptor, string, CancellationToken, Task> markReady)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _markReady = markReady ?? throw new ArgumentNullException(nameof(markReady));
    }

    // GitHub has no REST call for "ready for review": the markPullRequestReadyForReview mutation.
    private static Task MarkReadyForReviewAsync(WorkspaceDescriptor workspace, string nodeId, CancellationToken ct) =>
        Sources.GitHubProjectsClient.MarkReadyForReviewAsync(workspace.AccessToken, workspace.Host, nodeId, ct);

    /// <inheritdoc />
    public async Task<PrState> GetStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return PrState.Unknown; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
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
            var client = _clients(workspace.AccessToken, workspace.Host);
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

    /// <inheritdoc />
    public async Task<PrChecksState> GetChecksStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return PrChecksState.None; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            var pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            var runs = (await client.Check.Run.GetAllForReference(workspace.Owner, workspace.Repo, pr.Head.Sha).ConfigureAwait(false)).CheckRuns;
            return Combine(runs.Select(r => (r.Status.Value, r.Conclusion?.Value)));
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Check-state lookup failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return PrChecksState.None;
        }
    }

    /// <summary>Failed beats running beats passed; nothing reported is None.</summary>
    internal static PrChecksState Combine(IEnumerable<(CheckStatus Status, CheckConclusion? Conclusion)> runs)
    {
        var any = false;
        var pending = false;
        foreach (var (status, conclusion) in runs)
        {
            any = true;
            if (status != CheckStatus.Completed) { pending = true; continue; }
            if (conclusion is CheckConclusion.Failure or CheckConclusion.TimedOut or CheckConclusion.Cancelled or CheckConclusion.ActionRequired)
            {
                return PrChecksState.Failed;
            }
        }
        return !any ? PrChecksState.None : pending ? PrChecksState.Pending : PrChecksState.Passed;
    }

    /// <inheritdoc />
    public async Task<PrMergeState> GetMergeStateAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return PrMergeState.Unknown; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            var pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            return MergeStateOf(pr.Mergeable, pr.MergeableState?.Value);
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Merge-state lookup failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return PrMergeState.Unknown;
        }
    }

    /// <summary>GitHub's mergeable flags as a PrMergeState (a conflict is <c>mergeable == false</c> / <c>dirty</c>).</summary>
    internal static PrMergeState MergeStateOf(bool? mergeable, MergeableState? state) =>
        mergeable == false || state == MergeableState.Dirty ? PrMergeState.Conflicts
        : state == MergeableState.Behind ? PrMergeState.Behind
        : mergeable == true ? PrMergeState.Clean
        : PrMergeState.Unknown;

    /// <inheritdoc />
    public async Task<string?> UpdateBranchAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return "Updating a branch is not supported for this provider."; }
        try
        {
            // PUT /repos/{owner}/{repo}/pulls/{n}/update-branch — not wrapped by Octokit 14.
            var client = _clients(workspace.AccessToken, workspace.Host);
            var uri = new Uri($"repos/{Uri.EscapeDataString(workspace.Owner)}/{Uri.EscapeDataString(workspace.Repo)}/pulls/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}/update-branch", UriKind.Relative);
            await client.Connection.Put<object>(uri, new { }, null, "application/vnd.github+json").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Update-branch failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return ex.Message;
        }
    }

    /// <inheritdoc />
    public async Task<bool?> BranchExistsAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || string.IsNullOrWhiteSpace(branch)) { return null; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            await client.Repository.Branch.Get(workspace.Owner, workspace.Repo, branch).ConfigureAwait(false);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // The branch name may come from an agent's reply: never raw into the log.
            _logger.LogWarning(ex, "Couldn't check branch {Branch} on {Owner}/{Repo}", LogSafe.Scrub(branch), workspace.Owner, workspace.Repo);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetBranchHeadAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || string.IsNullOrWhiteSpace(branch)) { return null; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            ct.ThrowIfCancellationRequested();
            var found = await client.Repository.Branch.Get(workspace.Owner, workspace.Repo, branch).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(found.Commit?.Sha) ? null : found.Commit.Sha;
        }
        catch (NotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't read the head of branch {Branch} on {Owner}/{Repo}", LogSafe.Scrub(branch), workspace.Owner, workspace.Repo);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TicketComment>> ListCommentsAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return []; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            var comments = await client.Issue.Comment.GetAllForIssue(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            return comments.Select(c => new TicketComment(c.User?.Login ?? "", c.Body ?? "", c.CreatedAt)).OrderBy(c => c.CreatedAt).ToList();
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Reading comments failed on {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> ListChangedFilesAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return null; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            ct.ThrowIfCancellationRequested();
            var files = await client.PullRequest.Files(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            return files.SelectMany(f => f.PreviousFileName is { Length: > 0 } previous ? new[] { f.FileName, previous } : new[] { f.FileName })
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Listing the changed files failed on {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OpenPullRequest>> ListOpenPullRequestsAsync(WorkspaceDescriptor workspace, string headPrefix, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || string.IsNullOrWhiteSpace(headPrefix)) { return []; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            var open = await client.PullRequest.GetAllForRepository(workspace.Owner, workspace.Repo,
                new PullRequestRequest { State = ItemStateFilter.Open }, new ApiOptions { PageSize = 100, PageCount = 1 }).ConfigureAwait(false);
            return open.Where(p => p.Head?.Ref?.StartsWith(headPrefix, StringComparison.Ordinal) == true)
                .Select(p => new OpenPullRequest(p.Number, p.Head.Ref, p.HtmlUrl)).ToList();
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Listing open pull requests failed on {Owner}/{Repo}", workspace.Owner, workspace.Repo);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<bool> CloseAsync(WorkspaceDescriptor workspace, int number, string comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return false; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            if (!string.IsNullOrWhiteSpace(comment))
            {
                await client.Issue.Comment.Create(workspace.Owner, workspace.Repo, number, comment).ConfigureAwait(false);
            }
            // Issues and pull requests share the issue endpoint for their state.
            await client.Issue.Update(workspace.Owner, workspace.Repo, number, new IssueUpdate { State = ItemState.Closed }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Closing failed on {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CommentAsync(WorkspaceDescriptor workspace, int number, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0 || string.IsNullOrWhiteSpace(body)) { return false; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            await client.Issue.Comment.Create(workspace.Owner, workspace.Repo, number, body).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Comment failed on {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateBodyAsync(WorkspaceDescriptor workspace, int number, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0 || string.IsNullOrWhiteSpace(body)) { return false; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            await client.PullRequest.Update(workspace.Owner, workspace.Repo, number, new PullRequestUpdate { Body = body }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Updating the description failed on {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> MergeAsync(WorkspaceDescriptor workspace, int number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Kind != SourceProviderKind.GitHub || number <= 0) { return "Merging is not supported for this provider."; }
        try
        {
            var client = _clients(workspace.AccessToken, workspace.Host);
            var pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            // Never merge from the default branch: deleting the head branch afterwards would delete it.
            if (RepoSafety.RefuseDefaultBranch(pr.Head.Ref, workspace.DefaultBranch, pr.Base.Ref) is { } fromDefault)
            {
                _logger.LogWarning("Refused to merge {Owner}/{Repo}#{Number}: its head is the default branch", workspace.Owner, workspace.Repo, number);
                return fromDefault;
            }
            // A draft (the approval policy opened it so) is marked ready only now, right before a merge that may happen.
            if (pr.Draft)
            {
                await _markReady(workspace, pr.NodeId, ct).ConfigureAwait(false);
                pr = await client.PullRequest.Get(workspace.Owner, workspace.Repo, number).ConfigureAwait(false);
            }
            // Branch protection + CODEOWNERS, read through the API: refuse what the protection would refuse — also when
            // the token's owner could bypass it.
            var facts = await GitHubMergeGuard.ReadAsync(client, workspace.Owner, workspace.Repo, pr, ct).ConfigureAwait(false);
            if (MergeProtection.Refusal(facts) is { } refused)
            {
                _logger.LogWarning("Refused to merge {Owner}/{Repo}#{Number}: {Reason}", workspace.Owner, workspace.Repo, number, refused);
                return refused;
            }
            var merged = await client.PullRequest.Merge(workspace.Owner, workspace.Repo, number,
                new MergePullRequest { MergeMethod = PullRequestMergeMethod.Squash, Sha = pr.Head.Sha }).ConfigureAwait(false);
            if (!merged.Merged) { return merged.Message ?? "GitHub did not merge the pull request."; }
            try { await client.Git.Reference.Delete(workspace.Owner, workspace.Repo, "heads/" + pr.Head.Ref).ConfigureAwait(false); }
            catch (ApiException) { /* branch protection or already gone — the merge is what matters */ }
            _logger.LogInformation("Merged {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return null;
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Merge failed for {Owner}/{Repo}#{Number}", workspace.Owner, workspace.Repo, number);
            return ex.Message;
        }
    }

    // A GitHub Actions check run id is its job id; the job log is plain text with a timestamp per line.
    private static async Task<string> LogTailAsync(IGitHubClient client, WorkspaceDescriptor workspace, long jobId)
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
    public Task<PrCreationResult> CreateFromBranchAsync(
        WorkspaceDescriptor workspace,
        string branch,
        string title,
        string body,
        CancellationToken ct = default) =>
        CreateFromBranchAsync(workspace, branch, title, body, draft: false, ct);

    /// <inheritdoc />
    public async Task<PrCreationResult> CreateFromBranchAsync(
        WorkspaceDescriptor workspace,
        string branch,
        string title,
        string body,
        bool draft,
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

        // A pull request FROM the default branch means the change was pushed onto it: never open (or later merge) one.
        if (RepoSafety.RefuseDefaultBranch(branch, workspace.DefaultBranch) is { } refused)
        {
            _logger.LogWarning("M5: refused a pull request from the default branch of {Owner}/{Repo}", workspace.Owner, workspace.Repo);
            return new PrCreationResult(false, null, null, refused);
        }

        var client = _clients(workspace.AccessToken, workspace.Host);

        ct.ThrowIfCancellationRequested();
        try
        {
            var pr = await client.PullRequest.Create(
                workspace.Owner,
                workspace.Repo,
                new NewPullRequest(title, branch, workspace.DefaultBranch) { Body = body, Draft = draft })
                .ConfigureAwait(false);

            _logger.LogInformation(
                "M5: opened PR #{Number} ({Url}) for branch '{Branch}' on {Owner}/{Repo} (draft: {Draft})",
                pr.Number, pr.HtmlUrl, LogSafe.Scrub(branch), workspace.Owner, workspace.Repo, draft);

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
