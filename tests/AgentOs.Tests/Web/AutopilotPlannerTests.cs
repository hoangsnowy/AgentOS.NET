// The project orchestrator's rules: build order, the parallel cap, bounded CI self-repair, merge only on green and
// only when the board allows it, and a person asked when the agent can't get there.

using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class AutopilotPlannerTests
{
    private static PlanTicket T(int n, bool open = true, bool ai = true, int p = 1, int[]? deps = null, bool live = false) =>
        new(n, open, ai, p, deps ?? [], live);

    // ── Settings › Autopilot bounds ──

    [Fact]
    public void Limits_FromSettings_CapSlots()
    {
        var limits = AutopilotLimits.Default with { MaxParallel = 3 };

        AutopilotPlanner.Decide(AutopilotMode.Build, [T(1), T(2), T(3), T(4)], [], limits: limits).Count.ShouldBe(3);
        AutopilotPlanner.Decide(AutopilotMode.Build, [T(1), T(2), T(3), T(4)], []).Count.ShouldBe(2); // the default
    }

    [Fact]
    public void Limits_FromSettings_CapFixRounds_AndTheLogSaysTheRealNumber()
    {
        var limits = AutopilotLimits.Default with { MaxFixRounds = 1 };
        var red = Task(9, "Done", Pr(PrChecksState.Failed));

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [red], limits: limits).ShouldHaveSingleItem()
            .ShouldBeOfType<AutopilotAction.FixChecks>().Why.ShouldContain("try 1 of 1");
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [red with { FixRounds = 1 }], limits: limits).ShouldHaveSingleItem()
            .ShouldBeOfType<AutopilotAction.NeedsPerson>().Why.ShouldContain("after 1 tries");
    }

    [Fact]
    public void Limits_FromSettings_CapAcceptanceRuns()
    {
        PlanTask Green(int issue) => Task(issue, "Done", new PlanChange(Guid.NewGuid(), issue + 20, PrChecksState.Passed, null));
        var limits = AutopilotLimits.Default with { MaxParallel = 4, MaxParallelTests = 2 };

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Green(1), Green(2), Green(3)], limits: limits)
            .OfType<AutopilotAction.RunTests>().Count().ShouldBe(2);
    }

    [Fact]
    public void WithinCaps_UsesTheConfiguredCaps()
    {
        AutopilotAction[] picked = [new AutopilotAction.StartTicket(1, "a"), new AutopilotAction.StartTicket(2, "b"), new AutopilotAction.StartTicket(3, "c")];

        AutopilotPlanner.WithinCaps(picked, 0, 0, AutopilotLimits.Default with { MaxParallel = 3 }).Count.ShouldBe(3);
        AutopilotPlanner.WithinCaps(picked, 0, 0, AutopilotLimits.Default with { MaxParallel = 1 }).ShouldHaveSingleItem();
    }

    // ── Plan limits ──

    // Work the member's plan limit cut short picks up first once the plan resets — as a plain restart, no fix round.
    [Fact]
    public void CutShortByPlanLimit_ResumesFirst_WithinTheSlots()
    {
        var cut = Task(7, "Failed") with { CutShortByPlanLimit = true, FixRounds = 2 };

        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, [T(1), T(2)], [cut]);

        actions.Count.ShouldBe(2);
        actions[0].ShouldBeOfType<AutopilotAction.ResumeTask>().IssueNumber.ShouldBe(7);
        actions[1].ShouldBeOfType<AutopilotAction.StartTicket>().Number.ShouldBe(1);
        actions.OfType<AutopilotAction.FixChecks>().ShouldBeEmpty();
        AutopilotPlanner.TakesSlot(actions[0]).ShouldBeTrue();
    }

    [Fact]
    public void CutShortByPlanLimit_WaitsForASlot_AndAPlainFailureIsLeftAlone()
    {
        var cut = Task(7, "Failed") with { CutShortByPlanLimit = true };

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(8, "Running"), Task(9, "Running"), cut]).ShouldBeEmpty();
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(7, "Failed")]).ShouldBeEmpty(); // the Orchestrator agent's to retry
        AutopilotPlanner.Decide(AutopilotMode.Off, [], [cut]).ShouldBeEmpty();
    }

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
        var task = Task(9, "Done", Pr(PrChecksState.Failed)) with { FixRounds = AutopilotLimits.Default.MaxFixRounds };

        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [task]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
    }

    [Fact]
    public void GreenChecks_Untested_AreTestedFirst_NeverMergedUntested()
    {
        var green = Task(9, "Done", Pr(PrChecksState.Passed, number: 23));

        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [green]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RunTests>();
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [green], canTest: false).ShouldBeEmpty();
    }

    [Fact]
    public void Accepted_MergesOnlyWhenTheBoardAllowsIt()
    {
        var accepted = Task(9, "Done", Pr(PrChecksState.Passed, number: 23) with { Testing = ChangeTesting.Accepted });

        AutopilotPlanner.Decide(AutopilotMode.Build, [], [accepted]).ShouldBeEmpty();
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [accepted]).ShouldHaveSingleItem()
            .ShouldBeOfType<AutopilotAction.Merge>().PrNumber.ShouldBe(23);
    }

    [Fact]
    public void Rejected_GoesBackToCodingWithTheFindings_ThenAPerson()
    {
        var rejected = Task(9, "Done", Pr(PrChecksState.Passed) with { Testing = ChangeTesting.Rejected, Findings = "- F-4: duplicate email accepted" });

        var fix = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [rejected]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.FixChecks>();
        fix.Report.ShouldBe("- F-4: duplicate email accepted");
        fix.Why.ShouldContain("failed acceptance testing");
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [rejected with { FixRounds = AutopilotLimits.Default.MaxFixRounds }])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();
    }

    [Fact]
    public void WhileTesting_NothingMoves() =>
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [],
            [Task(9, "Done", Pr(PrChecksState.Passed) with { Testing = ChangeTesting.Testing })]).ShouldBeEmpty();

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

public sealed class AutopilotPlannerBranchTests
{
    private static PlanTask Done(params PlanChange[] changes) => new(Guid.NewGuid(), 9, "Done", changes, 0);

    private static PlanChange Accepted(PrMergeState merge) =>
        new(Guid.NewGuid(), 23, PrChecksState.Passed, null, ChangeTesting.Accepted, null, merge, "main");

    [Fact]
    public void Conflicts_GoBackToTheCodingAgent_BeforeAnythingElse()
    {
        var conflicting = new PlanChange(Guid.NewGuid(), 23, PrChecksState.Passed, null, ChangeTesting.Accepted, null, PrMergeState.Conflicts, "main");

        var fix = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Done(conflicting)]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.FixChecks>();

        fix.Report.ShouldContain("Merge origin/main");
        fix.Why.ShouldBe("#9 conflicts with main — asked the AI to bring it up to date (try 1 of 3).");
    }

    [Fact]
    public void Behind_IsUpdatedBeforeMerging()
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Done(Accepted(PrMergeState.Behind))])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.UpdateBranch>();
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Done(Accepted(PrMergeState.Clean))])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>();
    }

    [Theory]
    [InlineData(false, null, PrMergeState.Conflicts)]
    [InlineData(null, Octokit.MergeableState.Dirty, PrMergeState.Conflicts)]
    [InlineData(true, Octokit.MergeableState.Behind, PrMergeState.Behind)]
    [InlineData(true, Octokit.MergeableState.Clean, PrMergeState.Clean)]
    [InlineData(null, null, PrMergeState.Unknown)]
    public void MergeStateOf_GitHubFlags(bool? mergeable, Octokit.MergeableState? state, PrMergeState expected) =>
        AgentOs.Modules.Integration.GitHubPrCreationService.MergeStateOf(mergeable, state).ShouldBe(expected);
}

public sealed class AutopilotPlannerTestingSlotTests
{
    private static PlanTask Green(int issue, ChangeTesting testing = ChangeTesting.Untested) =>
        new(Guid.NewGuid(), issue, testing == ChangeTesting.Testing ? "Running" : "Done",
            [new PlanChange(Guid.NewGuid(), 30 + issue, PrChecksState.Passed, null, testing)], 0);

    // Two acceptance runs on one computer fight over the app's fixed ports — one at a time.
    [Fact]
    public void OneAcceptanceRunAtATime()
    {
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Green(1), Green(2)])
            .OfType<AutopilotAction.RunTests>().ShouldHaveSingleItem().IssueNumber.ShouldBe(1);
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Green(1, ChangeTesting.Testing), Green(2)])
            .OfType<AutopilotAction.RunTests>().ShouldBeEmpty();
    }
}
