// Agentic Testing: the verdict is evidence, not a claim — a "pass" with a failing criterion is a fail, and a reply
// without a verdict means "not tested", never "passed".

using AgentOs.Domain.Sessions;
using AgentOs.Modules.Pipeline.Agents;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Pipeline;

public sealed class AcceptanceAgentTests
{
    [Fact]
    public void Parse_Pass_WithEvidence()
    {
        var v = AcceptanceAgent.Parse("""Done. {"verdict":"pass","criteria":[{"criterion":"F-4","result":"pass","evidence":"npx playwright test e2e/landing.spec.ts: 1 passed"}],"summary":"All good."}""");

        v.Ran.ShouldBeTrue();
        v.Passed.ShouldBeTrue();
        v.Criteria.ShouldHaveSingleItem().Evidence.ShouldContain("1 passed");
    }

    [Fact]
    public void Parse_ClaimedPassWithAFailingCriterion_IsAFail()
    {
        var v = AcceptanceAgent.Parse("""{"verdict":"pass","criteria":[{"criterion":"LIB-7","result":"fail","evidence":"p95 412 ms"}],"summary":"Fast enough."}""");

        v.Passed.ShouldBeFalse();
        v.Failures.ShouldHaveSingleItem().Criterion.ShouldBe("LIB-7");
    }

    [Fact]
    public void Parse_UnknownResult_IsNotChecked_NeverPass()
    {
        var v = AcceptanceAgent.Parse("""{"verdict":"pass","criteria":[{"criterion":"LIB-1","result":"probably","evidence":""}],"summary":""}""");

        v.Criteria.ShouldHaveSingleItem().Result.ShouldBe("not-checked");
    }

    [Theory]
    [InlineData("I could not finish.")]
    [InlineData("{not json")]
    public void Parse_NoVerdict_IsNotRun(string reply) => AcceptanceAgent.Parse(reply).Ran.ShouldBeFalse();

    [Fact]
    public void Comment_ShowsEachCriterionWithItsEvidence_AndEscapesTables()
    {
        var v = new AcceptanceVerdict(true, false,
            [new AcceptanceCriterion("F-4", "fail", "409 expected | got 201"), new AcceptanceCriterion("LIB-1", "not-checked", "CHESS_LIBRARY_PATH not set")],
            "One failure.", null);

        var md = AcceptanceAgent.Comment(4, v);

        md.ShouldStartWith("### ❌ AgentOS.NET acceptance — failed (#4)");
        md.ShouldContain("| F-4 | ❌ fail | 409 expected \\| got 201 |");
        md.ShouldContain("➖ not checked");
    }

    [Fact]
    public void Findings_ListTheFailures_ForTheCodingAgent()
    {
        var v = new AcceptanceVerdict(true, false, [new AcceptanceCriterion("F-4", "fail", "duplicate email accepted")], "One failure.", null);

        AcceptanceAgent.Findings(v).ShouldContain("- F-4: duplicate email accepted");
    }

    [Fact]
    public void Instructions_AreReadOnly_AndCheckOutThePullRequestBranch()
    {
        var r = new AcceptanceRequest(Guid.NewGuid(), "t", "m", new WorkRepo(Guid.NewGuid(), "o", "r", "main", "issue-4-ai-fix"), 4, "Landing", "body");

        var s = AcceptanceAgent.Instructions(r);

        s.ShouldContain("git checkout issue-4-ai-fix");
        s.ShouldContain("NEVER change");
        s.ShouldContain("Never report \"pass\" for something neither you nor CI ran");
    }
}

public sealed class AcceptanceBaseFailureTests
{
    // Seen on VNChessAcademy#25: a corpus test failing identically on main bounced an unrelated change.
    [Fact]
    public void Parse_BaseFailures_DoNotFailTheChange_AndAreReported()
    {
        var v = AcceptanceAgent.Parse("""{"verdict":"pass","criteria":[{"criterion":"LIB-1","result":"pass","evidence":"ok"}],"baseFailures":[{"test":"TeacherHub.Tests.Formats.AcademyCorpusTests.CrossCheck","evidence":"CommentsLossy 29589 ≠ 31000, same on main"}],"summary":"Passes; main has one failing corpus test."}""");

        v.Passed.ShouldBeTrue();
        v.BaseFailures.ShouldHaveSingleItem().Criterion.ShouldEndWith("CrossCheck");

        var md = AcceptanceAgent.Comment(11, v);
        md.ShouldContain("Already failing on the base branch");
        md.ShouldContain("`TeacherHub.Tests.Formats.AcademyCorpusTests.CrossCheck`");
    }

    [Fact]
    public void Instructions_AskToCompareAFailingTestWithTheBaseBranch()
    {
        var r = new AcceptanceRequest(Guid.NewGuid(), "t", "m", new WorkRepo(Guid.NewGuid(), "o", "r", "main", "issue-11-ai-fix"), 11, "t", "b");

        // In the run's temp folder — the write guard refuses ../ next to the checkout (VNChess #124's run hit that).
        var text = AcceptanceAgent.Instructions(r);
        text.ShouldContain("git worktree add \"$AGENTOS_RUN_TMP/base\" origin/main");
        text.ShouldNotContain("git worktree add ../base");
    }

    [Fact]
    public void Instructions_LetCiProveWhatThisComputerCannotRun()
    {
        var r = new AcceptanceRequest(Guid.NewGuid(), "t", "m", new WorkRepo(Guid.NewGuid(), "o", "r", "main", "issue-129-ai-fix"), 129, "t", "b");

        var text = AcceptanceAgent.Instructions(r);

        // Docker / scripts / external services: a passing CI job whose steps exercise the criterion is evidence (VNChess
        // #129 reported its compose criteria "not-checked" although the PR's deploy smoke job ran them) — cited, never assumed.
        text.ShouldContain("gh pr checks issue-129-ai-fix");
        text.ShouldContain("whose steps really exercise that criterion PASSED");
        text.ShouldContain("Never report \"pass\" for something neither you nor CI ran.");
    }
}
