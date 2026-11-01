// The Design gate in the ticket flow (ADR-0016): a step kind between Test and Merge, off in the built-in flow and limited
// to area:ui tickets once on. The validator keeps every existing invariant (Checks before Merge, Review or Test for every
// ticket) and adds one — a ticket that runs Design runs Test (the Testing agent takes its screenshots). The step-by-step
// rules (AutopilotPlanner) run the Designer, send its changes back to Coding on the persisted fix rounds, wait for a
// person, and merge only after a pass or an owner approval. A flow saved before the kind existed shows it, off.

using System.Linq;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class DesignGateFlowTests
{
    private static TicketFlowDefinition Default => TicketFlowDefinition.Default;

    private static TicketFlowDefinition DesignOn(params string[] only) =>
        Default.With(Default.Find("design")! with { Enabled = true, OnlyLabels = only.Length == 0 ? [TicketFlowDefinition.UiLabel] : only });

    private static string Reasons(TicketFlowDefinition d) => string.Join(" | ", TicketFlowValidator.Validate(d).Select(p => p.Message));

    // ── The step kind ──

    [Fact]
    public void Design_IsRegistered_BetweenTestAndMerge_RunByTheDesigner_AndNotAMergeGate()
    {
        var info = TicketStepKinds.Of(TicketStepKind.Design);
        info.Rank.ShouldBeGreaterThan(TicketStepKinds.Of(TicketStepKind.Test).Rank);
        info.Rank.ShouldBeLessThan(TicketStepKinds.Of(TicketStepKind.Merge).Rank);
        info.DefaultRole.ShouldBe(AgentRoles.Designer);
        info.CanSendBack.ShouldBeTrue();
        info.IsMergeGate.ShouldBeFalse();     // it adds to Review / Test, never replaces them
        info.Required.ShouldBeFalse();
        TicketStepKinds.EitherGate().ShouldBe("Review or Test");
    }

    [Fact]
    public void DefaultFlow_DesignIsOff_SoNoTicketRunsIt()
    {
        Default.Resolve([TicketFlowDefinition.UiLabel]).Runs(TicketStepKind.Design).ShouldBeFalse();
        Default.Resolve([]).Runs(TicketStepKind.Design).ShouldBeFalse();
        Default.Summary().ShouldBe("Code → Checks → Review → Test → Merge");
    }

    [Fact]
    public void DesignOn_RunsForUiTicketsOnly_AndIsValid()
    {
        var d = DesignOn();

        TicketFlowValidator.Validate(d).ShouldBeEmpty();
        d.Resolve(["area:ui"]).Summary.ShouldBe("Code → Checks → Review → Test → Design → Merge");
        d.Resolve(["AREA:UI", "type:feature"]).Runs(TicketStepKind.Design).ShouldBeTrue();
        d.Resolve(["area:api"]).Summary.ShouldBe("Code → Checks → Review → Test → Merge");
        d.Summary().ShouldBe("Code → Checks → Review → Test → Design (only area:ui) → Merge");
        d.Resolve(["area:ui"]).SafetyProblem().ShouldBeNull();
    }

    [Fact]
    public void DesignOn_ForEveryTicket_IsValidToo()
    {
        var d = Default.With(Default.Find("design")! with { Enabled = true, OnlyLabels = null });
        TicketFlowValidator.Validate(d).ShouldBeEmpty();
        d.Resolve([]).Runs(TicketStepKind.Design).ShouldBeTrue();
    }

    // ── Existing invariants still hold with Design on ──

    [Fact]
    public void DesignDoesNotCount_AsAMergeGate()
    {
        // Review and Test both off: Design alone doesn't let a ticket merge.
        var d = DesignOn() with { };
        d = d.With(d.Find("review")! with { Enabled = false }).With(d.Find("test")! with { Enabled = false });
        Reasons(d).ShouldContain("Turn on Review or Test");
    }

    [Fact]
    public void DesignOn_ChecksStillRequired()
    {
        var d = TicketFlowDefinition.Chain(DesignOn().Nodes.Where(n => n.Kind != TicketStepKind.Checks).ToList());
        Reasons(d).ShouldContain("Checks must run before Merge");
    }

    // ── The new invariant: Design needs Test ──

    [Fact]
    public void DesignOn_TestOff_IsRefused()
    {
        var d = DesignOn().With(Default.Find("test")! with { Enabled = false });
        Reasons(d).ShouldContain("Design checks the Testing agent's screenshots — turn Test on, or Design off.");
    }

    [Fact]
    public void DesignOn_TestSkippedForUiTickets_IsRefused_WithTheTicketThatBreaksIt()
    {
        var d = DesignOn().With(Default.Find("test")! with { SkipLabels = [TicketFlowDefinition.UiLabel] });
        Reasons(d).ShouldContain("A ticket labelled area:ui would run Design without Test to take its screenshots.");
    }

    [Fact]
    public void DesignOn_TestOnlyForOtherTickets_IsRefused()
    {
        var d = DesignOn().With(Default.Find("test")! with { OnlyLabels = ["type:feature"] });
        Reasons(d).ShouldContain("would run Design without Test");
    }

    [Fact]
    public void DesignOn_TestSkippedForDocs_IsFine_WhenDesignNeverRunsForThem()
    {
        var d = DesignOn().With(Default.Find("test")! with { SkipLabels = ["type:docs"] })
            .With(DesignOn().Find("design")! with { SkipLabels = ["type:docs"] });
        TicketFlowValidator.Validate(d).ShouldBeEmpty();
    }

    [Fact]
    public void Design_PlacedBeforeTest_IsRefused()
    {
        var n = DesignOn();
        var d = TicketFlowDefinition.Chain([n.Find("code")!, n.Find("checks")!, n.Find("review")!, n.Find("design")!, n.Find("test")!, n.Find("merge")!]);
        Reasons(d).ShouldContain("Test must come before Design");
    }

    [Fact]
    public void Design_RunByTheWrongAgent_IsRefused()
    {
        Reasons(DesignOn().With(DesignOn().Find("design")! with { Role = AgentRoles.Reviewer })).ShouldContain("Design can't be run by Reviewer — pick Designer");
    }

    [Fact]
    public void Plan_DesignWithoutTest_IsUnsafe()
    {
        var d = DesignOn();
        new TicketFlowPlan([d.Find("code")!, d.Find("checks")!, d.Find("review")!, d.Find("design")!, d.Find("merge")!], []).SafetyProblem()!
            .ShouldContain("Design would run without Test");
    }

    // ── A flow saved before Design existed ──

    [Fact]
    public void WithKnownSteps_AddsDesign_Off_ToAnOldFlow_AndKeepsItsSettings()
    {
        var old = TicketFlowDefinition.Chain(Default.Nodes.Where(n => n.Kind != TicketStepKind.Design)
            .Select(n => n.Kind == TicketStepKind.Review ? n with { SkipLabels = ["type:chore"] } : n).ToList());
        TicketFlowValidator.Validate(old).ShouldBeEmpty();
        var json = old.ToJson();
        json.ShouldNotContain("Design");

        var upgraded = TicketFlowDefinition.FromJson(json)!.WithKnownSteps();

        upgraded.Ordered().Select(n => n.Id).ShouldBe(["code", "checks", "review", "test", "design", "merge"]);
        upgraded.Find("design")!.Enabled.ShouldBeFalse();
        upgraded.Find("review")!.Skip.ShouldBe(["type:chore"]);
        TicketFlowValidator.Validate(upgraded).ShouldBeEmpty();
        upgraded.Summary().ShouldBe(old.Summary());       // nothing a ticket runs changed
        Default.WithKnownSteps().ToJson().ShouldBe(Default.ToJson());
    }

    [Fact]
    public void WithKnownSteps_LeavesABrokenFlowAlone()
    {
        var broken = TicketFlowDefinition.Chain(Default.Nodes.Where(n => n.Kind is not (TicketStepKind.Checks or TicketStepKind.Design)).ToList());
        broken.WithKnownSteps().ShouldBeSameAs(broken);
    }

    // ── The step-by-step rules ──

    private static PlanTask Accepted(int issue, DesignGateState design, int fixRounds = 0, string? findings = null) =>
        new PlanTask(System.Guid.NewGuid(), issue, "Done",
            [new PlanChange(System.Guid.NewGuid(), issue + 100, PrChecksState.Passed, null, ChangeTesting.Accepted)], fixRounds)
        {
            Flow = DesignOn().Resolve(["area:ui"]),
            Design = design,
            DesignFindings = findings,
        };

    [Fact]
    public void Planner_TestedUiChange_IsCheckedByTheDesigner_NotMerged()
    {
        var action = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, DesignGateState.NotChecked)]).ShouldHaveSingleItem();
        action.ShouldBeOfType<AutopilotAction.RunDesign>().IssueNumber.ShouldBe(7);
        AutopilotPlanner.TakesSlot(action).ShouldBeTrue();
    }

    [Fact]
    public void Planner_NoComputer_NoDesignCheck()
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, DesignGateState.NotChecked)], canTest: false).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DesignGateState.Checking)]
    [InlineData(DesignGateState.NeedsPerson)]
    public void Planner_WhileCheckingOrWaitingForAPerson_NothingMerges(DesignGateState state)
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, state)]).ShouldBeEmpty();
    }

    [Fact]
    public void Planner_Passed_Merges_AndSaysTheDesignWasApproved()
    {
        var merge = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, DesignGateState.Passed)])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>();
        merge.Why.ShouldContain("its design approved");
    }

    [Fact]
    public void Planner_Passed_ButBuildOnly_LeavesTheMergeToAPerson()
    {
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Accepted(7, DesignGateState.Passed)]).ShouldBeEmpty();
    }

    [Fact]
    public void Planner_ChangesRequested_GoBackToCoding_WithTheFindings_OnTheFixRounds()
    {
        var fix = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, DesignGateState.ChangesRequested, 1, "- Checkout: unreadable button")])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.FixChecks>();
        fix.Report.ShouldContain("unreadable button");
        fix.Round.ShouldBe(2);
        fix.Why.ShouldContain("Design review asked for changes on #7");
        fix.Why.ShouldContain("try 2 of 3");
    }

    [Fact]
    public void Planner_ChangesRequested_OutOfRounds_NeedsAPerson()
    {
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Accepted(7, DesignGateState.ChangesRequested, 3, "x")])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>().Reason.ShouldContain("Design review still asks for changes");
    }

    [Fact]
    public void Planner_NonUiTicket_IgnoresTheDesignGate_AndMerges()
    {
        var api = Accepted(8, DesignGateState.NotChecked) with { Flow = DesignOn().Resolve(["area:api"]) };
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [api]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>()
            .Why.ShouldNotContain("design");
    }

    [Fact]
    public void Planner_DesignWaitsForTesting_First()
    {
        var untested = new PlanTask(System.Guid.NewGuid(), 9, "Done", [new PlanChange(System.Guid.NewGuid(), 109, PrChecksState.Passed, null)], 0)
        {
            Flow = DesignOn().Resolve(["area:ui"]),
        };
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [untested]).ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RunTests>();
    }

    [Fact]
    public void WithinCaps_ADesignCheck_TakesASlot()
    {
        AutopilotAction[] picked = [new AutopilotAction.RunDesign(System.Guid.NewGuid(), 1, "a"), new AutopilotAction.StartTicket(2, "b")];
        AutopilotPlanner.WithinCaps(picked, 0, 0, AutopilotLimits.Default with { MaxParallel = 1 }).ShouldHaveSingleItem()
            .ShouldBeOfType<AutopilotAction.RunDesign>();
    }

    // ── The gate as a review reads it: the owner's decision wins ──

    [Theory]
    [InlineData(DesignReviewStatus.Pending, null, DesignGateState.NotChecked)]
    [InlineData(DesignReviewStatus.Checking, null, DesignGateState.Checking)]
    [InlineData(DesignReviewStatus.Pass, null, DesignGateState.Passed)]
    [InlineData(DesignReviewStatus.Changes, null, DesignGateState.ChangesRequested)]
    [InlineData(DesignReviewStatus.NeedsPerson, null, DesignGateState.NeedsPerson)]
    [InlineData(DesignReviewStatus.NotRun, null, DesignGateState.NeedsPerson)]
    [InlineData(DesignReviewStatus.Changes, DesignDecision.Approved, DesignGateState.Passed)]
    [InlineData(DesignReviewStatus.Pass, DesignDecision.ChangesAsked, DesignGateState.ChangesRequested)]
    [InlineData(DesignReviewStatus.NeedsPerson, DesignDecision.Approved, DesignGateState.Passed)]
    public void Review_Gate_OwnerDecisionOverridesTheAgent(DesignReviewStatus status, DesignDecision? decision, DesignGateState expected)
    {
        var review = DesignReview.New("t", System.Guid.NewGuid(), System.Guid.NewGuid(), 7, 0, status) with { Decision = decision };
        review.Gate.ShouldBe(expected);
    }

    [Fact]
    public void Review_ChangeRequest_CarriesTheOwnersNote_OrTheAgentsFindings()
    {
        var agent = DesignReview.New("t", System.Guid.NewGuid(), System.Guid.NewGuid(), 7, 0, DesignReviewStatus.Changes)
            with { Summary = "Two problems.", Findings = [new DesignFinding("Checkout · phone · dark", "Unreadable button", "Use the accent token")] };
        agent.ChangeRequest().ShouldContain("Checkout · phone · dark: Unreadable button — fix: Use the accent token");
        agent.ChangeRequest().ShouldContain("390 and 1280 px");

        var owner = agent with { Decision = DesignDecision.ChangesAsked, DecisionNote = "Make the header sticky." };
        owner.ChangeRequest().ShouldStartWith("The project owner looked at the screenshots");
        owner.ChangeRequest().ShouldContain("Make the header sticky.");
    }

    [Fact]
    public void Scope_IsPerBoardTicketAndRun()
    {
        var board = System.Guid.Parse("11111111-2222-3333-4444-555555555555");
        DesignScope.For(board, 42, 0).ShouldBe("design:11111111222233334444555555555555:42:0");
        DesignScope.For(board, 42, 1).ShouldNotBe(DesignScope.For(board, 42, 0));
        DesignScope.For(board, 42, 0).Length.ShouldBeLessThanOrEqualTo(AgentOs.Domain.Files.TicketFileScope.MaxLength);
    }
}
