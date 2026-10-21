// The project orchestrator's rules: build order, the parallel cap, bounded CI self-repair, merge only on green and
// only when the board allows it, and a person asked when the agent can't get there.

using AgentOs.Domain.Workspaces;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class AutopilotPlannerTests
{
    private static PlanTicket T(int n, bool open = true, bool ai = true, int p = 1, int[]? deps = null, bool live = false) =>
        new(n, open, ai, p, deps ?? [], live);

    private static PlanTask Task(int issue, string status, params PlanChange[] changes) =>
        new(Guid.NewGuid(), issue, status, changes, 0);

    private static PlanChange Pr(PrChecksState checks, int number = 30, string? report = "backend failed") =>
        new(Guid.NewGuid(), number, checks, checks == PrChecksState.Failed ? report : null);

    [Fact]
    public void Off_DoesNothing() =>
        AutopilotPlanner.Decide(AutopilotMode.Off, [T(1)], [Task(2, "Done", Pr(PrChecksState.Failed))]).ShouldBeEmpty();

    [Fact]
    public void StartsOnlyTicketsWhoseDependenciesAreClosed_P0First_TwoAtATime()
    {
        PlanTicket[] tickets = [T(8, open: false), T(11, p: 0, deps: [8]), T(12, deps: [11]), T(13, p: 1), T(14, p: 2), T(15, ai: false)];

        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, tickets, []);

        actions.OfType<AutopilotAction.StartTicket>().Select(a => a.Number).ShouldBe([11, 13]);
        actions[0].Why.ShouldBe("Started #11 — #8 is done.");
    }

    [Fact]
    public void RunningTasksUseUpTheSlots()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, [T(1), T(2)], [Task(9, "Running"), Task(10, "Running")]);

        actions.ShouldBeEmpty();
    }

    [Fact]
    public void TicketWithALiveTask_IsNotStartedAgain() =>
        AutopilotPlanner.Decide(AutopilotMode.Build, [T(1, live: true)], []).ShouldBeEmpty();

    [Fact]
    public void FailedChecks_AreSentBackToTheAgent_WithTheReport_AndTheRound()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(9, "Done", Pr(PrChecksState.Failed, report: "XQF parity: 2 failed"))]);

        var fix = actions.ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.FixChecks>();
        fix.Report.ShouldBe("XQF parity: 2 failed");
        fix.Round.ShouldBe(1);
        fix.Why.ShouldBe("#9's checks failed — asked the AI to fix them (try 1 of 3).");
    }

    [Fact]
    public void FailedChecks_AfterThreeFixes_AskAPerson()
    {
        var task = Task(9, "Done", Pr(PrChecksState.Failed)) with { FixRounds = AutopilotPlanner.MaxFixRounds };

        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [task]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
    }

    [Fact]
    public void GreenChecks_MergeOnlyWhenTheBoardAllowsIt()
    {
        var green = Task(9, "Done", Pr(PrChecksState.Passed, number: 23));

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [green]).ShouldBeEmpty();
        var merge = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [green]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>();
        merge.PrNumber.ShouldBe(23);
    }

    [Theory]
    [InlineData(PrChecksState.Pending)]
    [InlineData(PrChecksState.None)]
    public void ChecksNotFinished_NeverMerge(PrChecksState state) =>
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Task(9, "Done", Pr(state))]).ShouldBeEmpty();

    [Fact]
    public void AFixInProgressTakesASlot()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, [T(1), T(2)], [Task(9, "Running"), Task(10, "Done", Pr(PrChecksState.Failed))]);

        actions.Select(a => a.GetType().Name).ShouldBe(["FixChecks"]);
    }
}
