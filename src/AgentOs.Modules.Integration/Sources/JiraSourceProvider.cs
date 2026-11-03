// E1.8 — Jira Cloud as a board's ticket tracker. Jira holds tickets, never code: the repository members refuse, the
// board / ticket members run over REST v3 (JiraRestClient) with the binding carried on the descriptor (Tracker) and the
// tracker's API token as its AccessToken — both put there by TrackerRoutingSourceProvider. The site is tenant-supplied,
// so the HTTP client is the SSRF-hardened named client (no private / loopback / link-local addresses).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration.Sources;

/// <summary><see cref="ISourceProvider"/> for Jira Cloud — tickets only.</summary>
public sealed class JiraSourceProvider : ISourceProvider
{
    internal const string NotACodeHostMessage = "Jira holds tickets, not code — connect the repositories from GitHub or Azure Repos.";

    internal const string NoBindingMessage = "This call needs the board's Jira binding (it is reached through the board's tracker).";

    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;

    /// <summary>A provider whose HTTP client comes from <paramref name="httpFactory"/> (the named SSRF-hardened client).</summary>
    public JiraSourceProvider(IHttpClientFactory? httpFactory = null, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory;
        _clock = clock ?? TimeProvider.System;
    }

    public SourceProviderKind Kind => SourceProviderKind.Jira;

    private JiraRestClient Client() =>
        new(_httpFactory?.CreateClient(nameof(JiraSourceProvider))
            ?? throw new InvalidOperationException("The Jira HTTP client is not configured (no IHttpClientFactory)."), _clock);

    private static TrackerBinding Binding(TrackerBinding? tracker) =>
        tracker is { Kind: SourceProviderKind.Jira } b ? b : throw new InvalidOperationException(NoBindingMessage);

    // ── Repositories: not a code host ────────────────────────────────────────────────────────────

    public Task<RepoValidation> ValidateAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
        Task.FromResult(RepoValidation.Fail(NotACodeHostMessage));

    public Task<IReadOnlyList<RemoteRepo>> ListRepositoriesAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteRepo>>([]);

    public Task<RepoContext> ReadRepoContextAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotACodeHostMessage);

    // ── Board = one Jira project (+ JQL) ─────────────────────────────────────────────────────────

    // The connect form names the project key; there is no picker to fill.
    public Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BoardSummary>>([]);

    public Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.Tracker is not { Kind: SourceProviderKind.Jira } binding) { return Task.FromResult(BoardValidation.Fail(NoBindingMessage)); }
        return binding.Problem() is { } problem
            ? Task.FromResult(BoardValidation.Fail(problem))
            : Client().ValidateAsync(binding, board.AccessToken, cancellationToken);
    }

    public Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        return Client().ReadTicketsAsync(Binding(board.Tracker), board.AccessToken, cancellationToken);
    }

    public async Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default) =>
        (await ReadTicketAsync(repo, number, 0, cancellationToken).ConfigureAwait(false)).Body;

    public Task<TicketContent> ReadTicketAsync(WorkspaceDescriptor repo, int number, int maxComments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return number <= 0
            ? Task.FromResult(TicketContent.BodyOnly(null))
            : Client().ReadTicketAsync(Binding(repo.Tracker), repo.TenantId, repo.AccessToken, number, maxComments, cancellationToken);
    }

    // ── Ticket writes ────────────────────────────────────────────────────────────────────────────

    public async Task<bool> CommentOnTicketAsync(WorkspaceDescriptor repo, int number, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || string.IsNullOrWhiteSpace(body) || repo.Tracker is not { Kind: SourceProviderKind.Jira } binding) { return false; }
        return await Safe(() => Client().CommentAsync(binding, repo.AccessToken, number, body, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> MoveTicketAsync(WorkspaceDescriptor repo, int number, TicketMilestone milestone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || repo.Tracker is not { Kind: SourceProviderKind.Jira } binding) { return false; }
        var target = milestone == TicketMilestone.Started ? binding.StartState : binding.DoneState;
        if (string.IsNullOrWhiteSpace(target)) { return false; }
        return await Safe(() => Client().TransitionAsync(binding, repo.AccessToken, number, target, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> LinkPullRequestAsync(WorkspaceDescriptor repo, int number, string pullRequestUrl, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || string.IsNullOrWhiteSpace(pullRequestUrl) || repo.Tracker is not { Kind: SourceProviderKind.Jira } binding) { return false; }
        return await Safe(() => Client().LinkAsync(binding, repo.AccessToken, number, pullRequestUrl, title, cancellationToken)).ConfigureAwait(false);
    }

    // Jira labels are free-form: nothing to create first.
    public Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return Task.FromResult(new LabelSyncResult([], labels.Select(l => l.Name).ToList()));
    }

    public async Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(drafts);
        var binding = Binding(board.Tracker);
        var client = Client();
        var created = new List<CreatedTicket>(drafts.Count);
        foreach (var draft in drafts)
        {
            created.Add(await client.CreateAsync(binding, board.AccessToken, draft, cancellationToken).ConfigureAwait(false));
        }
        return created;
    }

    // The ticket writes never throw (a tracker hiccup must not fail a run).
    internal static async Task<bool> Safe(Func<Task<bool>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
