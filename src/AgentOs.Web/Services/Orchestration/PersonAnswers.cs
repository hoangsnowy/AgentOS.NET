// A person's reply to the Autopilot Orchestrator's question, read back from the ticket's comments. AgentOS.NET signs
// what it posts (a "<sub>…AgentOS.NET…</sub>" line); a reply is any unsigned comment after the latest signed one, as
// long as a question was asked. Once AgentOS acts on it, it posts a signed acknowledgement — so the same reply never
// buys a second retry, and a failed attempt leads to a new question rather than a loop.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Reads a person's answer to the Orchestrator's question from a ticket's comments.</summary>
public static class PersonAnswers
{
    /// <summary>The heading every Orchestrator question starts with.</summary>
    public const string QuestionMarker = "AgentOS.NET needs you";

    private const int MaxAnswerChars = 2_000;

    /// <summary>True for a comment AgentOS.NET posted (it carries the signature line).</summary>
    public static bool IsAgentOs(string body) =>
        body.Contains("<sub>", StringComparison.Ordinal) && body.Contains("AgentOS.NET", StringComparison.Ordinal);

    /// <summary>The person's reply to the latest question, or null when there is none (yet).</summary>
    public static string? Latest(IReadOnlyList<TicketComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var ordered = comments.OrderBy(c => c.CreatedAt).ToList();
        var lastSigned = ordered.FindLastIndex(c => IsAgentOs(c.Body));
        if (lastSigned < 0) { return null; }
        // The reply must answer a question: the latest question comes no later than the latest signed comment, and
        // nothing AgentOS posted after it consumed the answer.
        var lastQuestion = ordered.FindLastIndex(c => IsAgentOs(c.Body) && c.Body.Contains(QuestionMarker, StringComparison.Ordinal));
        if (lastQuestion < 0 || lastQuestion != lastSigned) { return null; }
        var replies = ordered.Skip(lastSigned + 1).Where(c => !IsAgentOs(c.Body) && !string.IsNullOrWhiteSpace(c.Body))
            .Select(c => c.Body.Trim()).ToList();
        if (replies.Count == 0) { return null; }
        var text = string.Join("\n\n", replies);
        return text.Length <= MaxAnswerChars ? text : text[..MaxAnswerChars];
    }

    /// <summary>The signed acknowledgement posted when AgentOS acts on a reply.</summary>
    public static string Acknowledgement(string action) =>
        $"Thanks — {action}\n\n<sub>Posted by the AgentOS.NET Orchestrator agent after your answer.</sub>\n";

    // A branch an AgentOS attempt pushed for a ticket: issue-N-ai-fix, or issue-N-ai-fix-K for a later attempt.
    private static readonly System.Text.RegularExpressions.Regex AttemptBranch =
        new(@"^issue-(\d{1,7})-ai-fix(?:-\d{1,3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The ticket number an AgentOS attempt branch belongs to, or null for any other branch.</summary>
    public static int? TicketOfAttemptBranch(string branch) =>
        AttemptBranch.Match(branch ?? "") is { Success: true } m && int.TryParse(m.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
