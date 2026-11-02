// Coherence Phase 2 (A2a) — adapts a Board ticket into the pipeline's UserStory input so the Quality
// engine (the 5-agent SDLC loop) can run against a real board ticket. Pure + static: no DI, no I/O —
// the Board run-path calls it inside its Task.Run scope to build the story it streams.

using System.Collections.Generic;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Pipeline.Sessions;

/// <summary>Builds a <see cref="UserStory"/> from a Board ticket's title + body.</summary>
public static class IssueToStoryAdapter
{
    /// <summary>
    /// Compose a pipeline <see cref="UserStory"/> from a ticket. Title and body are joined into the
    /// story description (the title carries the headline intent; the body the detail) and fenced with
    /// <see cref="UntrustedText"/> — a ticket is third-party text, so the Requirement agent treats it as data. <paramref name="nMax"/>
    /// is the QA-loop cap (already clamped to the tenant/admin bound by the caller); the
    /// <see cref="UserStory"/> validator re-clamps to [1,10].
    /// </summary>
    /// <param name="discussion">The ticket's discussion (<see cref="TicketDiscussion.ForAgents"/>): its MAINTAINER decisions
    /// are appended to the story — the newest one wins over the body — while other people's comments are left out (the
    /// story is one fenced text, so it carries only what has the ticket's authority).</param>
    public static UserStory ToUserStory(string title, string? body, int nMax, string locale = "en-US",
        IReadOnlyList<TicketDiscussionComment>? discussion = null)
    {
        var text = string.IsNullOrWhiteSpace(body)
            ? (title ?? string.Empty).Trim()
            : $"{(title ?? string.Empty).Trim()}\n\n{body.Trim()}";
        var decisions = TicketDiscussion.MaintainerDigest(discussion, MaxDecisionChars);
        if (decisions.Length > 0)
        {
            text += "\n\nDecisions the maintainers wrote in the ticket's comments (oldest first; where they differ from the text "
                + "above, the newest decision wins):\n" + decisions;
        }
        return new UserStory(UntrustedText.Wrap("ticket", text), nMax, locale);
    }

    // The maintainer decisions' share of the story (≈1k tokens).
    private const int MaxDecisionChars = 4_000;
}
