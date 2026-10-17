// M2 — the provider-neutral source-control seam. GitHub and Azure DevOps each implement this; the
// Workspaces module + agents depend only on the interface (mirrors how the 5 agents depend only on
// ILlmClient). This is what lets "connect a repo" work the same regardless of backend, and what a
// later remote session (M4) clones/executes against.
//
// Scope note: this covers the M2 job — connect + read for grounding. PR creation currently lives in
// IGitHubPrService (Integration); M5 folds an OpenPullRequest member in here once the remote
// executor produces a working tree to push.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Workspaces;

/// <summary>A source-control backend (GitHub, Azure DevOps) AgentOS can connect a workspace to.</summary>
public interface ISourceProvider
{
    /// <summary>Which backend this implementation serves.</summary>
    SourceProviderKind Kind { get; }

    /// <summary>Check the token can reach the repo and resolve its default branch.</summary>
    Task<RepoValidation> ValidateAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default);

    /// <summary>List repositories the credentials can see — drives the "connect a workspace" picker.</summary>
    Task<IReadOnlyList<RemoteRepo>> ListRepositoriesAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Read README + top-level layout to ground the Requirement agent.</summary>
    Task<RepoContext> ReadRepoContextAsync(WorkspaceDescriptor workspace, CancellationToken cancellationToken = default);

    // ── Boards (GitHub Projects v2 / Azure DevOps board) ────────────────────────────────────────
    // A board is the planning layer that spans many repos. These read the board and its items; the
    // per-repo members above still drive validate/list/ground for the repos under the board.

    /// <summary>List boards the credentials can see — drives the "connect a board" picker.</summary>
    Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(ConnectionCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Check the token can reach the board and resolve its node id + title.</summary>
    Task<BoardValidation> ValidateBoardAsync(BoardDescriptor board, CancellationToken cancellationToken = default);

    /// <summary>Read the board's items (tickets) across all repos it spans.</summary>
    Task<BoardTickets> ReadBoardTicketsAsync(BoardDescriptor board, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the current description (body) of ticket <paramref name="number"/> on <paramref name="repo"/> — read
    /// at run time so the agent sees the latest edit, not a copy taken when the session was created. Returns
    /// null when the backend can't supply it (the run then proceeds on the title alone). The text is
    /// third-party input: callers must fence it (<c>UntrustedText.Wrap</c>) before it reaches a prompt.
    /// </summary>
    Task<string?> ReadTicketBodyAsync(WorkspaceDescriptor repo, int number, CancellationToken cancellationToken = default);

    // ── Writes (bootstrap) ───────────────────────────────────────────────────────────────────────
    // The first write members: stamping a standard label taxonomy and creating tickets from a
    // generated RequirementSpec. Labels are repo-scoped, so they take a WorkspaceDescriptor.

    /// <summary>
    /// Idempotently ensure each <paramref name="labels"/> entry exists on the repo (create the
    /// missing ones, skip those already present, matched by name case-insensitively).
    /// </summary>
    Task<LabelSyncResult> EnsureLabelsAsync(WorkspaceDescriptor repo, IReadOnlyList<LabelSpec> labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create each <paramref name="drafts"/> entry as a real issue on <paramref name="repo"/> and add
    /// it to <paramref name="board"/>. Labels are applied at creation; the repo must already carry them
    /// (seed via <see cref="EnsureLabelsAsync"/> first). Returns the created tickets in input order.
    /// </summary>
    Task<IReadOnlyList<CreatedTicket>> CreateTicketsAsync(BoardDescriptor board, WorkspaceDescriptor repo, IReadOnlyList<TicketDraft> drafts, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the right <see cref="ISourceProvider"/> for a <see cref="SourceProviderKind"/>.</summary>
public interface ISourceProviderResolver
{
    /// <summary>Return the provider for <paramref name="kind"/>, or throw if none is registered.</summary>
    ISourceProvider Resolve(SourceProviderKind kind);

    /// <summary>Try to return the provider for <paramref name="kind"/>.</summary>
    bool TryResolve(SourceProviderKind kind, out ISourceProvider? provider);
}
