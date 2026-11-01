// One ticket's whole agent flow as a Microsoft Agent Framework Workflow: the Autopilot Orchestrator agent decides
// WHICH ticket starts; this graph then runs it. The graph is built from the ticket flow (ADR-0014 — the definition the
// owner edits in Workflow › Ticket flow, Code → Checks → Review → Test → Merge by default), resolved for the ticket's
// labels: one executor per step the ticket runs, a "pass" edge to the next one, a "fail" edge back to Code for every step
// that can send a change back (red checks, conflicts, requested changes and failed acceptance), bounded by the same fix
// rounds the rules use — Settings › Autopilot, counted from the task's persisted FixRounds, so the cap is the true total
// across engines and restarts. A plan that breaks a safety invariant is never built: the default flow runs instead.
// Each step is one of the existing agents (ITicketSteps — the Board's
// BoardTicketSteps in production), so governance, metrics (Insights) and evidence on the PR are unchanged. When the
// graph ends without a merge — "needs a person", or an agent that couldn't run — the task is handed back to the
// Orchestrator agent and the step-by-step rules, which also pick up any flow a restart cut short. A member's plan limit
// (their computer's Claude/Codex plan said "no more for now") pauses the flow before it spends a round on it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.BoardTasks;
using Microsoft.Agents.AI.Workflows;

namespace AgentOs.Web.Services.Orchestration;

/// <summary>Where a ticket's flow is, as the next edge reads it.</summary>
public enum TicketStage
{
    /// <summary>The Coding agent pushed a change; its checks come next.</summary>
    Coded,
    /// <summary>A verdict sent the change back to the Coding agent (<see cref="TicketFlow.NextChange"/>).</summary>
    Fix,
    /// <summary>Checks are green: review next.</summary>
    Green,
    /// <summary>The Reviewer approved: testing next.</summary>
    Approved,
    /// <summary>The Testing agent accepted it: merge next.</summary>
    Accepted,
    /// <summary>Merged — done.</summary>
    Merged,
    /// <summary>Accepted, but the board leaves merging to a person.</summary>
    ReadyToMerge,
    /// <summary>Out of automatic rounds — handed to the Orchestrator agent / a person.</summary>
    NeedsPerson,
    /// <summary>An agent couldn't run or finish — handed back to the step-by-step path.</summary>
    Stopped,
    /// <summary>The member's AI plan hit its limit — handed back; Autopilot picks it up after the reset.</summary>
    PlanLimited,
    /// <summary>A step of a kind added later (the Design gate) passed: on to the next step.</summary>
    Passed,
}

/// <summary>The state a ticket's workflow threads along every edge.</summary>
public sealed class TicketFlow(string tenantId, Guid boardId, Guid sessionId, int issueNumber, bool mayMerge)
{
    public string TenantId { get; } = tenantId;
    public Guid BoardId { get; } = boardId;
    public Guid SessionId { get; } = sessionId;
    public int IssueNumber { get; } = issueNumber;
    public bool MayMerge { get; } = mayMerge;
    public TicketStage Stage { get; set; } = TicketStage.Fix;
    /// <summary>Automatic fix rounds used — seeded from the task's persisted fix rounds, so a flow started after a
    /// restart (or after step-by-step rounds) has only what is left of the cap.</summary>
    public int Round { get; set; }
    /// <summary>The cap on <see cref="Round"/> (Settings › Autopilot › Automatic fix rounds).</summary>
    public int MaxRounds { get; init; } = AgentOs.Modules.Pipeline.Agents.AutopilotLimits.Default.MaxFixRounds;
    /// <summary>What the Coding agent is asked to change next (null = the ticket itself, first attempt).</summary>
    public string? NextChange { get; set; }
    /// <summary>Why the flow stopped (plain language), when it did.</summary>
    public string? Reason { get; set; }
    /// <summary>The steps taken, for the Autopilot log.</summary>
    public List<string> Trail { get; } = [];

    /// <summary>The steps this ticket runs — its ticket flow resolved for its labels, snapshotted when it started.</summary>
    public TicketFlowPlan Plan { get; init; } = TicketFlowPlan.Default;

    /// <summary>The ticket flow version <see cref="Plan"/> came from (0 = the built-in flow).</summary>
    public int FlowVersion { get; init; }

    /// <summary>The step running now (its node id), null before the first.</summary>
    public string? StepId { get; private set; }

    /// <summary>The kind of the step running now.</summary>
    public TicketStepKind? Step { get; private set; }

    /// <summary>Told whenever a step starts (the Workflow › Ticket flow view highlights it).</summary>
    public Action<TicketFlow>? OnStep { get; init; }

    internal void Enter(TicketFlowNode node)
    {
        StepId = node.Id;
        Step = node.Kind;
        OnStep?.Invoke(this);
    }

    /// <summary>Send the change back to the Coding agent with <paramref name="change"/> — or stop when the rounds are used up.</summary>
    public TicketFlow SendBack(string what, string change)
    {
        if (Round >= MaxRounds)
        {
            Stage = TicketStage.NeedsPerson;
            Reason = $"{what} after {MaxRounds} automatic fixes — needs a person.";
            return this;
        }
        Round++;
        NextChange = change;
        Stage = TicketStage.Fix;
        Trail.Add($"{what.ToLowerInvariant()} → back to Coding (try {Round} of {MaxRounds})");
        return this;
    }

    internal TicketFlow Stop(string why)
    {
        Stage = TicketStage.Stopped;
        Reason = why;
        return this;
    }

    internal TicketFlow PausedForPlan(string why)
    {
        Stage = TicketStage.PlanLimited;
        Reason = why;
        return this;
    }
}

/// <summary>A proposed change of the ticket's task, as a step reads it.</summary>
public sealed record TicketChange(Guid RepoId, string Status, string? PrUrl, string? Error, string DefaultBranch)
{
    /// <summary>Its pull request number, when it has one.</summary>
    public int? Pr => BoardTaskService.PrNumber(PrUrl);
}

/// <summary>The steps a ticket workflow takes — each one an existing agent or a source-control call.</summary>
public interface ITicketSteps
{
    /// <summary>Start the Coding agent (with <c>flow.NextChange</c> as the change request, when set).</summary>
    Task<TaskRun> CodeAsync(TicketFlow flow, CancellationToken ct);
    /// <summary>Start the Reviewer agent on the task's changes.</summary>
    Task<TaskRun> ReviewAsync(TicketFlow flow, CancellationToken ct);
    /// <summary>Start the Testing agent on the task's changes.</summary>
    Task<TaskRun> TestAsync(TicketFlow flow, CancellationToken ct);
    /// <summary>The task's proposed changes as they stand.</summary>
    Task<IReadOnlyList<TicketChange>> ChangesAsync(TicketFlow flow, CancellationToken ct);
    /// <summary>A change's pull request: its checks and whether it merges cleanly.</summary>
    Task<(PrChecksState Checks, PrMergeState Merge)> PrStateAsync(TicketFlow flow, TicketChange change, CancellationToken ct);
    /// <summary>What failed in a change's checks.</summary>
    Task<string?> FailedChecksAsync(TicketFlow flow, TicketChange change, CancellationToken ct);
    /// <summary>Bring a change that is behind its base up to date.</summary>
    Task UpdateBranchAsync(TicketFlow flow, TicketChange change, CancellationToken ct);
    /// <summary>Merge a change and record it; null on success, else the reason.</summary>
    Task<string?> MergeAsync(TicketFlow flow, TicketChange change, CancellationToken ct);

    /// <summary>Why no agent should run now — the member's AI plan is at its limit (plain words) — or null.</summary>
    string? PlanLimited(TicketFlow flow) => null;

    /// <summary>The Design gate (ADR-0016): run the Designer agent on the screenshots the Testing agent sent back (unless the
    /// owner already decided) and say where the gate stands. Without a design reviewer wired, a person decides.</summary>
    Task<DesignStep> DesignAsync(TicketFlow flow, CancellationToken ct) =>
        Task.FromResult(new DesignStep(AgentOs.Domain.Sessions.DesignGateState.NeedsPerson, null, "No design reviewer is set up."));
}

/// <summary>Where a ticket's Design gate stands after its step ran: the gate, what to send back to Coding on changes, and
/// why (plain words).</summary>
public sealed record DesignStep(AgentOs.Domain.Sessions.DesignGateState Gate, string? Findings, string? Why);

/// <summary>Builds and runs a ticket's MAF workflow.</summary>
public static class TicketWorkflow
{
    /// <summary>The default flow's graph (Code → Checks → Review → Test → Merge).</summary>
    public static Workflow Build(ITicketSteps steps, TimeSpan checksTimeout, TimeSpan pollInterval) =>
        Build(steps, TicketFlowPlan.Default, checksTimeout, pollInterval);

    /// <summary>The graph for <paramref name="plan"/> — the steps one ticket runs. A plan that breaks a safety invariant
    /// (<see cref="TicketFlowPlan.SafetyProblem"/>) is never built: the default flow's graph is.</summary>
    public static Workflow Build(ITicketSteps steps, TicketFlowPlan plan, TimeSpan checksTimeout, TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(plan);
        var nodes = (plan.SafetyProblem() is null ? plan : TicketFlowPlan.Default).Steps;
        var executors = nodes.Select(n => Create(n, steps, checksTimeout, pollInterval)).ToList();
        var code = executors[0];
        var done = new DoneExecutor();
        var builder = new WorkflowBuilder(code);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var from = executors[i];
            if (i == nodes.Count - 1)
            {
                builder.AddEdge(from, done);
                continue;
            }
            var passed = PassStage(node.Kind);
            var backToCode = node.Info.CanSendBack;
            builder.AddEdge(from, executors[i + 1], (TicketFlow? f) => f?.Stage == passed);
            if (backToCode) { builder.AddEdge(from, code, (TicketFlow? f) => f?.Stage == TicketStage.Fix); }
            builder.AddEdge(from, done, (TicketFlow? f) => f is not null && f.Stage != passed && !(backToCode && f.Stage == TicketStage.Fix));
        }
        return builder.WithOutputFrom(done).Build();
    }

    /// <summary>Run <paramref name="flow"/> (its <see cref="TicketFlow.Plan"/>) to its end; returns the final state.</summary>
    public static async Task<TicketFlow> RunAsync(ITicketSteps steps, TicketFlow flow, TimeSpan checksTimeout, TimeSpan pollInterval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var run = await InProcessExecution.RunAsync(Build(steps, flow.Plan, checksTimeout, pollInterval), flow, cancellationToken: ct).ConfigureAwait(false);
        return run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<TicketFlow>().LastOrDefault() ?? flow;
    }

    // The stage a step leaves behind when it passed — what its "pass" edge reads.
    private static TicketStage PassStage(TicketStepKind kind) => kind switch
    {
        TicketStepKind.Code => TicketStage.Coded,
        TicketStepKind.Checks => TicketStage.Green,
        TicketStepKind.Review => TicketStage.Approved,
        TicketStepKind.Test => TicketStage.Accepted,
        TicketStepKind.Merge => TicketStage.Merged,
        _ => TicketStage.Passed,
    };

    // One executor per step kind — a new kind (TicketStepKinds) adds its executor here.
    private static Executor<TicketFlow, TicketFlow> Create(TicketFlowNode node, ITicketSteps steps, TimeSpan checksTimeout, TimeSpan pollInterval) =>
        node.Kind switch
        {
            TicketStepKind.Code => new CodeExecutor(node, steps),
            TicketStepKind.Checks => new ChecksExecutor(node, steps, checksTimeout, pollInterval),
            TicketStepKind.Review => new ReviewExecutor(node, steps),
            TicketStepKind.Test => new TestExecutor(node, steps),
            TicketStepKind.Design => new DesignExecutor(node, steps),
            TicketStepKind.Merge => new MergeExecutor(node, steps),
            _ => throw new NotSupportedException($"No executor for the {node.Kind} step."),
        };

    // The member's plan is at its limit: stop before an agent runs (and after one ran into it) — a round spent then
    // would be spent on nothing.
    private static TicketFlow? PlanPause(ITicketSteps steps, TicketFlow f) => steps.PlanLimited(f) is { } why ? f.PausedForPlan(why) : null;

    private sealed class CodeExecutor(TicketFlowNode node, ITicketSteps steps) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            if (PlanPause(steps, f) is { } before) { return before; }
            var run = await steps.CodeAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Coding agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
            if (PlanPause(steps, f) is { } after) { return after; }
            var changes = await steps.ChangesAsync(f, ct).ConfigureAwait(false);
            if (changes.Count == 0 || changes.Any(c => c.Status is "Failed" or "Blocked" || c.Pr is null))
            {
                return f.Stop(changes.Select(c => c.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "The Coding agent didn't finish.");
            }
            f.NextChange = null;
            f.Stage = TicketStage.Coded;
            f.Trail.Add("coded");
            return f;
        }
    }

    private sealed class ChecksExecutor(TicketFlowNode node, ITicketSteps steps, TimeSpan timeout, TimeSpan poll) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            var deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                var states = new List<(TicketChange Change, PrChecksState Checks, PrMergeState Merge)>();
                foreach (var c in (await steps.ChangesAsync(f, ct).ConfigureAwait(false)).Where(c => c.Pr is not null))
                {
                    var (checks, merge) = await steps.PrStateAsync(f, c, ct).ConfigureAwait(false);
                    states.Add((c, checks, merge));
                }
                if (states.Find(s => s.Merge == PrMergeState.Conflicts) is { Change: { } conflicted })
                {
                    return f.SendBack("Conflicts with its base",
                        $"Your branch conflicts with {conflicted.DefaultBranch} (another change was merged first). Merge origin/{conflicted.DefaultBranch} "
                        + "into it, resolve every conflict keeping the intent of both changes, build, run the tests, commit and push.");
                }
                var failed = states.Where(s => s.Checks == PrChecksState.Failed).ToList();
                if (failed.Count > 0)
                {
                    var reports = new List<string>();
                    foreach (var s in failed) { reports.Add(await steps.FailedChecksAsync(f, s.Change, ct).ConfigureAwait(false) ?? $"PR #{s.Change.Pr}: checks failed."); }
                    return f.SendBack("Checks failed",
                        "The pull request's automated checks failed. Fix the cause (not the test, unless the test itself is wrong) so they pass:\n\n"
                        + string.Join("\n", reports));
                }
                if (states.Count > 0 && states.All(s => s.Checks == PrChecksState.Passed))
                {
                    f.Stage = TicketStage.Green;
                    f.Trail.Add("checks green");
                    return f;
                }
                foreach (var behind in states.Where(s => s.Merge == PrMergeState.Behind))
                {
                    await steps.UpdateBranchAsync(f, behind.Change, ct).ConfigureAwait(false);
                }
                await Task.Delay(poll, ct).ConfigureAwait(false);
            }
            while (DateTimeOffset.UtcNow < deadline);
            return f.Stop($"Checks didn't finish within {timeout.TotalMinutes:0} minutes.");
        }
    }

    private sealed class ReviewExecutor(TicketFlowNode node, ITicketSteps steps) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            if (PlanPause(steps, f) is { } before) { return before; }
            var run = await steps.ReviewAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Reviewer agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
            if (PlanPause(steps, f) is { } after) { return after; }
            var changes = await steps.ChangesAsync(f, ct).ConfigureAwait(false);
            if (changes.Any(c => c.Status == "ChangesRequested"))
            {
                return f.SendBack("Code review asked for changes",
                    string.Join("\n", changes.Where(c => c.Status == "ChangesRequested").Select(c => c.Error).Where(e => !string.IsNullOrWhiteSpace(e))));
            }
            if (changes.Count > 0 && changes.All(c => c.Status == "Reviewed"))
            {
                f.Stage = TicketStage.Approved;
                f.Trail.Add("review approved");
                return f;
            }
            return f.Stop("The Reviewer agent couldn't review the change.");
        }
    }

    private sealed class TestExecutor(TicketFlowNode node, ITicketSteps steps) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            if (PlanPause(steps, f) is { } before) { return before; }
            var run = await steps.TestAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Testing agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
            if (PlanPause(steps, f) is { } after) { return after; }
            var changes = await steps.ChangesAsync(f, ct).ConfigureAwait(false);
            if (changes.Any(c => c.Status == "Rejected"))
            {
                return f.SendBack("Acceptance testing failed",
                    string.Join("\n", changes.Where(c => c.Status == "Rejected").Select(c => c.Error).Where(e => !string.IsNullOrWhiteSpace(e))));
            }
            if (changes.Count > 0 && changes.All(c => c.Status == "Accepted"))
            {
                f.Stage = TicketStage.Accepted;
                f.Trail.Add("acceptance passed");
                return f;
            }
            return f.Stop("The Testing agent couldn't test the change.");
        }
    }

    // The Design gate: pass → on to Merge; changes (the Designer's or the owner's) → back to Code as a fix round; a
    // person's call → the flow hands the task back, and Autopilot merges once the owner approves on Board › Tasks.
    private sealed class DesignExecutor(TicketFlowNode node, ITicketSteps steps) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            if (PlanPause(steps, f) is { } before) { return before; }
            var step = await steps.DesignAsync(f, ct).ConfigureAwait(false);
            if (PlanPause(steps, f) is { } after) { return after; }
            switch (step.Gate)
            {
                case AgentOs.Domain.Sessions.DesignGateState.Passed:
                    f.Stage = TicketStage.Passed;
                    f.Trail.Add("design approved");
                    return f;
                case AgentOs.Domain.Sessions.DesignGateState.ChangesRequested:
                    return f.SendBack("Design review asked for changes", step.Findings ?? "Design review asked for changes.");
                case AgentOs.Domain.Sessions.DesignGateState.NeedsPerson:
                    f.Stage = TicketStage.NeedsPerson;
                    f.Reason = "The design needs a person's eye — approve it or ask for changes on Board › Tasks."
                        + (string.IsNullOrWhiteSpace(step.Why) ? "" : " " + step.Why);
                    return f;
                default:
                    return f.Stop(step.Why ?? "The Designer agent couldn't check the design.");
            }
        }
    }

    private sealed class MergeExecutor(TicketFlowNode node, ITicketSteps steps) : Executor<TicketFlow, TicketFlow>(node.Id)
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            f.Enter(node);
            if (!f.MayMerge)
            {
                f.Stage = TicketStage.ReadyToMerge;
                f.Trail.Add("ready for you to merge");
                return f;
            }
            foreach (var c in (await steps.ChangesAsync(f, ct).ConfigureAwait(false)).Where(c => c.Pr is not null))
            {
                if (await steps.MergeAsync(f, c, ct).ConfigureAwait(false) is { } error) { return f.Stop($"Couldn't merge PR #{c.Pr}: {error}"); }
            }
            f.Stage = TicketStage.Merged;
            f.Trail.Add("merged");
            return f;
        }
    }

    private sealed class DoneExecutor() : Executor<TicketFlow, TicketFlow>("done")
    {
        public override ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct) => ValueTask.FromResult(f);
    }
}
