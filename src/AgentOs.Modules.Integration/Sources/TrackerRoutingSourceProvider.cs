// E1.8 — the seam that makes an external tracker transparent. Every caller keeps resolving the provider for the board's
// CODE host (board.Kind: GitHub / Azure Repos) and calling it with the board's token; this decorator wraps that provider
// and, for the ticket members only, asks IBoardTrackers whether the board (descriptor.Id) has a tracker binding. If it
// has, the call goes to the tracker's provider (Azure DevOps Boards / Jira) with a descriptor carrying the binding and
// the TRACKER's token; repository and pull-request members always stay on the code host. Tracker tickets get their
// repository from the board's repos (TrackerRepoMap). The binding lookup is cached briefly per tenant + board.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Integration.Sources;

/// <summary>Routes a board's ticket calls to its external tracker; everything else to the code host.</summary>
internal sealed class TrackerRoutingSourceProvider : ISourceProvider
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const int MaxCached = 2048;

    private readonly ISourceProvider _inner;
    private readonly IReadOnlyDictionary<SourceProviderKind, ISourceProvider> _trackers;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string Tenant, Guid Board), (DateTimeOffset At, TrackerConnection? Connection)> _cache = new();

    public TrackerRoutingSourceProvider(ISourceProvider inner, IReadOnlyDictionary<SourceProviderKind, ISourceProvider> trackers,
        IServiceScopeFactory scopes, TimeProvider? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The code-host provider this wraps.</summary>
    internal ISourceProvider Inner => _inner;

    public SourceProviderKind Kind => _inner.Kind;

    // ── The board's tracker, if any ──────────────────────────────────────────────────────────────

    private async Task<(ISourceProvider Provider, TrackerConnection Connection)?> TrackerAsync(string tenantId, Guid boardId, CancellationToken ct)
    {
        if (boardId == Guid.Empty || string.IsNullOrWhiteSpace(tenantId)) { return null; }
        var key = (tenantId, boardId);
        TrackerConnection? connection;
        if (_cache.TryGetValue(key, out var hit) && _clock.GetUtcNow() - hit.At < CacheTtl)
        {
            connection = hit.Connection;
        }
        else
        {
            using var scope = _scopes.CreateScope();
            connection = scope.ServiceProvider.GetService<IBoardTrackers>() is { } trackers
                ? await trackers.GetAsync(tenantId, boardId, ct).ConfigureAwait(false)
                : null;
            if (_cache.Count >= MaxCached) { _cache.Clear(); }
            _cache[key] = (_clock.GetUtcNow(), connection);
        }
        return connection is not null && _trackers.TryGetValue(connection.Binding.Kind, out var provider) ? (provider, connection) : null;
    }

    /// <summary>Forget a board's cached binding (after it was connected or removed).</summary>
    internal void Forget(string tenantId, Guid boardId) => _cache.TryRemove((tenantId, boardId), out _);

    internal static BoardDescriptor TrackerBoard(BoardDescriptor board, TrackerConnection c) =>
        new(board.Id, board.TenantId, c.Binding.Kind, c.Binding.Site, "org", null, null, c.AccessToken, c.Binding.Project, c.Binding.Host)
        {
            Tracker = c.Binding,
        };

    internal static WorkspaceDescriptor TrackerWorkspace(WorkspaceDescriptor repo, TrackerConnection c) =>
        new(repo.Id, repo.TenantId, c.Binding.Kind, c.Binding.Site, c.Binding.Project, c.Binding.Project, "main", c.AccessToken, c.Binding.Host)
        {
            Tracker = c.Binding,
        };

    // ── Ticket members: routed ───────────────────────────────────────────────────────────────────

    public async Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        return await TrackerAsync(board.TenantId, board.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.ValidateBoardAsync(TrackerBoard(board, t.Connection), cancellationToken).ConfigureAwait(false)
            : await _inner.ValidateBoardAsync(board, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (await TrackerAsync(board.TenantId, board.Id, cancellationToken).ConfigureAwait(false) is not { } t)
        {
            return await _inner.ReadBoardTicketsAsync(board, cancellationToken).ConfigureAwait(false);
        }
        var tickets = await t.Provider.ReadBoardTicketsAsync(TrackerBoard(board, t.Connection), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> repos;
        using (var scope = _scopes.CreateScope())
        {
            repos = scope.ServiceProvider.GetService<IBoardRepos>() is { } boardRepos
                ? await boardRepos.RepoKeysAsync(board.TenantId, board.Id, cancellationToken).ConfigureAwait(false)
                : [];
        }
        return new BoardTickets(TrackerRepoMap.Assign(tickets.Items, repos));
    }

    public async Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.ReadTicketBodyAsync(TrackerWorkspace(repo, t.Connection), number, cancellationToken).ConfigureAwait(false)
            : await _inner.ReadTicketBodyAsync(repo, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketContent> ReadTicketAsync(WorkspaceDescriptor repo, int number, int maxComments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.ReadTicketAsync(TrackerWorkspace(repo, t.Connection), number, maxComments, cancellationToken).ConfigureAwait(false)
            : await _inner.ReadTicketAsync(repo, number, maxComments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CommentOnTicketAsync(WorkspaceDescriptor repo, int number, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.CommentOnTicketAsync(TrackerWorkspace(repo, t.Connection), number, body, cancellationToken).ConfigureAwait(false)
            : await _inner.CommentOnTicketAsync(repo, number, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MoveTicketAsync(WorkspaceDescriptor repo, int number, TicketMilestone milestone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.MoveTicketAsync(TrackerWorkspace(repo, t.Connection), number, milestone, cancellationToken).ConfigureAwait(false)
            : await _inner.MoveTicketAsync(repo, number, milestone, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> LinkPullRequestAsync(WorkspaceDescriptor repo, int number, string pullRequestUrl, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.LinkPullRequestAsync(TrackerWorkspace(repo, t.Connection), number, pullRequestUrl, title, cancellationToken).ConfigureAwait(false)
            : await _inner.LinkPullRequestAsync(repo, number, pullRequestUrl, title, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(repo.TenantId, repo.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.EnsureLabelsAsync(TrackerWorkspace(repo, t.Connection), labels, cancellationToken).ConfigureAwait(false)
            : await _inner.EnsureLabelsAsync(repo, labels, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(repo);
        return await TrackerAsync(board.TenantId, board.Id, cancellationToken).ConfigureAwait(false) is { } t
            ? await t.Provider.CreateTicketsAsync(TrackerBoard(board, t.Connection), TrackerWorkspace(repo, t.Connection), drafts, cancellationToken).ConfigureAwait(false)
            : await _inner.CreateTicketsAsync(board, repo, drafts, cancellationToken).ConfigureAwait(false);
    }

    // ── Code-host members: never routed ──────────────────────────────────────────────────────────

    public Task<RepoValidation> ValidateAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
        _inner.ValidateAsync(workspace, cancellationToken);

    public Task<IReadOnlyList<RemoteRepo>> ListRepositoriesAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default) =>
        _inner.ListRepositoriesAsync(credentials, cancellationToken);

    public Task<RepoContext> ReadRepoContextAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
        _inner.ReadRepoContextAsync(workspace, cancellationToken);

    public Task<IReadOnlyList<RepoInstructionFile>> ReadDesignSkillsAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default) =>
        _inner.ReadDesignSkillsAsync(workspace, cancellationToken);

    public Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default) =>
        _inner.ListBoardsAsync(credentials, cancellationToken);
}
