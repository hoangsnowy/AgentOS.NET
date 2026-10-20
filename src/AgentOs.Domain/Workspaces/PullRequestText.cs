// AgentOs.Domain/Workspaces/PullRequestText.cs
// Title + body of the pull request an agent opens for a ticket.

using System;
using System.Text.RegularExpressions;

namespace AgentOs.Domain.Workspaces;

/// <summary>
/// Builds the pull request text for a ticket. The first live run on a real project opened
/// "fix: #7 #7 Add xiangqi core…": the session title already carries "#7", every PR was a "fix" whatever the
/// ticket was, and nothing linked the PR back to its issue. Now: a Conventional Commits type from the ticket's
/// <c>type:*</c> label, the plain title, the issue number once at the end, and <c>Closes #N</c> in the body so
/// GitHub links the two and closes the ticket when the change is merged.
/// </summary>
public static partial class PullRequestText
{
    /// <summary>"feat: Add xiangqi core (#7)".</summary>
    public static string Title(int? issueNumber, string ticketTitle, string? ticketType)
    {
        ArgumentNullException.ThrowIfNull(ticketTitle);
        var title = LeadingIssueRef().Replace(ticketTitle.Trim(), string.Empty).Trim();
        if (title.Length == 0) { title = "Agent change"; }
        var prefix = ConventionalType(ticketType);
        return issueNumber is { } n ? $"{prefix}: {title} (#{n})" : $"{prefix}: {title}";
    }

    /// <summary>The agent's summary, then <c>Closes #N</c> on its own line.</summary>
    public static string Body(int? issueNumber, string? summary)
    {
        var text = string.IsNullOrWhiteSpace(summary) ? "Change prepared by an AgentOS.NET agent." : summary.Trim();
        return issueNumber is { } n ? $"{text}\n\nCloses #{n}" : text;
    }

    /// <summary>Maps a ticket's <c>type:*</c> label (or its bare value) to a Conventional Commits type.</summary>
    public static string ConventionalType(string? ticketType)
    {
        var t = ticketType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (t.StartsWith("type:", StringComparison.Ordinal)) { t = t["type:".Length..].Trim(); }
        return t switch
        {
            "bug" or "fix" or "bugfix" or "defect" => "fix",
            "docs" or "doc" or "documentation" => "docs",
            "test" or "tests" => "test",
            "refactor" => "refactor",
            "chore" or "maintenance" => "chore",
            "perf" or "performance" => "perf",
            _ => "feat",
        };
    }

    // "#7 ", "#7: ", "#7 — " at the start of a title (the Board stores "#7 Title").
    [GeneratedRegex(@"^#\d+\s*[:\-—–]?\s*")]
    private static partial Regex LeadingIssueRef();
}
