// Coherence Phase 2 (A2a) — adapts a Board ticket into the pipeline's UserStory input so the Quality
// engine (the 5-agent SDLC loop) can run against a real board ticket. Pure + static: no DI, no I/O —
// the Board run-path calls it inside its Task.Run scope to build the story it streams.

using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;

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
    public static UserStory ToUserStory(string title, string? body, int nMax, string locale = "en-US")
    {
        var text = string.IsNullOrWhiteSpace(body)
            ? (title ?? string.Empty).Trim()
            : $"{(title ?? string.Empty).Trim()}\n\n{body.Trim()}";
        return new UserStory(UntrustedText.Wrap("ticket", text), nMax, locale);
    }
}
