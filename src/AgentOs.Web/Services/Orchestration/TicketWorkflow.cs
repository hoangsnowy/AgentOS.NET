// One ticket's whole agent flow as a Microsoft Agent Framework Workflow: the Autopilot Orchestrator agent decides
// WHICH ticket starts; this graph then runs it — Coding → checks → Reviewer → Testing → merge — routing each verdict
// along a conditional edge (red checks, conflicts, requested changes and failed acceptance go back to the Coding agent,
// bounded by the same 3 rounds the rules use). Each step is one of the existing agents (ITicketSteps — the Board's
// BoardTicketSteps in production), so governance, metrics (Insights) and evidence on the PR are unchanged. When the
// graph ends without a merge — "needs a person", or an agent that couldn't run — the task is handed back to the
// Orchestrator agent and the step-by-step rules, which also pick up any flow a restart cut short.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
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
    /// <summary>Automatic fix rounds used.</summary>
    public int Round { get; set; }
    /// <summary>What the Coding agent is asked to change next (null = the ticket itself, first attempt).</summary>
    public string? NextChange { get; set; }
    /// <summary>Why the flow stopped (plain language), when it did.</summary>
    public string? Reason { get; set; }
    /// <summary>The steps taken, for the Autopilot log.</summary>
    public List<string> Trail { get; } = [];

    /// <summary>Send the change back to the Coding agent with <paramref name="change"/> — or stop when the rounds are used up.</summary>
    public TicketFlow SendBack(string what, string change)
    {
        if (Round >= AutopilotPlanner.MaxFixRounds)
        {
            Stage = TicketStage.NeedsPerson;
            Reason = $"{what} after {AutopilotPlanner.MaxFixRounds} automatic fixes — needs a person.";
            return this;
        }
        Round++;
        NextChange = change;
        Stage = TicketStage.Fix;
        Trail.Add($"{what.ToLowerInvariant()} → back to Coding (try {Round} of {AutopilotPlanner.MaxFixRounds})");
        return this;
    }

    internal TicketFlow Stop(string why)
    {
        Stage = TicketStage.Stopped;
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
}

/// <summary>Builds and runs a ticket's MAF workflow.</summary>
public static class TicketWorkflow
{
    /// <summary>The graph.</summary>
    public static Workflow Build(ITicketSteps steps, TimeSpan checksTimeout, TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var code = new CodeExecutor(steps);
        var checks = new ChecksExecutor(steps, checksTimeout, pollInterval);
        var review = new ReviewExecutor(steps);
        var test = new TestExecutor(steps);
        var merge = new MergeExecutor(steps);
        var done = new DoneExecutor();
        return new WorkflowBuilder(code)
            .AddEdge(code, checks, (TicketFlow? f) => f?.Stage == TicketStage.Coded)
            .AddEdge(code, done, (TicketFlow? f) => f is not null && f.Stage != TicketStage.Coded)
            .AddEdge(checks, code, (TicketFlow? f) => f?.Stage == TicketStage.Fix)
            .AddEdge(checks, review, (TicketFlow? f) => f?.Stage == TicketStage.Green)
            .AddEdge(checks, done, (TicketFlow? f) => f is not null && f.Stage is not (TicketStage.Fix or TicketStage.Green))
            .AddEdge(review, code, (TicketFlow? f) => f?.Stage == TicketStage.Fix)
            .AddEdge(review, test, (TicketFlow? f) => f?.Stage == TicketStage.Approved)
            .AddEdge(review, done, (TicketFlow? f) => f is not null && f.Stage is not (TicketStage.Fix or TicketStage.Approved))
            .AddEdge(test, code, (TicketFlow? f) => f?.Stage == TicketStage.Fix)
            .AddEdge(test, merge, (TicketFlow? f) => f?.Stage == TicketStage.Accepted)
            .AddEdge(test, done, (TicketFlow? f) => f is not null && f.Stage is not (TicketStage.Fix or TicketStage.Accepted))
            .AddEdge(merge, done)
            .WithOutputFrom(done)
            .Build();
    }

    /// <summary>Run <paramref name="flow"/> to its end; returns the final state.</summary>
    public static async Task<TicketFlow> RunAsync(ITicketSteps steps, TicketFlow flow, TimeSpan checksTimeout, TimeSpan pollInterval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var run = await InProcessExecution.RunAsync(Build(steps, checksTimeout, pollInterval), flow, cancellationToken: ct).ConfigureAwait(false);
        return run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<TicketFlow>().LastOrDefault() ?? flow;
    }

    private sealed class CodeExecutor(ITicketSteps steps) : Executor<TicketFlow, TicketFlow>("code")
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            var run = await steps.CodeAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Coding agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
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

    private sealed class ChecksExecutor(ITicketSteps steps, TimeSpan timeout, TimeSpan poll) : Executor<TicketFlow, TicketFlow>("checks")
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
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

    private sealed class ReviewExecutor(ITicketSteps steps) : Executor<TicketFlow, TicketFlow>("review")
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            var run = await steps.ReviewAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Reviewer agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
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

    private sealed class TestExecutor(ITicketSteps steps) : Executor<TicketFlow, TicketFlow>("test")
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
            var run = await steps.TestAsync(f, ct).ConfigureAwait(false);
            if (run.Status != TaskStart.Started) { return f.Stop($"The Testing agent couldn't start: {run.Status}."); }
            await run.Completion.ConfigureAwait(false);
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

    private sealed class MergeExecutor(ITicketSteps steps) : Executor<TicketFlow, TicketFlow>("merge")
    {
        public override async ValueTask<TicketFlow> HandleAsync(TicketFlow f, IWorkflowContext context, CancellationToken ct)
        {
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
