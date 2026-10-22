// The Reviewer agent's step: green checks → review → (changes go back to the Coding agent, bounded) → testing → merge.
// Plus its verdict parsing and the pull-request comment.

using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class ReviewStepTests
{
    private static PlanTask Task(int issue, int rounds, params PlanChange[] changes) => new(Guid.NewGuid(), issue, "Done", changes, rounds);

    private static PlanChange Change(ChangeReview review, ChangeTesting testing = ChangeTesting.Untested, string? findings = null) =>
        new(Guid.NewGuid(), 30, PrChecksState.Passed, null, testing, findings, PrMergeState.Clean, "main", review);

    [Fact]
    public void GreenUnreviewedChange_IsReviewedBeforeItIsTested()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Task(15, 0, Change(ChangeReview.NotReviewed))]);

        var review = actions.ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RunReview>();
        review.Why.ShouldBe("Reviewing #15's change — its checks passed.");
    }

    [Fact]
    public void ReviewNeedsTheMembersComputer() =>
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(15, 0, Change(ChangeReview.NotReviewed))], canTest: false).ShouldBeEmpty();

    [Fact]
    public void WhileReviewing_NothingElseHappensToTheTask() =>
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Task(15, 0, Change(ChangeReview.Reviewing))]).ShouldBeEmpty();

    [Fact]
    public void ChangesRequested_GoBackToTheCodingAgent_WithTheFindings()
    {
        var actions = AutopilotPlanner.Decide(AutopilotMode.Build, [],
            [Task(15, 0, Change(ChangeReview.ChangesRequested, findings: "- Viewer.tsx:42: off-by-one on the last move"))]);

        var fix = actions.ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.FixChecks>();
        fix.Report.ShouldContain("off-by-one");
        fix.Why.ShouldBe("Code review asked for changes on #15 — sent back to the AI (try 1 of 3).");
    }

    [Fact]
    public void ChangesRequested_AfterThreeRounds_NeedsAPerson() =>
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(15, 3, Change(ChangeReview.ChangesRequested, findings: "x"))])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.NeedsPerson>();

    [Fact]
    public void ApprovedChange_IsTestedNext() =>
        AutopilotPlanner.Decide(AutopilotMode.Build, [], [Task(15, 0, Change(ChangeReview.Approved))])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.RunTests>();

    [Fact]
    public void ApprovedAndAccepted_IsMerged() =>
        AutopilotPlanner.Decide(AutopilotMode.BuildAndMerge, [], [Task(15, 0, Change(ChangeReview.Approved, ChangeTesting.Accepted))])
            .ShouldHaveSingleItem().ShouldBeOfType<AutopilotAction.Merge>();

    // ── Verdict ──

    [Fact]
    public void Parse_ApproveWithOnlyNits_IsApproved()
    {
        var v = ReviewAgent.Parse("""noise {"verdict":"approve","findings":[{"severity":"nit","file":"a.ts","line":3,"comment":"rename x"}],"summary":"Looks right."}""");
        v.Ran.ShouldBeTrue();
        v.Approved.ShouldBeTrue();
        v.Findings.ShouldHaveSingleItem().Line.ShouldBe(3);
    }

    [Fact]
    public void Parse_ApproveClaimWithAMustFix_IsNotTrusted()
    {
        var v = ReviewAgent.Parse("""{"verdict":"approve","findings":[{"severity":"Must-Fix","file":"Api.cs","comment":"reads another school's rows"}],"summary":"ok"}""");
        v.Approved.ShouldBeFalse();
        v.Blocking.ShouldHaveSingleItem().Comment.ShouldContain("another school");
    }

    [Fact]
    public void Parse_NoJson_DidNotRun() => ReviewAgent.Parse("I could not finish").Ran.ShouldBeFalse();

    [Fact]
    public void ChangeRequest_ListsOnlyMustFixFindings()
    {
        var v = new ReviewVerdict(true, false,
            [new ReviewFinding("must-fix", "Api.cs", 12, "missing authorization"), new ReviewFinding("nit", "a.ts", null, "style")],
            "One defect.", null);

        var text = ReviewAgent.ChangeRequest(v);

        text.ShouldContain("Api.cs:12: missing authorization");
        text.ShouldNotContain("style");
    }

    [Fact]
    public void Comment_ShowsVerdictAndEscapesTableCells()
    {
        var v = new ReviewVerdict(true, false, [new ReviewFinding("must-fix", "a|b.cs", 1, "x | y")], "s", null);

        var md = ReviewAgent.Comment(15, v);

        md.ShouldStartWith("### 🔁 AgentOS.NET review — changes requested (#15)");
        md.ShouldContain(@"a\|b.cs:1");
        md.ShouldContain(@"x \| y");
    }
}
