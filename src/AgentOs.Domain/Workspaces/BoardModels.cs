// Board reshape — provider-neutral projections of a planning board and its items (tickets). A board
// (GitHub Projects v2 / ADO board) aggregates work items that span MANY repos, so a BoardTicket
// carries its source repo (null for a draft item that isn't backed by a repo issue yet). These are
// the board-level peers of RemoteRepo / RepoContext.

using System.Collections.Generic;

namespace AgentOs.Domain.Workspaces;

/// <summary>What a board item is backed by. GitHub Projects v2 items wrap an Issue, a PullRequest, or a DraftIssue.</summary>
public enum BoardTicketKind
{
    /// <summary>A repo issue tracked on the board.</summary>
    Issue = 0,

    /// <summary>A pull request tracked on the board.</summary>
    PullRequest = 1,

    /// <summary>A board-only draft item not yet backed by a repo issue (no repo, no number).</summary>
    DraftIssue = 2,
}

/// <summary>A board surfaced by <see cref="ISourceProvider.ListBoardsAsync"/> for the connect picker.</summary>
/// <param name="Number">Board number (GitHub Projects v2 <c>number</c>).</param>
/// <param name="NodeId">GraphQL node id.</param>
/// <param name="Title">Board title.</param>
/// <param name="Scope"><c>org</c> or <c>user</c>.</param>
/// <param name="OwnerLogin">Login of the owning org/user.</param>
public sealed record BoardSummary(int Number, string NodeId, string Title, string Scope, string OwnerLogin);

/// <summary>Outcome of validating a board exists and the token can read it.</summary>
/// <param name="Ok">True when the board was resolved.</param>
/// <param name="NodeId">Resolved board node id when <see cref="Ok"/>.</param>
/// <param name="Title">Resolved board title when <see cref="Ok"/>.</param>
/// <param name="Error">Human-readable reason when not <see cref="Ok"/>.</param>
public sealed record BoardValidation(bool Ok, string? NodeId = null, string? Title = null, string? Error = null)
{
    /// <summary>A successful validation carrying the resolved board node id + title.</summary>
    public static BoardValidation Success(string nodeId, string title) => new(true, nodeId, title);

    /// <summary>A failed validation carrying the reason.</summary>
    public static BoardValidation Fail(string error) => new(false, Error: error);
}

/// <summary>One item on a board, projected provider-neutrally.</summary>
/// <param name="ItemNodeId">GraphQL node id of the board ITEM (stable per board, distinct from the issue).</param>
/// <param name="Number">Issue/PR number in its repo; null for a draft item.</param>
/// <param name="Title">Item title.</param>
/// <param name="State">Provider state (e.g. <c>OPEN</c>/<c>CLOSED</c>), or empty for a draft.</param>
/// <param name="Kind">Whether the item is an Issue, PullRequest, or DraftIssue.</param>
/// <param name="RepoOwner">Owner of the backing repo; null for a draft item.</param>
/// <param name="RepoName">Name of the backing repo; null for a draft item.</param>
/// <param name="HtmlUrl">Browser URL of the item (issue/PR URL, or the board for a draft).</param>
/// <param name="Labels">Issue/PR labels (empty for a draft).</param>
/// <param name="Status">The board's single-select <c>Status</c> field value (e.g. <c>Todo</c>/<c>In Progress</c>), or null if unset.</param>
/// <param name="AssigneeLogin">First assignee login, or null.</param>
/// <param name="Key">How the tracker names it when that isn't <c>#Number</c> (Jira <c>SHOP-12</c>, ADO <c>AB#12</c>); null for GitHub.</param>
public sealed record BoardTicket(
    string ItemNodeId,
    int? Number,
    string Title,
    string State,
    BoardTicketKind Kind,
    string? RepoOwner,
    string? RepoName,
    string HtmlUrl,
    IReadOnlyList<string> Labels,
    string? Status,
    string? AssigneeLogin,
    string? Key = null)
{
    /// <summary>How the ticket is written for people: its tracker key, else <c>#Number</c>.</summary>
    public string Reference => Key ?? (Number is { } n ? "#" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) : Title);
}

/// <summary>The items on a board, as read by <see cref="ISourceProvider.ReadBoardTicketsAsync"/>.</summary>
public sealed record BoardTickets(IReadOnlyList<BoardTicket> Items);
