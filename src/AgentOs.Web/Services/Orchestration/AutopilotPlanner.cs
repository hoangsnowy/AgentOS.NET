// The project orchestrator's decisions, as a pure function: board state in, actions out (each with the sentence the
// Autopilot log shows). Kept free of I/O so every rule is unit-tested; ProjectOrchestrator gathers the state from
// GitHub and the task store, and carries the actions out.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>What the orchestrator may do on a board.</summary>
public enum AutopilotMode
{
    /// <summary>Nothing happens unless a person clicks.</summary>
    Off,
    /// <summary>Start approved tickets in dependency order and fix failing checks; a person merges.</summary>
    Build,
    /// <summary>Build, and merge a proposed change once its checks pass, review approved it and acceptance testing passed it.</summary>
    BuildAndMerge,
}


/// <summary>A ticket as the planner sees it.</summary>
public sealed record PlanTicket(int Number, bool Open, bool AiReady, int Priority, IReadOnlyList<int> DependsOn, bool HasLiveTask);

/// <summary>Where a proposed change stands with the Testing agent.</summary>
public enum ChangeTesting { Untested, Testing, Accepted, Rejected }

/// <summary>Where a proposed change stands with the Reviewer agent (reviewed before it is tested).</summary>
public enum ChangeReview { NotReviewed, Reviewing, Approved, ChangesRequested }

/// <summary>An open proposed change of a task.</summary>
public sealed record PlanChange(Guid RepoId, int PrNumber, PrChecksState Checks, string? FailedReport,
    ChangeTesting Testing = ChangeTesting.Untested, string? Findings = null, PrMergeState Merge = PrMergeState.Clean,
    string BaseBranch = "main", ChangeReview Review = ChangeReview.Approved);

/// <summary>A task as the planner sees it.</summary>
/// <param name="FixRounds">The task's persisted fix rounds (every change sent back to the Coding agent, by any engine,
/// before or after a restart) — the one counter every cap reads.</param>
public sealed record PlanTask(Guid SessionId, int IssueNumber, string Status, IReadOnlyList<PlanChange> OpenChanges, int FixRounds)
{
    /// <summary>Its last run was cut short by the member's plan limit (nothing failed): it resumes once the plan
    /// resets, without spending a fix round or a retry.</summary>
    public bool CutShortByPlanLimit { get; init; }

    /// <summary>The steps this task runs — its ticket flow (the version it started with) resolved for its ticket's
    /// labels. The rules make no move for a step it passes through (no review when Review is off for it).</summary>
    public TicketFlowPlan Flow { get; init; } = TicketFlowPlan.Default;

    /// <summary>Where the Design gate stands for the task's current change (its review of the current fix round): the
    /// Designer agent's verdict, overridden by the owner's Approve design / Ask for changes. Read only when the task's
    /// flow runs Design.</summary>
    public AgentOs.Domain.Sessions.DesignGateState Design { get; init; } = AgentOs.Domain.Sessions.DesignGateState.NotChecked;

    /// <summary>What goes back to the Coding agent when the Design gate asks for changes.</summary>
    public string? DesignFindings { get; init; }
}

/// <summary>One thing to do, and the sentence that explains it.</summary>
public abstract record AutopilotAction(string Why)
{
    public sealed record StartTicket(int Number, string Why) : AutopilotAction(Why);
    public sealed record ResumeTask(Guid SessionId, int IssueNumber, string Why) : AutopilotAction(Why);
    public sealed record FixChecks(Guid SessionId, int IssueNumber, string Report, int Round, string Why) : AutopilotAction(Why);
    public sealed record UpdateBranch(Guid SessionId, int IssueNumber, Guid RepoId, int PrNumber, string Why) : AutopilotAction(Why);
    public sealed record RunTests(Guid SessionId, int IssueNumber, string Why) : AutopilotAction(Why);
    public sealed record RunReview(Guid SessionId, int IssueNumber, string Why) : AutopilotAction(Why);
    public sealed record RunDesign(Guid SessionId, int IssueNumber, string Why) : AutopilotAction(Why);
    public sealed record Merge(Guid SessionId, int IssueNumber, Guid RepoId, int PrNumber, string Why) : AutopilotAction(Why);
    public sealed record NeedsPerson(Guid SessionId, int IssueNumber, string Reason, string Why) : AutopilotAction(Why);
}

/// <summary>The rules.</summary>
public static class AutopilotPlanner
{
    /// <summary>What Autopilot does next on a board, given its tickets and tasks — a pure decision, executed by the
    /// orchestrator.</summary>
    /// <param name="mode">Off, Build, or BuildAndMerge.</param>
    /// <param name="tickets">The board's tickets.</param>
    /// <param name="tasks">The board's open tasks and their proposed changes.</param>
    /// <param name="canTest">A computer of the member is connected, so the Testing agent can run. Without one nothing
    /// is tested — and nothing untested is merged.</param>
    /// <param name="limits">The tenant's bounds (Settings › Autopilot; null = the defaults): slots, acceptance runs and
    /// fix rounds. The Orchestrator agent asks with <see cref="AutopilotLimits.Uncapped"/> for every legal move and
    /// <see cref="WithinCaps"/> applies the caps to the moves it picks, in its order. An acceptance run brings the app
    /// up on fixed ports, so two at once on one computer collide (seen on the lab) — hence its own cap.</param>
    public static IReadOnlyList<AutopilotAction> Decide(AutopilotMode mode, IReadOnlyList<PlanTicket> tickets, IReadOnlyList<PlanTask> tasks,
        bool canTest = true, AutopilotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(tasks);
        var actions = new List<AutopilotAction>();
        if (mode == AutopilotMode.Off) { return actions; }
        var caps = limits ?? AutopilotLimits.Default;
        var maxParallel = caps.MaxParallel;
        var maxParallelTests = caps.MaxParallelTests;
        var maxFixRounds = caps.MaxFixRounds;

        var running = tasks.Count(t => t.Status == "Running");
        var testing = tasks.Count(t => t.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing));

        // 0) Work a plan limit cut short resumes first — it was already under way, and spends no fix round or retry.
        foreach (var task in tasks.Where(t => t.Status == "Failed" && t.CutShortByPlanLimit))
        {
            if (running >= maxParallel) { break; }
            actions.Add(new AutopilotAction.ResumeTask(task.SessionId, task.IssueNumber,
                $"Picked #{task.IssueNumber} up again — your computer's AI plan is open again (no fix round used)."));
            running++;
        }

        // 1) Proposed changes waiting on their checks: fix red ones (bounded), merge green ones (when allowed).
        foreach (var task in tasks.Where(t => t.Status == "Done"))
        {
            // Another change merged first and this one now conflicts: the Coding agent brings it up to date.
            var conflicting = task.OpenChanges.Where(c => c.Merge == PrMergeState.Conflicts).ToList();
            if (conflicting.Count > 0)
            {
                if (task.FixRounds >= maxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        "Still conflicts with its base after automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: it still conflicts with {conflicting[0].BaseBranch}. It needs you."));
                    continue;
                }
                if (running >= maxParallel) { continue; }
                var conflictRound = task.FixRounds + 1;
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber,
                    $"Your branch conflicts with {conflicting[0].BaseBranch} (another change was merged first). Merge origin/{conflicting[0].BaseBranch} "
                    + "into it, resolve every conflict keeping the intent of both changes, build, run the tests, commit and push.",
                    conflictRound,
                    $"#{task.IssueNumber} conflicts with {conflicting[0].BaseBranch} — asked the AI to bring it up to date (try {conflictRound} of {maxFixRounds})."));
                running++;
                continue;
            }
            var failed = task.OpenChanges.Where(c => c.Checks == PrChecksState.Failed).ToList();
            if (failed.Count > 0)
            {
                if (task.FixRounds >= maxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        $"Checks still fail after {maxFixRounds} automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: its checks still fail after {maxFixRounds} tries. It needs you."));
                    continue;
                }
                if (running >= maxParallel) { continue; }
                var round = task.FixRounds + 1;
                var report = string.Join("\n", failed.Select(c => c.FailedReport).Where(r => !string.IsNullOrWhiteSpace(r)));
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber, report, round,
                    $"#{task.IssueNumber}'s checks failed — asked the AI to fix them (try {round} of {maxFixRounds})."));
                running++;
                continue;
            }
            // Review, then testing, come after green checks; nothing moves while one runs or a check is still going.
            if (task.OpenChanges.Count == 0 || task.OpenChanges.Any(c => c.Testing == ChangeTesting.Testing || c.Review == ChangeReview.Reviewing)
                || !task.OpenChanges.All(c => c.Checks == PrChecksState.Passed))
            {
                continue;
            }
            // The Reviewer agent asked for changes: they go back to the Coding agent (same bounded rounds as any fix).
            var changesRequested = task.OpenChanges.Where(c => c.Review == ChangeReview.ChangesRequested).ToList();
            if (changesRequested.Count > 0)
            {
                if (task.FixRounds >= maxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        $"Code review still asks for changes after {maxFixRounds} automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: code review still asks for changes after {maxFixRounds} tries. It needs you."));
                    continue;
                }
                if (running >= maxParallel) { continue; }
                var round = task.FixRounds + 1;
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber,
                    string.Join('\n', changesRequested.Select(c => c.Findings).Where(r => !string.IsNullOrWhiteSpace(r))), round,
                    $"Code review asked for changes on #{task.IssueNumber} — sent back to the AI (try {round} of {maxFixRounds})."));
                running++;
                continue;
            }
            // The task's ticket flow decides which of review and testing it gets (at least one — a safety invariant).
            if (task.Flow.Runs(TicketStepKind.Review) && task.OpenChanges.Any(c => c.Review == ChangeReview.NotReviewed))
            {
                // A review only reads the code: no app, no ports — just a free slot and the member's computer.
                if (!canTest || running >= maxParallel) { continue; }
                actions.Add(new AutopilotAction.RunReview(task.SessionId, task.IssueNumber,
                    $"Reviewing #{task.IssueNumber}'s change — its checks passed."));
                running++;
                continue;
            }
            var rejected = task.OpenChanges.Where(c => c.Testing == ChangeTesting.Rejected).ToList();
            if (rejected.Count > 0)
            {
                if (task.FixRounds >= maxFixRounds)
                {
                    actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                        $"Acceptance testing still fails after {maxFixRounds} automatic fixes — needs a person.",
                        $"Stopped fixing #{task.IssueNumber}: it still fails acceptance testing after {maxFixRounds} tries. It needs you."));
                    continue;
                }
                if (running >= maxParallel) { continue; }
                var round = task.FixRounds + 1;
                actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber,
                    string.Join('\n', rejected.Select(c => c.Findings).Where(r => !string.IsNullOrWhiteSpace(r))), round,
                    $"#{task.IssueNumber} failed acceptance testing — sent back to the AI with the findings (try {round} of {maxFixRounds})."));
                running++;
                continue;
            }
            if (task.Flow.Runs(TicketStepKind.Test) && task.OpenChanges.Any(c => c.Testing == ChangeTesting.Untested))
            {
                if (!canTest || running >= maxParallel || testing >= maxParallelTests) { continue; }
                actions.Add(new AutopilotAction.RunTests(task.SessionId, task.IssueNumber,
                    $"Testing #{task.IssueNumber} against its acceptance criteria — its checks passed."));
                running++;
                testing++;
                continue;
            }
            // The Design gate (ADR-0016) comes after testing, for the tickets whose flow runs it: nothing merges until the
            // Designer agent passed the screenshots or the owner approved them; "changes" go back to Coding.
            if (task.Flow.Runs(TicketStepKind.Design))
            {
                var gate = task.Design;
                if (gate is AgentOs.Domain.Sessions.DesignGateState.Checking or AgentOs.Domain.Sessions.DesignGateState.NeedsPerson)
                {
                    // Being checked, or the owner decides on Board › Tasks (Approve design / Ask for changes).
                    continue;
                }
                if (gate == AgentOs.Domain.Sessions.DesignGateState.ChangesRequested)
                {
                    if (task.FixRounds >= maxFixRounds)
                    {
                        actions.Add(new AutopilotAction.NeedsPerson(task.SessionId, task.IssueNumber,
                            $"Design review still asks for changes after {maxFixRounds} automatic fixes — needs a person.",
                            $"Stopped fixing #{task.IssueNumber}: design review still asks for changes after {maxFixRounds} tries. It needs you."));
                        continue;
                    }
                    if (running >= maxParallel) { continue; }
                    var round = task.FixRounds + 1;
                    actions.Add(new AutopilotAction.FixChecks(task.SessionId, task.IssueNumber, task.DesignFindings ?? "Design review asked for changes.", round,
                        $"Design review asked for changes on #{task.IssueNumber} — sent back to the AI (try {round} of {maxFixRounds})."));
                    running++;
                    continue;
                }
                if (gate == AgentOs.Domain.Sessions.DesignGateState.NotChecked)
                {
                    // The Designer reads screenshots — no app, no ports: a free slot and the member's computer.
                    if (!canTest || running >= maxParallel) { continue; }
                    actions.Add(new AutopilotAction.RunDesign(task.SessionId, task.IssueNumber,
                        $"Checking #{task.IssueNumber}'s screens against the project's design — its tests passed."));
                    running++;
                    continue;
                }
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
                        $"Merged #{task.IssueNumber}'s change (PR #{change.PrNumber}) — {Passed(task.Flow)}."));
                }
            }
        }

        // 2) Approved tickets whose dependencies are closed, while there is room: p0 first, then oldest.
        // (Under the Orchestrator agent every one is offered; it picks the order, the caps are applied after.)
        var closed = tickets.Where(t => !t.Open).Select(t => t.Number).ToHashSet();
        var ready = tickets
            .Where(t => t.Open && t.AiReady && !t.HasLiveTask && t.DependsOn.All(closed.Contains))
            .OrderBy(t => t.Priority).ThenBy(t => t.Number);
        foreach (var t in ready)
        {
            if (running >= maxParallel) { break; }
            var after = t.DependsOn.Count == 0 ? "" : $" — {string.Join(", ", t.DependsOn.Select(d => "#" + d))} {(t.DependsOn.Count == 1 ? "is" : "are")} done";
            actions.Add(new AutopilotAction.StartTicket(t.Number, $"Started #{t.Number}{after}."));
            running++;
        }
        return actions;
    }

    // What a change passed before it merges, as its ticket flow ran it ("checks green, review approved and acceptance
    // testing passed").
    private static string Passed(TicketFlowPlan flow)
    {
        var passed = new List<string> { "checks green" };
        if (flow.Runs(TicketStepKind.Review)) { passed.Add("review approved"); }
        if (flow.Runs(TicketStepKind.Test)) { passed.Add("acceptance testing passed"); }
        if (flow.Runs(TicketStepKind.Design)) { passed.Add("its design approved"); }
        return passed.Count == 1 ? passed[0] : string.Join(", ", passed.Take(passed.Count - 1)) + " and " + passed[^1];
    }

    /// <summary>True for a move that occupies a work slot on the member's computer.</summary>
    public static bool TakesSlot(AutopilotAction action) =>
        action is AutopilotAction.StartTicket or AutopilotAction.ResumeTask or AutopilotAction.FixChecks or AutopilotAction.RunReview
            or AutopilotAction.RunTests or AutopilotAction.RunDesign;

    /// <summary>
    /// The guardrail under the Orchestrator agent: the moves it picked, in its order, kept only while they fit — slots
    /// (<see cref="AutopilotLimits.MaxParallel"/>, counting <paramref name="running"/> tasks), the acceptance-run cap,
    /// and at most one move per task in a pass.
    /// </summary>
    public static IReadOnlyList<AutopilotAction> WithinCaps(IEnumerable<AutopilotAction> picked, int running, int testing, AutopilotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(picked);
        var caps = limits ?? AutopilotLimits.Default;
        var kept = new List<AutopilotAction>();
        var touched = new HashSet<Guid>();
        foreach (var action in picked)
        {
            var session = SessionOf(action);
            if (session is { } id && !touched.Add(id) && action is not AutopilotAction.Merge and not AutopilotAction.UpdateBranch) { continue; }
            if (TakesSlot(action))
            {
                if (running >= caps.MaxParallel) { continue; }
                if (action is AutopilotAction.RunTests)
                {
                    if (testing >= caps.MaxParallelTests) { continue; }
                    testing++;
                }
                running++;
            }
            kept.Add(action);
        }
        return kept;
    }

    private static Guid? SessionOf(AutopilotAction action) => action switch
    {
        AutopilotAction.FixChecks a => a.SessionId,
        AutopilotAction.ResumeTask a => a.SessionId,
        AutopilotAction.UpdateBranch a => a.SessionId,
        AutopilotAction.RunTests a => a.SessionId,
        AutopilotAction.RunReview a => a.SessionId,
        AutopilotAction.RunDesign a => a.SessionId,
        AutopilotAction.Merge a => a.SessionId,
        AutopilotAction.NeedsPerson a => a.SessionId,
        _ => null,
    };
}
