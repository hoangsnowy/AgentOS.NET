// M2 — GitHub implementation of the source-provider seam. Reuses the same Octokit client shape as
// GitHubPrService (raw PAT credentials, optional Enterprise base URL), but per-workspace: the token
// comes from the WorkspaceDescriptor (resolved just-in-time from the encrypted store), NOT from the
// tenant-global runtime overrides. Read-only for M2 (validate / list / read-context); PR creation
// still lives in IGitHubPrService until M5 folds it in.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using Octokit;

namespace AgentOs.Modules.Integration.Sources;

/// <summary><see cref="ISourceProvider"/> backed by GitHub via Octokit.</summary>
public sealed class GitHubSourceProvider : ISourceProvider
{

    public SourceProviderKind Kind => SourceProviderKind.GitHub;

    public async Task<RepoValidation> ValidateAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var client = CreateClient(workspace.AccessToken, workspace.Host);
        try
        {
            var repo = await client.Repository.Get(workspace.Owner, workspace.Repo).ConfigureAwait(false);
            return RepoValidation.Success(repo.DefaultBranch);
        }
        catch (AuthorizationException)
        {
            return RepoValidation.Fail("GitHub rejected the token. Check it has 'repo' scope and isn't expired.");
        }
        catch (NotFoundException)
        {
            return RepoValidation.Fail($"Repository {workspace.Owner}/{workspace.Repo} was not found (or the token can't see it).");
        }
        catch (ApiException ex)
        {
            return RepoValidation.Fail($"GitHub error: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<RemoteRepo>> ListRepositoriesAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var client = CreateClient(credentials.AccessToken, credentials.Host);
        var repos = await client.Repository.GetAllForCurrent().ConfigureAwait(false);
        return repos
            .Select(r => new RemoteRepo(
                Owner: r.Owner?.Login ?? string.Empty,
                Name: r.Name,
                FullName: r.FullName,
                DefaultBranch: r.DefaultBranch ?? "main",
                RemoteUrl: r.HtmlUrl,
                Private: r.Private))
            .ToList();
    }

    public async Task<RepoContext> ReadRepoContextAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var client = CreateClient(workspace.AccessToken, workspace.Host);

        var repo = await client.Repository.Get(workspace.Owner, workspace.Repo).ConfigureAwait(false);

        var readme = string.Empty;
        try
        {
            var r = await client.Repository.Content.GetReadme(workspace.Owner, workspace.Repo).ConfigureAwait(false);
            readme = r.Content ?? string.Empty;
        }
        catch (NotFoundException)
        {
            // No README — leave empty.
        }

        var topLevel = new List<string>();
        try
        {
            var contents = await client.Repository.Content.GetAllContents(workspace.Owner, workspace.Repo).ConfigureAwait(false);
            topLevel.AddRange(contents.Select(c => c.Name));
        }
        catch (NotFoundException)
        {
            // Empty repo — no contents.
        }

        return new RepoContext(
            FullName: repo.FullName,
            DefaultBranch: repo.DefaultBranch ?? workspace.DefaultBranch,
            Description: repo.Description,
            Readme: readme,
            TopLevelPaths: topLevel);
    }

    // ── Boards (GitHub Projects v2, via GraphQL — Octokit REST doesn't cover Projects v2) ────────

    public async Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var owner = credentials.Owner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            return Array.Empty<BoardSummary>();
        }

        // Scope is unknown from a bare listing probe: try org, fall back to user (a NOT_FOUND on the
        // org attempt just means the login is a user).
        try
        {
            var org = await GitHubProjectsClient.ListBoardsAsync(owner, "org", credentials.AccessToken, credentials.Host, cancellationToken).ConfigureAwait(false);
            if (org.Count > 0)
            {
                return org;
            }
        }
        catch (InvalidOperationException)
        {
            // Not an org (or no org boards) — fall through to user scope.
        }

        return await GitHubProjectsClient.ListBoardsAsync(owner, "user", credentials.AccessToken, credentials.Host, cancellationToken).ConfigureAwait(false);
    }

    public Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        return GitHubProjectsClient.ValidateBoardAsync(board, cancellationToken);
    }

    public Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        return GitHubProjectsClient.ReadItemsAsync(board, cancellationToken);
    }

    public async Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0)
        {
            return null;
        }

        var client = CreateClient(repo.AccessToken, repo.Host);
        try
        {
            // The Issues API also serves pull requests, so one call covers both ticket kinds.
            var issue = await client.Issue.Get(repo.Owner, repo.Repo, number).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(issue.Body) ? null : issue.Body;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    // ── Writes (bootstrap) ───────────────────────────────────────────────────────────────────────

    public async Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(labels);
        var client = CreateClient(repo.AccessToken, repo.Host);

        IReadOnlyList<Label> existing;
        try
        {
            existing = await client.Issue.Labels.GetAllForRepository(repo.Owner, repo.Repo).ConfigureAwait(false);
        }
        catch (AuthorizationException)
        {
            throw new InvalidOperationException("GitHub rejected the token. Check it has 'repo' scope and isn't expired.");
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException($"Repository {repo.Owner}/{repo.Repo} was not found (or the token can't see it).");
        }

        var (toCreate, alreadyThere) = LabelSync.Partition(existing.Select(l => l.Name), labels);

        var created = new List<string>();
        foreach (var spec in toCreate)
        {
            var newLabel = new NewLabel(spec.Name, LabelSync.NormalizeColor(spec.Color));
            if (!string.IsNullOrWhiteSpace(spec.Description))
            {
                newLabel.Description = spec.Description;
            }
            await client.Issue.Labels.Create(repo.Owner, repo.Repo, newLabel).ConfigureAwait(false);
            created.Add(spec.Name);
        }

        return new LabelSyncResult(created, alreadyThere);
    }

    public async Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(drafts);

        // Resolve the board node id once (issues are added to it after creation).
        var boardNodeId = await ResolveBoardNodeIdAsync(board, cancellationToken).ConfigureAwait(false);
        var client = CreateClient(repo.AccessToken, repo.Host);
        var created = new List<CreatedTicket>();

        foreach (var draft in drafts)
        {
            var newIssue = new NewIssue(draft.Title) { Body = draft.Body };
            foreach (var label in draft.Labels)
            {
                newIssue.Labels.Add(label);
            }

            Issue issue;
            try
            {
                issue = await client.Issue.Create(repo.Owner, repo.Repo, newIssue).ConfigureAwait(false);
            }
            catch (AuthorizationException)
            {
                throw new InvalidOperationException("GitHub rejected the token. Check it has 'repo' scope and isn't expired.");
            }
            catch (NotFoundException)
            {
                throw new InvalidOperationException($"Repository {repo.Owner}/{repo.Repo} was not found (or the token can't see it).");
            }

            var itemNodeId = await GitHubProjectsClient.AddItemToBoardAsync(
                boardNodeId, issue.NodeId, board.AccessToken, board.Host, cancellationToken).ConfigureAwait(false);

            created.Add(new CreatedTicket(issue.Number, issue.NodeId, issue.HtmlUrl, itemNodeId, issue.Title));
        }

        return created;
    }

    private static async Task<string> ResolveBoardNodeIdAsync(BoardDescriptor board, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(board.ProjectNodeId))
        {
            return board.ProjectNodeId;
        }
        var validation = await GitHubProjectsClient.ValidateBoardAsync(board, ct).ConfigureAwait(false);
        if (!validation.Ok || string.IsNullOrEmpty(validation.NodeId))
        {
            throw new InvalidOperationException(validation.Error ?? "Could not resolve the board to add tickets to.");
        }
        return validation.NodeId;
    }

    private static GitHubClient CreateClient(string token, string? host) => GitHubClientFactory.Create(token, host);
}
