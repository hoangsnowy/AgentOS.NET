// A blocked task picks up again when its project's maintainer answers on the ticket. Found dogfooding on the lab
// (2026-11): Autopilot stopped a task — "Code review still asks for changes after 3 automatic fixes — needs a person." —
// the product owner decided on the GitHub ticket ("digest pinning is out of scope, keep version tags"), and the task
// stayed Blocked forever; the only way on was Board › Tasks › Ask for changes, which a non-technical owner won't find.
//
// The decision is pure (unit-tested); ProjectOrchestrator reads the ticket for its Blocked tasks and carries it out the
// way "Ask for changes" does (same branch, same pull request, one more fix round). What counts as an answer:
//   - a MAINTAINER comment (TicketDiscussion.RoleOf: OWNER / MEMBER / COLLABORATOR; AgentOS.NET's own signed comments
//     and bots are never one — AgentOS posts with the owner's account, so the signature tells them apart);
//   - written after the task became Blocked, and after the newest answer already acted on (each answer resumes once);
//   - not a reply to the Orchestrator agent's question while that agent decides — it answers those itself (it may close
//     the ticket rather than code), and acknowledges them on the ticket.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>What Autopilot does about a blocked task's ticket comments.</summary>
public enum TicketAnswerOutcome
{
    /// <summary>A maintainer answered: send the task back to the AI with the answer.</summary>
    Resume,

    /// <summary>Settings › Autopilot › "Continue when a maintainer answers on the ticket" is off.</summary>
    TurnedOff,

    /// <summary>Autopilot doesn't build on this board.</summary>
    NotBuilding,

    /// <summary>No maintainer comment after the block (or after the last answer acted on).</summary>
    NoNewAnswer,

    /// <summary>The comments answer the Orchestrator agent's question, and that agent decides — it acts on them.</summary>
    LeftToOrchestrator,
}

/// <summary>The decision about a blocked task's ticket comments.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Answers">The maintainer comments that count, oldest first (empty unless there are any).</param>
/// <param name="AnsweredAt">The newest answer's time — persisted once acted on, so it never resumes the task again.</param>
/// <param name="AnswersAQuestion">The answers follow a question the Orchestrator agent asked on the ticket: acting on
/// them posts the same signed acknowledgement the agent's own retry does.</param>
public sealed record TicketAnswerDecision(TicketAnswerOutcome Outcome, IReadOnlyList<TicketDiscussionComment> Answers,
    DateTimeOffset? AnsweredAt, bool AnswersAQuestion)
{
    /// <summary>Nothing to do, for <paramref name="outcome"/>.</summary>
    public static TicketAnswerDecision None(TicketAnswerOutcome outcome) => new(outcome, [], null, false);
}

/// <summary>Reads a maintainer's answer to a blocked task from its ticket's comments.</summary>
public static class TicketAnswers
{
    /// <summary>The most characters of answers sent to the AI (each comment is clipped to
    /// <see cref="TicketDiscussion.MaxCommentChars"/>; the newest are kept).</summary>
    public const int MaxRequestChars = 8_000;

    /// <summary>True when an answer may resume a task at all — Autopilot builds on the board and the setting is on.
    /// Checked before any ticket is read.</summary>
    public static bool Applies(AutopilotMode mode, bool enabled) => enabled && mode != AutopilotMode.Off;

    /// <summary>
    /// The decision for a blocked task. <paramref name="blockedAt"/> is when it became Blocked; a task blocked before that
    /// was recorded falls back to <paramref name="taskCreatedAt"/> (any maintainer comment since counts — once).
    /// </summary>
    public static TicketAnswerDecision Decide(AutopilotMode mode, bool enabled, bool orchestratorDecides,
        DateTimeOffset? blockedAt, DateTimeOffset? answeredAt, DateTimeOffset taskCreatedAt, IReadOnlyList<TicketDiscussionComment>? comments)
    {
        if (!enabled) { return TicketAnswerDecision.None(TicketAnswerOutcome.TurnedOff); }
        if (mode == AutopilotMode.Off) { return TicketAnswerDecision.None(TicketAnswerOutcome.NotBuilding); }
        if (comments is null || comments.Count == 0) { return TicketAnswerDecision.None(TicketAnswerOutcome.NoNewAnswer); }

        var cutoff = blockedAt ?? taskCreatedAt;
        if (answeredAt is { } used && used > cutoff) { cutoff = used; }
        // Stable: equal timestamps keep the provider's order.
        var ordered = comments.Select((c, i) => (c, i)).OrderBy(x => x.c.CreatedAt).ThenBy(x => x.i).Select(x => x.c).ToList();
        var answers = ordered.Where(c => c.CreatedAt > cutoff && c.Role == TicketCommentRole.Maintainer).ToList();
        if (answers.Count == 0) { return TicketAnswerDecision.None(TicketAnswerOutcome.NoNewAnswer); }

        // The newest thing AgentOS.NET said before the newest answer: a question asked since the block means these answer it.
        var newest = answers[^1].CreatedAt;
        var lastAgentOs = ordered.LastOrDefault(c => c.CreatedAt <= newest && !TicketDiscussion.IsBot(c) && TicketDiscussion.IsAgentOsComment(c.Body));
        var answersAQuestion = lastAgentOs is not null && lastAgentOs.CreatedAt > cutoff && TicketDiscussion.IsAgentOsQuestion(lastAgentOs.Body);
        if (answersAQuestion && orchestratorDecides)
        {
            return new TicketAnswerDecision(TicketAnswerOutcome.LeftToOrchestrator, answers, newest, true);
        }
        return new TicketAnswerDecision(TicketAnswerOutcome.Resume, answers, newest, answersAQuestion);
    }

    /// <summary>
    /// The request sent to the Coding agent (as "Ask for changes" sends a person's words): the maintainer's comments,
    /// newest last, each with its author and time, within <see cref="MaxRequestChars"/>. The agent prompt fences it as
    /// untrusted text like every requested change.
    /// </summary>
    public static string Request(IReadOnlyList<TicketDiscussionComment> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var parts = answers.Select(c => $"— {SafeLogin(c.Author)} on {c.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC:\n"
            + Clip(c.Body.Trim(), TicketDiscussion.MaxCommentChars)).ToList();
        // Newest kept first when the budget runs out.
        var kept = new List<string>();
        var used = 0;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (kept.Count > 0 && used + parts[i].Length + 2 > MaxRequestChars) { break; }
            kept.Add(parts[i]);
            used += parts[i].Length + 2;
        }
        kept.Reverse();
        var sb = new StringBuilder(used + 400);
        sb.Append(answers.Count == 1
            ? "The project's maintainer answered on the ticket:\n\n"
            : "The project's maintainer answered on the ticket (oldest first, newest last):\n\n");
        if (kept.Count < parts.Count)
        {
            sb.Append('(').Append((parts.Count - kept.Count).ToString(CultureInfo.InvariantCulture)).Append(" earlier answer(s) left out — over the size limit.)\n\n");
        }
        sb.AppendJoin("\n\n", kept);
        sb.Append("\n\nContinue on the same branch and pull request. Do what the answer says — where answers differ, the newest one wins — "
            + "and say in the pull request what you changed because of it.");
        return sb.ToString();
    }

    // A login as GitHub allows it (letters, digits, '-'), at most 40 characters.
    private static string SafeLogin(string? login)
    {
        var sb = new StringBuilder(41).Append('@');
        foreach (var ch in login ?? string.Empty)
        {
            if (sb.Length > 40) { break; }
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') { sb.Append(ch); }
        }
        return sb.Length == 1 ? "a maintainer" : sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + " […]";
}
