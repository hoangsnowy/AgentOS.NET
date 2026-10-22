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

/// <summary>Where a proposed change stands with the Testing agent.</summary>
public enum ChangeTesting { Untested, Testing, Accepted, Rejected }

/// <summary>An open proposed change of a task.</summary>
public sealed record PlanChange(Guid RepoId, int PrNumber, PrChecksState Checks, string? FailedReport,
    ChangeTesting Testing = ChangeTesting.Untested, string? Findings = null, PrMergeState Merge = PrMergeState.Clean,
    string BaseBranch = "main");

/// <summary>A task as the planner sees it.</summary>
public sealed record PlanTask(Guid SessionId, int IssueNumber, string Status, IReadOnlyList<PlanChange> OpenChanges, int FixRounds);

/// <summary>One thing to do, and the sentence that explains it.</summary>
public abstract record AutopilotAction(string Why)
{
    public sealed record StartTicket(int Number, string Why) : AutopilotAction(Why);
    public sealed record FixChecks(Guid SessionId, int IssueNumber, string Report, int Round, string Why) : AutopilotAction(Why);
    public sealed record UpdateBranch(Guid SessionId, int IssueNumber, Guid RepoId, int PrNumber, string Why) : AutopilotAction(Why);
    public sealed record RunTests(Guid SessionId, int IssueNumber, string Why) : AutopilotAction(Why);
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

    /// <summary>Acceptance runs at once: a test brings the app up on fixed ports, so two at once on one computer collide
    /// (seen on the lab: the second one found the ports taken and could not check its end-to-end criteria).</summary>
    public const int MaxParallelTests = 1;

    /// <summary>What Autopilot does next on a board, given its tickets and tasks — a pure decision, executed by the
    /// orchestrator.</summary>
    /// <param name="mode">Off, Build, or BuildAndMerge.</param>
    /// <param name="tickets">The board's tickets.</param>
    /// <param name="tasks">The board's open tasks and their proposed changes.</param>
    /// <param name="canTest">A computer of the member is connected, so the Testing agent can run. Without one nothing
    /// is tested — and nothing untested is merged.</param>
    public static IReadOnlyList<AutopilotAction> Decide(AutopilotMode mode, IReadOnlyList<PlanTicket> tickets, IReadOnlyList<PlanTask> tasks,
        bool canTest = true)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(tasks);
        var actions = new List<AutopilotAction>();
        if (mode == AutopilotMode.Off) { return actions; }

        var running = tasks.Count(t => t.Status == "Running");
        var testing = tasks.Count(t => t.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing));

        // 1) Proposed changes waiting on their checks: fix red ones (bounded), merge green ones (when allowed).
        foreach (var task in tasks.Where(t => t.Status == "Done"))
        {
            // Another change merged first and this one now conflicts: the Coding agent brings it up to date.
            var conflicting = task.OpenChanges.Where(c => c.Merge == PrMergeState.Conflicts).ToList();
            if (conflicting.Count > 0)
            {
                if (task.FixRounds >= MaxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        "Still conflicts with its base after automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: it still conflicts with {conflicting[0].BaseBranch}. It needs you."));
                    continue;
                }
                if (running >= MaxParallel) { continue; }
                var conflictRound = task.FixRounds + 1;
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber,
                    $"Your branch conflicts with {conflicting[0].BaseBranch} (another change was merged first). Merge origin/{conflicting[0].BaseBranch} "
                    + "into it, resolve every conflict keeping the intent of both changes, build, run the tests, commit and push.",
                    conflictRound,
                    $"#{task.IssueNumber} conflicts with {conflicting[0].BaseBranch} — asked the AI to bring it up to date (try {conflictRound} of {MaxFixRounds})."));
                running++;
                continue;
            }
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
            // Testing comes after green checks; nothing moves while a test runs or a check is still going.
            if (task.OpenChanges.Count == 0 || task.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing)
                || !task.OpenChanges.All(c => c.Checks == PrChecksState.Passed))
            {
                continue;
            }
            var rejected = task.OpenChanges.Where(c => c.Testing == ChangeTesting.Rejected).ToList();
            if (rejected.Count > 0)
            {
                if (task.FixRounds >= MaxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        $"Acceptance testing still fails after {MaxFixRounds} automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: it still fails acceptance testing after {MaxFixRounds} tries. It needs you."));
                    continue;
                }
                if (running >= MaxParallel) { continue; }
                var round = task.FixRounds + 1;
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber,
                    string.Join('\n', rejected.Select(c => c.Findings).Where(r => !string.IsNullOrWhiteSpace(r))), round,
                    $"#{task.IssueNumber} failed acceptance testing — sent back to the AI with the findings (try {round} of {MaxFixRounds})."));
                running++;
                continue;
            }
            if (task.OpenChanges.Any(c => c.Testing == ChangeTesting.Untested))
            {
                if (!canTest || running >= MaxParallel || testing >= MaxParallelTests) { continue; }
                actions.Add(new AutopilotAction.RunTests(task.SessionId, task.IssueNumber,
                    $"Testing #{task.IssueNumber} against its acceptance criteria — its checks passed."));
                running++;
                testing++;
                continue;
            }
            if (mode == AutopilotMode.BuildAndMerge)
            {
                // Its base moved on without conflicts: update the branch first (checks run again), merge next pass.
                var behind = task.OpenChanges.Where(c => c.Merge == PrMergeState.Behind).ToList();
                if (behind.Count > 0)
                {
                    actions.AddRange(behind.Select(c => new AutopilotAction.UpdateBranch(task.SessionId, task.IssueNumber, c.RepoId, c.PrNumber,
                        $"Brought #{task.IssueNumber}'s change (PR #{c.PrNumber}) up to date with {c.BaseBranch} before merging.")));
                    continue;
                }
                foreach (var change in task.OpenChanges)
                {
                    actions.Add(new AutopilotAction.Merge(task.SessionId, task.IssueNumber, change.RepoId, change.PrNumber,
                        $"Merged #{task.IssueNumber}'s change (PR #{change.PrNumber}) — checks green and acceptance testing passed."));
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
