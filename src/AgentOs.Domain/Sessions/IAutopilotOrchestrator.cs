// The Orchestrator agent: the project-level decision maker of Autopilot. Each time a board's state changes it reads
// the whole picture — tickets and their dependencies, every task and proposed change, failures, findings, free
// computer slots, recent history — and decides what happens next, with its reasons. It chooses only among moves the
// rules allow (the "legal moves": nothing untested is merged, no ticket starts before what it depends on, slots are
// capped), and it handles what the rules alone can't: retrying a failed task with guidance, and asking a person one
// precise question instead of a flat "needs a person".

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>A move the rules allow this pass, with its id (<c>M1</c>…) and what it does.</summary>
public sealed record OrchestratorMove(string Id, string Kind, int IssueNumber, string Description);

/// <summary>A task that is stuck (couldn't finish, or out of automatic fix rounds) — the agent may retry it with
/// guidance or ask a person a question about it.</summary>
public sealed record OrchestratorStuckTask(Guid SessionId, int IssueNumber, string Title, string Situation, int RetriesLeft)
{
    /// <summary>A person's reply to the agent's last question on this ticket, when there is one — new information
    /// that can justify one more retry, or closing the ticket when the person says so.</summary>
    public string? PersonAnswer { get; init; }
}

/// <summary>Everything the Orchestrator agent sees on one board.</summary>
/// <param name="Project">Board / repository name.</param>
/// <param name="Mode">Build or BuildAndMerge.</param>
/// <param name="Snapshot">Plain-text picture of the board: tickets (state, labels, depends-on), tasks and their changes.</param>
/// <param name="Moves">The legal moves this pass.</param>
/// <param name="Stuck">Stuck tasks it may retry with guidance or ask about.</param>
/// <param name="FreeSlots">Work slots free on the member's computer (start / fix / review / test take one each).</param>
/// <param name="RecentDecisions">The newest Autopilot log lines, newest first.</param>
public sealed record OrchestratorBrief(
    Guid BoardId,
    string TenantId,
    string MemberId,
    string Project,
    string Mode,
    string Snapshot,
    IReadOnlyList<OrchestratorMove> Moves,
    IReadOnlyList<OrchestratorStuckTask> Stuck,
    int FreeSlots,
    IReadOnlyList<string> RecentDecisions);

/// <summary>A stuck task to run again, with what to do differently.</summary>
public sealed record OrchestratorRetry(int IssueNumber, string Guidance);

/// <summary>A question for a person about a stuck task (posted on its ticket).</summary>
public sealed record OrchestratorQuestion(int IssueNumber, string Question);

/// <summary>A ticket to close because a person's answer says it is done or no longer wanted.</summary>
public sealed record OrchestratorClose(int IssueNumber, string Reason);

/// <summary>The Orchestrator agent's decision. <see cref="Ran"/> is false when it couldn't decide (no computer,
/// timed out, unreadable answer) — Autopilot then follows the rules for this pass.</summary>
public sealed record OrchestratorDecision(
    bool Ran,
    string Reasoning,
    IReadOnlyList<string> MoveIds,
    IReadOnlyList<OrchestratorRetry> Retries,
    IReadOnlyList<OrchestratorQuestion> Questions,
    string? Error)
{
    /// <summary>Tickets to close — only ever ones a person answered about.</summary>
    public IReadOnlyList<OrchestratorClose> Closes { get; init; } = [];

    /// <summary>A decision that couldn't be made.</summary>
    public static OrchestratorDecision NotRun(string why) => new(false, string.Empty, [], [], [], why);
}

/// <summary>Decides what Autopilot does next on a board.</summary>
public interface IAutopilotOrchestrator
{
    /// <summary>Choose among <paramref name="brief"/>'s moves and handle its stuck tasks.</summary>
    Task<OrchestratorDecision> DecideAsync(OrchestratorBrief brief, CancellationToken ct = default);
}
