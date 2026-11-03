// Azure DevOps source provider. Repo connection (validate + list + ground) runs LIVE over the ADO REST
// API (AzureDevOpsRestClient) — the official client SDK does not resolve under net10, so we use raw HTTP
// like the GitHub Projects client. Boards (E1.8): work items are a board's tickets when the board has an Azure DevOps
// tracker binding — TrackerRoutingSourceProvider hands these members a descriptor carrying it (Tracker) and the
// tracker's PAT; AzureDevOpsBoardsClient does the REST work. Without a binding the board members stay unsupported.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration.Sources;

/// <summary><see cref="ISourceProvider"/> for Azure DevOps — repos and Boards work items over REST.</summary>
public sealed class AzureDevOpsSourceProvider : ISourceProvider
{
    private const string DefaultHost = "dev.azure.com";

    internal const string BoardsNotSupportedMessage =
        "Azure DevOps work items are read through a board's tracker — connect the board with \"Tickets from: Azure DevOps Boards\".";

    private readonly IHttpClientFactory? _httpFactory;
    private readonly TimeProvider _clock;

    /// <summary>Parameterless ctor keeps the board members usable (and tests simple) without an HttpClient.</summary>
    public AzureDevOpsSourceProvider(IHttpClientFactory? httpFactory = null, TimeProvider? clock = null)
    {
        _httpFactory = httpFactory;
        _clock = clock ?? TimeProvider.System;
    }

    public SourceProviderKind Kind => SourceProviderKind.AzureDevOps;

    private AzureDevOpsRestClient Client()
        => new(_httpFactory?.CreateClient(nameof(AzureDevOpsSourceProvider))
            ?? throw new InvalidOperationException("Azure DevOps HTTP client is not configured (no IHttpClientFactory)."));

    // ── Repo connection — live over REST ─────────────────────────────────────────────────────────

    public Task<RepoValidation> ValidateAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Client().ValidateAsync(
            workspace.Host ?? DefaultHost, workspace.Owner, workspace.Project ?? string.Empty, workspace.Repo, workspace.AccessToken, cancellationToken);
    }

    public Task<IReadOnlyList<RemoteRepo>> ListRepositoriesAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return Client().ListRepositoriesAsync(credentials.Host ?? DefaultHost, credentials.Owner ?? string.Empty, credentials.AccessToken, cancellationToken);
    }

    public Task<RepoContext> ReadRepoContextAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default)
    {
        // Minimal grounding for now: identity + default branch (no README/tree fetch yet). A connected
        // ADO workspace runs without crashing; richer context is a follow-up.
        ArgumentNullException.ThrowIfNull(workspace);
        return Task.FromResult(new RepoContext(
            FullName: $"{workspace.Owner}/{workspace.Project}/{workspace.Repo}",
            DefaultBranch: workspace.DefaultBranch,
            Description: null,
            Readme: string.Empty,
            TopLevelPaths: Array.Empty<string>()));
    }

    // ── Boards / work items (E1.8) — through a board's Azure DevOps tracker binding ─────────────

    private AzureDevOpsBoardsClient Boards() =>
        new(_httpFactory?.CreateClient(nameof(AzureDevOpsSourceProvider))
            ?? throw new InvalidOperationException("Azure DevOps HTTP client is not configured (no IHttpClientFactory)."), _clock);

    private static TrackerBinding? AdoBinding(TrackerBinding? tracker) => tracker is { Kind: SourceProviderKind.AzureDevOps } b ? b : null;

    // The connect form names the project; there is no picker to fill.
    public Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BoardSummary>>([]);

    public Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (AdoBinding(board.Tracker) is not { } binding) { return Task.FromResult(BoardValidation.Fail(BoardsNotSupportedMessage)); }
        return binding.Problem() is { } problem
            ? Task.FromResult(BoardValidation.Fail(problem))
            : Boards().ValidateAsync(binding, board.AccessToken, cancellationToken);
    }

    public Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        return AdoBinding(board.Tracker) is { } binding
            ? Boards().ReadTicketsAsync(binding, board.AccessToken, cancellationToken)
            : throw new NotSupportedException(BoardsNotSupportedMessage);
    }

    public async Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (AdoBinding(repo.Tracker) is null || number <= 0) { return null; }
        return (await ReadTicketAsync(repo, number, 0, cancellationToken).ConfigureAwait(false)).Body;
    }

    /// <summary>The note on a ticket read from Azure DevOps without a Boards binding (a repos-only board).</summary>
    internal const string CommentsNotSupportedNote =
        "This board's tickets don't come from Azure DevOps Boards — the agents work from the ticket's description only.";

    public Task<TicketContent> ReadTicketAsync(WorkspaceDescriptor repo, int number, int maxComments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (AdoBinding(repo.Tracker) is not { } binding)
        {
            return Task.FromResult(TicketContent.BodyOnly(null, maxComments > 0 ? CommentsNotSupportedNote : null));
        }
        return number <= 0
            ? Task.FromResult(TicketContent.BodyOnly(null))
            : Boards().ReadTicketAsync(binding, repo.TenantId, repo.AccessToken, number, maxComments, cancellationToken);
    }

    public async Task<bool> CommentOnTicketAsync(WorkspaceDescriptor repo, int number, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || string.IsNullOrWhiteSpace(body) || AdoBinding(repo.Tracker) is not { } binding) { return false; }
        return await JiraSourceProvider.Safe(() => Boards().CommentAsync(binding, repo.AccessToken, number, body, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> MoveTicketAsync(WorkspaceDescriptor repo, int number, TicketMilestone milestone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || AdoBinding(repo.Tracker) is not { } binding) { return false; }
        var state = milestone == TicketMilestone.Started ? binding.StartState : binding.DoneState;
        if (string.IsNullOrWhiteSpace(state)) { return false; }
        return await JiraSourceProvider.Safe(() => Boards().SetStateAsync(binding, repo.AccessToken, number, state.Trim(), cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> LinkPullRequestAsync(WorkspaceDescriptor repo, int number, string pullRequestUrl, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (number <= 0 || string.IsNullOrWhiteSpace(pullRequestUrl) || AdoBinding(repo.Tracker) is not { } binding) { return false; }
        return await JiraSourceProvider.Safe(() => Boards().LinkAsync(binding, repo.AccessToken, number, pullRequestUrl, title, cancellationToken)).ConfigureAwait(false);
    }

    // Work item tags are free-form: nothing to create first.
    public Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(labels);
        return AdoBinding(repo.Tracker) is null
            ? throw new NotSupportedException(BoardsNotSupportedMessage)
            : Task.FromResult(new LabelSyncResult([], labels.Select(l => l.Name).ToList()));
    }

    public async Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(drafts);
        var binding = AdoBinding(board.Tracker) ?? throw new NotSupportedException(BoardsNotSupportedMessage);
        var client = Boards();
        var created = new List<CreatedTicket>(drafts.Count);
        foreach (var draft in drafts)
        {
            created.Add(await client.CreateAsync(binding, board.AccessToken, draft, cancellationToken).ConfigureAwait(false));
        }
        return created;
    }
}
