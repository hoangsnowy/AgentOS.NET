// Approval policies (E1.5) in Autopilot: the rules hold a task at every gate the policy requires and FAIL CLOSED — they
// ask an approver (once), wait while asked, go on only once approved (and say who approved), and stop for a person when
// it was rejected or nobody can approve. The per-ticket MAF workflow does the same and hands the task back while it waits.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Domain.Workspaces;
using AgentOs.Web.Services.BoardTasks;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class ApprovalGatesTests
{
    private static ApprovalCheck Check(ApprovalGate gate, ApprovalStatus? status, int? approvers = 1, string by = "alice") =>
        ApprovalRules.Evaluate(new ApprovalPolicy(ApprovalPolicy.FlagOf(gate), false), gate,
            status is { } s
                ? ApprovalRequest.New("t", Guid.NewGuid(), Guid.NewGuid(), 7, gate, 0, "#7", null, "Autopilot", DateTimeOffset.UtcNow) with
                {
                    Status = s,
                    DecidedBy = s == ApprovalStatus.Pending ? null : by,
                    Note = s == ApprovalStatus.Rejected ? "wrong approach" : null,
                }
                : null,
            approvers);

    // A change whose checks are green, review approved and acceptance passed: only gates stand between it and a merge.
    private static PlanTask Ready(int issue = 7) =>
        new(Guid.NewGuid(), issue, "Done",
            [new PlanChange(Guid.NewGuid(), 40, PrChecksState.Passed, null, ChangeTesting.Accepted, Review: ChangeReview.Approved)], 0);

    private static PlanTask Fresh(int issue = 7) =>
        new(Guid.NewGuid(), issue, "Done", [new PlanChange(Guid.NewGuid(), 40, PrChecksState.Passed, null, Review: ChangeReview.NotReviewed)], 0);

    // ── Merge gate ──

    [Fact]
    public void Merge_NotAskedYet_AsksAnApprover_AndDoesNotMerge()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, null) }]);

        var ask = actions.ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RequestApproval>();
        ask.Gate.ShouldBe(ApprovalGate.Merge);
        ask.Why.ShouldContain("waits for an approver to approve the merge");
    }

    [Fact]
    public void Merge_Asked_WaitsQuietly()
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, ApprovalStatus.Pending) }])
            .ShouldBeEmpty();
    }

    [Fact]
    public void Merge_ResumesAfterApproval_AndSaysWhoApproved()
    {
        var merge = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, ApprovalStatus.Approved) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>();
        merge.Why.ShouldContain("the merge approved by alice");
    }

    [Fact]
    public void Merge_Rejected_StopsForAPerson_NeverMerges()
    {
        var stop = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, ApprovalStatus.Rejected) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
        stop.Reason.ShouldBe("The merge was rejected by alice: wrong approach");
        stop.FromApproval.ShouldBeTrue();
    }

    [Fact]
    public void Merge_NobodyCanApprove_FailsClosed()
    {
        var stop = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, null, approvers: 0) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
        stop.Reason.ShouldContain("nobody in this workspace holds the approver or admin role");
        stop.FromApproval.ShouldBeTrue();
    }

    [Fact]
    public void Merge_GateIsNotAskedInBuildMode_APersonMergesAnyway() =>
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Ready() with { MergeApproval = Check(ApprovalGate.Merge, null) }]).ShouldBeEmpty();

    // ── Diff gate ──

    [Fact]
    public void Diff_HoldsReviewUntilApproved()
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Fresh() with { DiffApproval = Check(ApprovalGate.Diff, null) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RequestApproval>().Gate.ShouldBe(ApprovalGate.Diff);
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Fresh() with { DiffApproval = Check(ApprovalGate.Diff, ApprovalStatus.Pending) }])
            .ShouldBeEmpty();
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Fresh() with { DiffApproval = Check(ApprovalGate.Diff, ApprovalStatus.Approved) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RunReview>();
    }

    [Fact]
    public void Diff_IsAskedOnlyOnceChecksAreGreen()
    {
        var running = Fresh() with { DiffApproval = Check(ApprovalGate.Diff, null) };
        running = running with { OpenChanges = [running.OpenChanges[0] with { Checks = PrChecksState.Pending }] };

        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [running]).ShouldBeEmpty();
    }

    // ── Plan gate ──

    [Fact]
    public void Plan_AHeldDraftTask_StartsOnlyOnceApproved()
    {
        var held = new PlanTask(Guid.NewGuid(), 9, "Draft", [], 0) { PlanApproval = Check(ApprovalGate.Plan, ApprovalStatus.Pending) };

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [held]).ShouldBeEmpty();
        var start = AutopilotPlanner.Decide(AutopilotMode.Build, [], [held with { PlanApproval = Check(ApprovalGate.Plan, ApprovalStatus.Approved) }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.ResumeTask>();
        start.SessionId.ShouldBe(held.SessionId);
        start.Why.ShouldContain("the plan approved by alice");
    }

    [Fact]
    public void Plan_Rejected_BlocksTheWholeTask()
    {
        var held = new PlanTask(Guid.NewGuid(), 9, "Draft", [], 0) { PlanApproval = Check(ApprovalGate.Plan, ApprovalStatus.Rejected) };

        var stop = AutopilotPlanner.Decide(AutopilotMode.Build, [], [held]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
        stop.BlockAll.ShouldBeTrue();
        stop.FromApproval.ShouldBeTrue();
    }

    [Fact]
    public void Plan_ApprovedStartsRespectTheSlotCap()
    {
        PlanTask Approved() => new(Guid.NewGuid(), 9, "Draft", [], 0) { PlanApproval = Check(ApprovalGate.Plan, ApprovalStatus.Approved) };

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Approved(), Approved(), Approved()]).Count.ShouldBe(2);
    }

    [Fact]
    public void ApprovalMoves_SurviveTheCaps_AndTakeNoSlot()
    {
        AutopilotAction ask = new AutopilotAction.RequestApproval(Guid.NewGuid(), 1, ApprovalGate.Merge, "ask");

        AutopilotPlanner.TakesSlot(ask).ShouldBeFalse();
        AutopilotPlanner.WithinCaps([ask], running: 99, testing: 99).ShouldHaveSingleItem();
    }

    // ── The per-ticket MAF workflow ──

    private sealed class GatedSteps(Dictionary<ApprovalGate, ApprovalCheck> gates) : ITicketSteps
    {
        private string _status = "Pending";
        public int Reviews { get; private set; }
        public int Merges { get; private set; }
        public List<ApprovalGate> Asked { get; } = [];

        private static Task<TaskRun> Started() => Task.FromResult(new TaskRun(TaskStart.Started, Task.CompletedTask));

        public Task<ApprovalCheck> ApprovalAsync(TicketFlow flow, ApprovalGate gate, CancellationToken ct)
        {
            Asked.Add(gate);
            return Task.FromResult(gates.GetValueOrDefault(gate, ApprovalCheck.Proceed));
        }

        public Task<TaskRun> CodeAsync(TicketFlow flow, CancellationToken ct) { _status = "Done"; return Started(); }
        public Task<TaskRun> ReviewAsync(TicketFlow flow, CancellationToken ct) { Reviews++; _status = "Reviewed"; return Started(); }
        public Task<TaskRun> TestAsync(TicketFlow flow, CancellationToken ct) { _status = "Accepted"; return Started(); }

        public Task<IReadOnlyList<TicketChange>> ChangesAsync(TicketFlow flow, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketChange>>([new(Guid.Empty, _status, "https://github.com/o/r/pull/50", null, "main")]);

        public Task<(PrChecksState Checks, PrMergeState Merge)> PrStateAsync(TicketFlow flow, TicketChange change, CancellationToken ct) =>
            Task.FromResult((PrChecksState.Passed, PrMergeState.Clean));

        public Task<string?> FailedChecksAsync(TicketFlow flow, TicketChange change, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task UpdateBranchAsync(TicketFlow flow, TicketChange change, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> MergeAsync(TicketFlow flow, TicketChange change, CancellationToken ct) { Merges++; return Task.FromResult<string?>(null); }
    }

    private static Task<TicketFlow> Run(GatedSteps steps) =>
        TicketWorkflow.RunAsync(steps, new TicketFlow("t", Guid.NewGuid(), Guid.NewGuid(), 7, mayMerge: true),
            TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

    [Fact]
    public async Task Workflow_DiffWaiting_StopsBeforeReview_AndHandsBack()
    {
        var steps = new GatedSteps(new() { [ApprovalGate.Diff] = Check(ApprovalGate.Diff, ApprovalStatus.Pending) });

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.AwaitingApproval);
        flow.Reason.ShouldNotBeNull().ShouldContain("approve the change");
        steps.Reviews.ShouldBe(0);
        steps.Merges.ShouldBe(0);
    }

    [Fact]
    public async Task Workflow_MergeWaiting_ReviewsAndTests_ButDoesNotMerge()
    {
        var steps = new GatedSteps(new() { [ApprovalGate.Merge] = Check(ApprovalGate.Merge, ApprovalStatus.Pending) });

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.AwaitingApproval);
        steps.Reviews.ShouldBe(1);
        steps.Merges.ShouldBe(0);
        flow.Trail.ShouldContain("waits for approval");
    }

    [Fact]
    public async Task Workflow_Approved_Merges()
    {
        var steps = new GatedSteps(new()
        {
            [ApprovalGate.Diff] = Check(ApprovalGate.Diff, ApprovalStatus.Approved),
            [ApprovalGate.Merge] = Check(ApprovalGate.Merge, ApprovalStatus.Approved),
        });

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.Merged);
        steps.Merges.ShouldBe(1);
        steps.Asked.ShouldContain(ApprovalGate.Merge);
    }

    [Fact]
    public async Task Workflow_NobodyCanApprove_NeedsAPerson_NeverMerges()
    {
        var steps = new GatedSteps(new() { [ApprovalGate.Merge] = Check(ApprovalGate.Merge, null, approvers: 0) });

        var flow = await Run(steps);

        flow.Stage.ShouldBe(TicketStage.NeedsPerson);
        steps.Merges.ShouldBe(0);
    }

    [Fact]
    public async Task Workflow_WithoutAPolicy_IsUnchanged()
    {
        var steps = new GatedSteps([]);

        (await Run(steps)).Stage.ShouldBe(TicketStage.Merged);
    }
}
