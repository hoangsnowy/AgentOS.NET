// The per-ticket Microsoft Agent Framework workflow, run for real (InProcessExecution) against scripted steps: every
// route of the graph — green path, red checks, conflicts, a branch behind its base, requested changes, failed
// acceptance, the 3-round cap, an agent that couldn't finish, checks that never finish, a board that leaves the
// merge to a person — plus the round counter seeded from the task's persisted fix rounds (one count across engines and
// restarts) and the member's plan limit pausing the flow before it spends a round.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.BoardTasks;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class TicketWorkflowTests
{
    private sealed class ScriptedSteps : ITicketSteps
    {
        private string _status = "Pending";
        private string? _error;
        private string? _pr;
        public Queue<string> Coding { get; } = new();    // "ok" | "fail"
        public Queue<(PrChecksState, PrMergeState)> Checks { get; } = new();
        public Queue<string> Reviews { get; } = new();   // "Reviewed" | "ChangesRequested"
        public Queue<string> Tests { get; } = new();     // "Accepted" | "Rejected"
        public List<string?> CodeRequests { get; } = [];
        public int Updates { get; private set; }
        public int Merges { get; private set; }
        /// <summary>Set when the member's plan hits its limit (after <see cref="LimitAfterCodeRuns"/> coding runs).</summary>
        public string? Limit { get; set; }
        public int LimitAfterCodeRuns { get; set; } = int.MaxValue;

        public string? PlanLimited(TicketFlow flow) => CodeRequests.Count >= LimitAfterCodeRuns ? Limit : null;

        private static Task<TaskRun> Started() => Task.FromResult(new TaskRun(TaskStart.Started, Task.CompletedTask));

        public Task<TaskRun> CodeAsync(TicketFlow flow, CancellationToken ct)
        {
            CodeRequests.Add(flow.NextChange);
            var outcome = Coding.Count > 0 ? Coding.Dequeue() : "ok";
            if (outcome == "ok") { _status = "Done"; _pr = "https://github.com/o/r/pull/50"; _error = null; }
            else { _status = "Failed"; _pr = null; _error = "push rejected"; }
            return Started();
        }

        public Task<TaskRun> ReviewAsync(TicketFlow flow, CancellationToken ct)
        {
            _status = Reviews.Dequeue();
            _error = _status == "ChangesRequested" ? "- Viewer.tsx:42: off-by-one" : null;
            return Started();
        }

        public Task<TaskRun> TestAsync(TicketFlow flow, CancellationToken ct)
        {
            _status = Tests.Dequeue();
            _error = _status == "Rejected" ? "- LIB-3: ArrowRight does nothing" : null;
            return Started();
        }

        public Task<IReadOnlyList<TicketChange>> ChangesAsync(TicketFlow flow, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketChange>>([new(Guid.Empty, _status, _pr, _error, "main")]);

        public Task<(PrChecksState Checks, PrMergeState Merge)> PrStateAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
            Task.FromResult(Checks.Count > 0 ? Checks.Dequeue() : (PrChecksState.Pending, PrMergeState.Clean));

        public Task<string?> FailedChecksAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
            Task.FromResult<string?>("backend: 2 tests failed");

        public Task UpdateBranchAsync(TicketFlow flow, TicketChange change, CancellationToken ct) { Updates++; return Task.CompletedTask; }

        public Task<string?> MergeAsync(TicketFlow flow, TicketChange change, CancellationToken ct) { Merges++; _status = "Merged"; return Task.FromResult<string?>(null); }
    }

    private static readonly (PrChecksState, PrMergeState) Green = (PrChecksState.Passed, PrMergeState.Clean);

    private static Task<TicketFlow> Run(ScriptedSteps steps, bool mayMerge = true, TimeSpan? checksTimeout = null) =>
        TicketWorkflow.RunAsync(steps, new TicketFlow("t", Guid.NewGuid(), Guid.NewGuid(), 15, mayMerge),
            checksTimeout ?? TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

    [Fact]
    public async Task GreenPath_CodesChecksReviewsTestsAndMerges()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue((PrChecksState.Pending, PrMergeState.Clean));
        steps.Checks.Enqueue(Green);
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Accepted");

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Merged);
        flow.Round.ShouldBe(0);
        flow.Trail.ShouldBe(["coded", "checks green", "review approved", "acceptance passed", "merged"]);
        steps.Merges.ShouldBe(1);
    }

    [Fact]
    public async Task RedChecks_GoBackToCoding_WithTheReport()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue((PrChecksState.Failed, PrMergeState.Clean));
        steps.Checks.Enqueue(Green);
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Accepted");

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Merged);
        flow.Round.ShouldBe(1);
        steps.CodeRequests.Count.ShouldBe(2);
        steps.CodeRequests[1]!.ShouldContain("backend: 2 tests failed");
    }

    [Fact]
    public async Task Conflicts_GoBackToCoding_ToMergeTheBase()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue((PrChecksState.Passed, PrMergeState.Conflicts));
        steps.Checks.Enqueue(Green);
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Accepted");

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Merged);
        steps.CodeRequests[1]!.ShouldContain("Merge origin/main");
    }

    [Fact]
    public async Task BehindItsBase_IsUpdated_ThenGoesOn()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue((PrChecksState.Pending, PrMergeState.Behind));
        steps.Checks.Enqueue(Green);
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Accepted");

        (await Run(steps)).Stage.ShouldBe(TicketStage.Merged);
        steps.Updates.ShouldBe(1);
    }

    [Fact]
    public async Task RequestedChangesAndFailedAcceptance_GoBackToCoding_WithTheFindings()
    {
        var steps = new ScriptedSteps();
        for (var i = 0; i < 3; i++) { steps.Checks.Enqueue(Green); }
        steps.Reviews.Enqueue("ChangesRequested");
        steps.Reviews.Enqueue("Reviewed");
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Rejected");
        steps.Tests.Enqueue("Accepted");

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Merged);
        flow.Round.ShouldBe(2);
        steps.CodeRequests[1]!.ShouldContain("off-by-one");
        steps.CodeRequests[2]!.ShouldContain("ArrowRight");
    }

    [Fact]
    public async Task ThreeRoundsUsed_NeedsAPerson()
    {
        var steps = new ScriptedSteps();
        for (var i = 0; i < 4; i++) { steps.Checks.Enqueue(Green); steps.Reviews.Enqueue("ChangesRequested"); }

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.NeedsPerson);
        flow.Round.ShouldBe(3);
        flow.Reason!.ShouldContain("needs a person");
        steps.Merges.ShouldBe(0);
    }

    [Fact]
    public async Task CodingCouldNotFinish_StopsWithTheReason()
    {
        var steps = new ScriptedSteps();
        steps.Coding.Enqueue("fail");

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Stopped);
        flow.Reason.ShouldBe("push rejected");
    }

    [Fact]
    public async Task ChecksNeverFinish_StopAfterTheTimeout() =>
        (await Run(new ScriptedSteps(), checksTimeout: TimeSpan.FromMilliseconds(30))).Stage.ShouldBe(TicketStage.Stopped);

    // One counter: a flow started after step-by-step rounds (or a restart) has only what is left of the cap.
    [Fact]
    public async Task SeededFromPersistedFixRounds_GetsOnlyTheRoundsLeft()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue((PrChecksState.Failed, PrMergeState.Clean));
        steps.Checks.Enqueue((PrChecksState.Failed, PrMergeState.Clean));
        var flow = TicketWorkflowRunner.Seed("t", Guid.NewGuid(), Guid.NewGuid(), 15, mayMerge: true, persistedFixRounds: 2, AutopilotLimits.Default);

        var end = await TicketWorkflow.RunAsync(steps, flow, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        end.Stage.ShouldBe(TicketStage.NeedsPerson);
        end.Round.ShouldBe(3);
        steps.CodeRequests.Count.ShouldBe(2); // the ticket itself, then the single round that was left
        end.Reason!.ShouldContain("after 3 automatic fixes");
    }

    [Fact]
    public async Task ConfiguredMaxRounds_IsTheCap()
    {
        var steps = new ScriptedSteps();
        for (var i = 0; i < 3; i++) { steps.Checks.Enqueue((PrChecksState.Failed, PrMergeState.Clean)); }
        var flow = TicketWorkflowRunner.Seed("t", Guid.NewGuid(), Guid.NewGuid(), 15, true, 0, AutopilotLimits.Default with { MaxFixRounds = 1 });

        var end = await TicketWorkflow.RunAsync(steps, flow, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        end.Stage.ShouldBe(TicketStage.NeedsPerson);
        end.Round.ShouldBe(1);
        end.Trail.ShouldContain("checks failed → back to Coding (try 1 of 1)");
    }

    // The member's plan hit its limit while coding: the flow stops there — no review, no test, no fix round spent.
    [Fact]
    public async Task PlanLimit_PausesTheFlow_WithoutSpendingARound()
    {
        var steps = new ScriptedSteps { Limit = "Your computer's Claude plan hit its limit — Autopilot waits until 23:50, nothing is lost.", LimitAfterCodeRuns = 1 };
        steps.Checks.Enqueue(Green);

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.PlanLimited);
        flow.Round.ShouldBe(0);
        flow.Reason!.ShouldContain("waits until 23:50");
        steps.CodeRequests.Count.ShouldBe(1);
        steps.Merges.ShouldBe(0);
    }

    [Fact]
    public async Task PlanLimitBeforeTheFirstStep_StartsNothing()
    {
        var steps = new ScriptedSteps { Limit = "limit", LimitAfterCodeRuns = 0 };

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.PlanLimited);
        steps.CodeRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task BoardWithoutAutoMerge_EndsReadyForAPerson()
    {
        var steps = new ScriptedSteps();
        steps.Checks.Enqueue(Green);
        steps.Reviews.Enqueue("Reviewed");
        steps.Tests.Enqueue("Accepted");

        var flow = await Run(steps, mayMerge: false);

        flow.Stage.ShouldBe(TicketStage.ReadyToMerge);
        steps.Merges.ShouldBe(0);
    }
}
