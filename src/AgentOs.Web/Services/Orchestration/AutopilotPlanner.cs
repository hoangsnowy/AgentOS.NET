// The project orchestrator's decisions, as a pure function: board state in, actions out (each with the sentence the
// Autopilot log shows). Kept free of I/O so every rule is unit-tested; ProjectOrchestrator gathers the state from
// GitHub and the task store, and carries the actions out.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>What the orchestrator may do on a board.</summary>
public enum AutopilotMode
{
    /// <summary>Nothing happens unless a person clicks.</summary>
    Off,
    /// <summary>Start approved tickets in dependency order and fix failing checks; a person merges.</summary>
    Build,
    /// <summary>Build, and merge a proposed change once all its checks pass.</summary>
    BuildAndMerge,
}


/// <summary>A ticket as the planner sees it.</summary>
public sealed record PlanTicket(int Number, bool Open, bool AiReady, int Priority, IReadOnlyList<int> DependsOn, bool HasLiveTask);

/// <summary>An open proposed change of a task.</summary>
public sealed record PlanChange(Guid RepoId, int PrNumber, PrChecksState Checks, string? FailedReport);

/// <summary>A task as the planner sees it.</summary>
public sealed record PlanTask(Guid SessionId, int IssueNumber, string Status, IReadOnlyList<PlanChange> OpenChanges, int FixRounds);

/// <summary>One thing to do, and the sentence that explains it.</summary>
public abstract record AutopilotAction(string Why)
{
    public sealed record StartTicket(int Number, string Why) : AutopilotAction(Why);
    public sealed record FixChecks(Guid SessionId, int IssueNumber, string Report, int Round, string Why) : AutopilotAction(Why);
    public sealed record Merge(Guid SessionId, int IssueNumber, Guid RepoId, int PrNumber, string Why) : AutopilotAction(Why);
    public sealed record NeedsPerson(Guid SessionId, int IssueNumber, string Reason, string Why) : AutopilotAction(Why);
}

/// <summary>The rules.</summary>
public static class AutopilotPlanner
{
    /// <summary>Automatic "Fix the failing checks" rounds before a person is asked.</summary>
    public const int MaxFixRounds = 3;

    /// <summary>Tasks running at once on a board.</summary>
    public const int MaxParallel = 2;

    public static IReadOnlyList<AutopilotAction> Decide(AutopilotMode mode, IReadOnlyList<PlanTicket> tickets, IReadOnlyList<PlanTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(tasks);
        var actions = new List<AutopilotAction>();
        if (mode == AutopilotMode.Off) { return actions; }

        var running = tasks.Count(t => t.Status == "Running");

        // 1) Proposed changes waiting on their checks: fix red ones (bounded), merge green ones (when allowed).
        foreach (var task in tasks.Where(t => t.Status == "Done"))
        {
            var failed = task.OpenChanges.Where(c => c.Checks == PrChecksState.Failed).ToList();
            if (failed.Count > 0)
            {
                if (task.FixRounds >= MaxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        $"Checks still fail after {MaxFixRounds} automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: its checks still fail after {MaxFixRounds} tries. It needs you."));
                    continue;
                }
                if (running >= MaxParallel) { continue; }
                var round = task.FixRounds + 1;
                var report = string.Join("\n", failed.Select(c => c.FailedReport).Where(r => !string.IsNullOrWhiteSpace(r)));
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber, report, round,
                    $"#{task.IssueNumber}'s checks failed — asked the AI to fix them (try {round} of {MaxFixRounds})."));
                running++;
                continue;
            }
            if (mode == AutopilotMode.BuildAndMerge && task.OpenChanges.Count > 0 && task.OpenChanges.All(c => c.Checks == PrChecksState.Passed))
            {
                foreach (var change in task.OpenChanges)
                {
                    actions.Add(new AutopilotAction.Merge(task.SessionId, task.IssueNumber, change.RepoId, change.PrNumber,
                        $"Merged #{task.IssueNumber}'s change (PR #{change.PrNumber}) — all checks passed."));
                }
            }
        }

        // 2) Approved tickets whose dependencies are closed, while there is room: p0 first, then oldest.
        var closed = tickets.Where(t => !t.Open).Select(t => t.Number).ToHashSet();
        var ready = tickets
            .Where(t => t.Open && t.AiReady && !t.HasLiveTask && t.DependsOn.All(closed.Contains))
            .OrderBy(t => t.Priority).ThenBy(t => t.Number);
        foreach (var t in ready)
        {
            if (running >= MaxParallel) { break; }
            var after = t.DependsOn.Count == 0 ? "" : $" — {string.Join(", ", t.DependsOn.Select(d => "#" + d))} {(t.DependsOn.Count == 1 ? "is" : "are")} done";
            actions.Add(new AutopilotAction.StartTicket(t.Number, $"Started #{t.Number}{after}."));
            running++;
        }
        return actions;
    }
}
