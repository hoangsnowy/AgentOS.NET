// Azure DevOps source provider. Repo connection (validate + list + ground) runs LIVE over the ADO REST
// API (AzureDevOpsRestClient) — the official client SDK does not resolve under net10, so we use raw HTTP
// like the GitHub Projects client. Boards / work items are a separate milestone and stay NotSupported.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration.Sources;

/// <summary><see cref="ISourceProvider"/> for Azure DevOps — repos over REST; boards deferred.</summary>
public sealed class AzureDevOpsSourceProvider : ISourceProvider
{
    private const string DefaultHost = "dev.azure.com";

    internal const string BoardsNotSupportedMessage =
        "Azure DevOps boards / work items are not yet supported — connect a repo, or use GitHub for board features.";

    private readonly IHttpClientFactory? _httpFactory;

    /// <summary>Parameterless ctor keeps the board members usable (and tests simple) without an HttpClient.</summary>
    public AzureDevOpsSourceProvider(IHttpClientFactory? httpFactory = null) => _httpFactory = httpFactory;

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

    // ── Boards / work items — separate milestone ─────────────────────────────────────────────────

    public Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(BoardsNotSupportedMessage);

    public Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
        => Task.FromResult(BoardValidation.Fail(BoardsNotSupportedMessage));

    public Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(BoardsNotSupportedMessage);

    // ADO boards/work items aren't wired yet — no body to read; the run proceeds on the title.
    public Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(BoardsNotSupportedMessage);

    public Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(BoardsNotSupportedMessage);
}
