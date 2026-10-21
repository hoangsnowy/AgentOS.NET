// Bootstrap (slice 2) — the ticket-creation write models. A TicketDraft is a provider-neutral,
// not-yet-created ticket (the output of decomposing a RequirementSpec); CreateTicketsAsync turns each
// into a real repo issue added to the board and returns a CreatedTicket with the resolved ids. Real
// issues (not board drafts) because the downstream IIssueWorkAgent needs a real issue number + repo.

using System.Collections.Generic;

namespace AgentOs.Domain.Workspaces;

/// <summary>A ticket to create on a board's repo: title, markdown body, labels, and the ai-gate flag.</summary>
/// <param name="Title">Imperative one-line title.</param>
/// <param name="Body">Markdown body — typically the requirement plus a <c>- [ ]</c> acceptance-criteria checklist.</param>
/// <param name="Labels">Labels to apply (drawn from the standard taxonomy; includes the ai gate).</param>
/// <param name="AiReady">True when AgentOS.NET may auto-run this ticket (mirrors the <c>ai:ready</c> label).</param>
/// <param name="DependsOn">Indices (into the same draft list) of the tickets that must be done first — written into
/// the created ticket as "Depends on #N" (TicketDependencies). Null/empty = none.</param>
public sealed record TicketDraft(string Title, string Body, IReadOnlyList<string> Labels, bool AiReady, IReadOnlyList<int>? DependsOn = null);

/// <summary>A ticket created by <see cref="ISourceProvider.CreateTicketsAsync"/> — the resolved ids + URL.</summary>
/// <param name="Number">Repo issue number.</param>
/// <param name="NodeId">GraphQL node id of the issue (the board item's content id).</param>
/// <param name="HtmlUrl">Browser URL of the created issue.</param>
/// <param name="ItemNodeId">GraphQL node id of the board ITEM created when the issue was added to the board.</param>
/// <param name="Title">The created issue's title (echoed for display).</param>
public sealed record CreatedTicket(int Number, string NodeId, string HtmlUrl, string ItemNodeId, string Title);
