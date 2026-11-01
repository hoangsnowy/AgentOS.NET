// A Board agent run that ends without the JSON reply its prompt asks for (the CLI ran out of turns or context after doing
// the work — VNChess #102 lost a 20-minute migration that way) is asked ONCE for the reply instead of discarded. These
// pin the parsing paths (present / absent / recovered / recovery fails), the one-attempt bound, the branch-on-the-remote
// and head-moved checks through IPrCreationService, the follow-up prompt's narrow context (no ticket text, no secrets),
// the setting, the feed event, that the Reviewer and Testing agents recover their verdicts the same way — and what a
// recovered reply may NOT decide: nothing for a run that can't be continued, no approval/pass from a conversation that
// didn't resume the run's own session, no pass for criteria that weren't checked.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Tests.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ReplyRecoveryTests
{
    private const string TicketBody = "Migrate the engine to the new board API. SECRET-TICKET-MARKER.";

    private static IssueWorkRequest Request(FakePrs? prs = null, string? existingBranch = null, string? changeRequest = null) => new(
        SessionId: Guid.NewGuid(),
        TenantId: "tenant-a",
        MemberId: "member-1",
        Repos: [new WorkRepo(Guid.NewGuid(), "acme", "chess", "main", existingBranch)],
        IssueNumber: 102,
        IssueTitle: "Migrate to the new board API",
        IssueBody: TicketBody,
        ProviderOverride: "RemoteAgent",
        ChangeRequest: changeRequest)
    {
        BranchExists = prs is null ? null : (r, branch, ct) => prs.BranchExistsAsync(Repo(r), branch, ct),
        BranchHead = prs is null ? null : (r, branch, ct) => prs.GetBranchHeadAsync(Repo(r), branch, ct),
    };

    private static WorkspaceDescriptor Repo(WorkRepo r) =>
        new(Guid.NewGuid(), "tenant-a", SourceProviderKind.GitHub, r.Owner, r.Repo, null, r.DefaultBranch, "pat");

    // A first run on the member's computer: it can be continued (the runner kept its folder) unless said otherwise.
    private static LlmResponse Reply(string content, string? continuation = "req-1") =>
        AgentTestHelpers.StubResponse(content) with { Continuation = continuation };

    // A follow-up's reply: by default it continued the run's own CLI session.
    private static LlmResponse Answer(string content, bool resumed = true) =>
        AgentTestHelpers.StubResponse(content) with { Resumed = resumed };

    // The Substitute answers each call in turn and records what it was asked.
    private static (ILlmClient Llm, List<LlmRequest> Asked) Llm(params LlmResponse[] replies)
    {
        var asked = new List<LlmRequest>();
        var llm = Substitute.For<ILlmClient>();
        llm.Provider.Returns("RemoteAgent");
        var i = 0;
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            asked.Add(ci.Arg<LlmRequest>());
            return Task.FromResult(replies[Math.Min(i++, replies.Length - 1)]);
        });
        return (llm, asked);
    }

    private static IssueWorkAgent Agent(ILlmClient llm, ISessionRunFeed? feed = null, IAgentProfiles? profiles = null) =>
        new(AgentTestHelpers.FactoryReturning(llm), AgentTestHelpers.OptionsWith(new AgentsOptions()),
            NullLogger<IssueWorkAgent>.Instance, feed, profiles: profiles);

    private const string NoJson = "I migrated the engine, committed it and pushed issue-102-ai-fix. Then I ran out of turns.";
    private const string Recovered = """{"branch":"issue-102-ai-fix","pushed":true,"summary":"Migrated the engine.","description":"## What changed"}""";

    // ── Coding agent ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueWork_JsonPresent_AsksNothingMore()
    {
        var (llm, asked) = Llm(Reply("""{"branch":"issue-102-ai-fix","summary":"Done."}"""));

        var outcome = (await Agent(llm).RunAsync(Request())).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeTrue();
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task IssueWork_NoJson_BranchOnTheRemote_RecoversTheSummary()
    {
        var prs = new FakePrs(exists: true);
        var feed = new CapturingFeed();
        var (llm, asked) = Llm(Reply(NoJson, continuation: "req-1"), Answer(Recovered));

        var outcome = (await Agent(llm, feed).RunAsync(Request(prs))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeTrue();
        outcome.BranchName.ShouldBe("issue-102-ai-fix");
        outcome.Summary.ShouldBe("Migrated the engine.");
        outcome.Description.ShouldBe("## What changed");
        prs.Asked.ShouldBe(["acme/chess:issue-102-ai-fix"]);
        asked.Count.ShouldBe(2);
        var followUp = asked[1];
        followUp.Purpose.ShouldBe(LlmCallPurposes.ReplyRecovery);   // Insights counts it
        followUp.ResumeOf.ShouldBe("req-1");                        // the same run's folder + CLI session
        followUp.Timeout.ShouldBe(TimeSpan.FromMinutes(2));
        followUp.AgentRole.ShouldBe(asked[0].AgentRole);
        followUp.Autonomy.ShouldBe(WorkspaceAutonomy.Scoped);       // it must be able to look at the checkout
        feed.Events.ShouldContain(e => e.Kind == SessionRunEventKind.ReplyRecovered && e.Message.Contains("issue-102-ai-fix"));
    }

    [Fact]
    public async Task IssueWork_RunThatCantBeContinued_ServerProvider_AsksNothing()
    {
        // A server-side provider's run has no folder or session left: a follow-up could only make up an answer.
        var (llm, asked) = Llm(Reply(NoJson, continuation: null), Answer(Recovered));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: true)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldBe("Agent did not return a JSON summary.");
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task IssueWork_RecoveredBranchNotOnTheRemote_IsDoneButNotPushed()
    {
        var (llm, _) = Llm(Reply(NoJson), Answer(Recovered));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: false)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldStartWith("The work was done but not pushed — branch issue-102-ai-fix");
        outcome.Error.ShouldContain("git push -u origin issue-102-ai-fix");
    }

    [Fact]
    public async Task IssueWork_AgentSaysNotPushed_IsDoneButNotPushed_EvenWhenTheRemoteCantBeChecked()
    {
        var (llm, _) = Llm(Reply(NoJson), Answer(Recovered.Replace("\"pushed\":true", "\"pushed\":false", StringComparison.Ordinal)));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: null)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldStartWith("The work was done but not pushed");
    }

    [Fact]
    public async Task IssueWork_RemoteCantBeChecked_ResumedSession_TheAgentsWordStands()
    {
        var (llm, _) = Llm(Reply(NoJson), Answer(Recovered, resumed: true));

        (await Agent(llm).RunAsync(Request(new FakePrs(exists: null)))).Repos.ShouldHaveSingleItem().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task IssueWork_RemoteCantBeChecked_FreshConversation_IsRejected()
    {
        // The follow-up could not continue the run's session (context full, Codex, keep window passed…): it has no memory
        // of the run, and nothing on the remote confirms its summary.
        var (llm, _) = Llm(Reply(NoJson), Answer(Recovered, resumed: false));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: null)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldBe(IssueWorkAgent.Unconfirmed("issue-102-ai-fix"));
    }

    [Fact]
    public async Task IssueWork_FreshConversation_BranchOnTheRemote_Counts()
    {
        var (llm, _) = Llm(Reply(NoJson), Answer(Recovered, resumed: false));

        (await Agent(llm).RunAsync(Request(new FakePrs(exists: true)))).Repos.ShouldHaveSingleItem().Ok.ShouldBeTrue();
    }

    // ── Coding agent: a revision of an existing pull request (its branch is always on the remote) ──

    private const string Revision = """{"branch":"issue-102-ai-fix","pushed":true,"summary":"Fixed the review findings."}""";

    [Fact]
    public async Task IssueWork_RevisionWhoseHeadDidNotMove_IsDoneButNotPushed()
    {
        var prs = new FakePrs(exists: true, heads: ["abc123", "abc123"]);
        var (llm, _) = Llm(Reply(NoJson), Answer(Revision, resumed: true));

        var outcome = (await Agent(llm).RunAsync(Request(prs, existingBranch: "issue-102-ai-fix", changeRequest: "Fix the null check.")))
            .Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldStartWith("The work was done but not pushed — branch issue-102-ai-fix");
        prs.HeadsAsked.ShouldBe(2);   // once before the run, once after the summary
    }

    [Fact]
    public async Task IssueWork_RevisionWhoseHeadMoved_Counts_EvenFromAFreshConversation()
    {
        var prs = new FakePrs(exists: true, heads: ["abc123", "def456"]);
        var (llm, _) = Llm(Reply(NoJson), Answer(Revision, resumed: false));

        var outcome = (await Agent(llm).RunAsync(Request(prs, existingBranch: "issue-102-ai-fix", changeRequest: "Fix the null check.")))
            .Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeTrue();
        outcome.BranchName.ShouldBe("issue-102-ai-fix");
    }

    [Theory]
    [InlineData(true, true)]     // Azure DevOps (head unknown): the resumed session's word stands
    [InlineData(false, false)]   // …but a fresh conversation's doesn't
    public async Task IssueWork_RevisionWhoseHeadIsUnknown_DependsOnTheResumedSession(bool resumed, bool ok)
    {
        var prs = new FakePrs(exists: null, heads: [null, null]);
        var (llm, _) = Llm(Reply(NoJson), Answer(Revision, resumed));

        var outcome = (await Agent(llm).RunAsync(Request(prs, existingBranch: "issue-102-ai-fix", changeRequest: "Fix the null check.")))
            .Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBe(ok);
    }

    [Fact]
    public async Task IssueWork_RevisionThatAnswersFirstTime_NeverReadsTheHeadAgain()
    {
        var prs = new FakePrs(exists: true, heads: ["abc123", "abc123"]);
        var (llm, _) = Llm(Reply(Revision));

        (await Agent(llm).RunAsync(Request(prs, existingBranch: "issue-102-ai-fix", changeRequest: "Fix it.")))
            .Repos.ShouldHaveSingleItem().Ok.ShouldBeTrue();
        prs.HeadsAsked.ShouldBe(1);   // recorded before the run; only a recovered summary compares it
    }

    [Fact]
    public async Task IssueWork_FollowUpAlsoWithoutJson_FailsAfterExactlyOneMoreCall()
    {
        var feed = new CapturingFeed();
        var (llm, asked) = Llm(Reply(NoJson), Answer("Still thinking about it."), Answer(Recovered));

        var outcome = (await Agent(llm, feed).RunAsync(Request())).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldBe(IssueWorkAgent.NoSummaryAfterAskingAgain);
        asked.Count.ShouldBe(2);   // one attempt — the third (good) reply is never asked for
        feed.Events.ShouldContain(e => e.Kind == SessionRunEventKind.ReplyRecovered && e.Message.Contains("still gave no summary"));
    }

    [Fact]
    public async Task IssueWork_FollowUpCallFails_FailsAfterOneAttempt()
    {
        var asked = 0;
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(
            _ => { asked++; return Task.FromResult(Reply(NoJson)); },
            _ => { asked++; throw new LlmException("Remote agent timed out after 120s.", "RemoteAgent"); });

        var outcome = (await Agent(llm).RunAsync(Request())).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        // The follow-up's own failure is kept: a typed signal in it (the member's plan hit its limit) still reaches the
        // Board's plan-limit check.
        outcome.Error.ShouldBe(IssueWorkAgent.NoSummaryAfterAskingAgain + ": Remote agent timed out after 120s.");
        asked.ShouldBe(2);
    }

    [Fact]
    public async Task IssueWork_FollowUpMeetsThePlanLimit_TheBoardStillSeesAPlanLimit()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(Reply(NoJson)),
            _ => throw new LlmException("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", "RemoteAgent"));

        var outcome = (await Agent(llm).RunAsync(Request())).Repos.ShouldHaveSingleItem();

        AgentOs.Modules.RemoteAgent.PlanLimitDetector.IsPlanLimit(outcome.Error).ShouldBeTrue();
    }

    [Fact]
    public async Task IssueWork_RecoverySwitchedOff_KeepsTheOldFailure_AndAsksNothing()
    {
        var (llm, asked) = Llm(Reply(NoJson), Answer(Recovered));

        var outcome = (await Agent(llm, profiles: new Profiles(recover: false)).RunAsync(Request())).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldBe("Agent did not return a JSON summary.");
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task IssueWork_FollowUpPrompt_CarriesOnlyWhatItNeeds()
    {
        var withSecrets = "Pushed with https://x-access-token:ghs_abcdefghijklmnop1234@github.com/acme/chess.git and "
            + "ANTHROPIC key sk-ant-api03-SECRETSECRETSECRET. " + new string('.', 3_000) + " the end, no JSON.";
        var (llm, asked) = Llm(Reply(withSecrets), Answer(Recovered));

        await Agent(llm).RunAsync(Request());

        var followUp = asked[1];
        var prompt = followUp.SystemPrompt + followUp.UserPrompt;
        followUp.UserPrompt.ShouldContain("acme/chess");
        followUp.UserPrompt.ShouldContain("issue-102-ai-fix");
        followUp.UserPrompt.ShouldContain("the end, no JSON.");                 // the tail of what it said
        followUp.SystemPrompt.ShouldStartWith("You are the Coding Agent of AgentOS.NET");
        followUp.SystemPrompt.ShouldContain(UntrustedText.SystemRule);
        prompt.ShouldNotContain("SECRET-TICKET-MARKER");                        // no ticket text again
        prompt.ShouldNotContain(asked[0].SystemPrompt);                         // no original instructions again
        prompt.ShouldNotContain("ghs_abcdefghijklmnop1234");
        prompt.ShouldNotContain("SECRETSECRETSECRET");
        followUp.UserPrompt.Length.ShouldBeLessThan(ReplyRecovery.TailChars + 2_000);
        followUp.Files.ShouldBeNull();
        followUp.Progress.ShouldBeNull();
    }

    [Fact]
    public void FollowUp_KeepsTheScreenshotTarget_SoTheKeptFolderSendsThemBack()
    {
        var target = new RunUploadTarget("design/acme/7/0", RunCollect.Screenshots) { PathPrefix = "repo-a/" };
        var original = new LlmRequest("s", "u", "m") { Autonomy = WorkspaceAutonomy.Scoped, Uploads = target, Files = [] };

        var followUp = ReplyRecovery.FollowUp(original, Reply("…"), "You are the Testing Agent of AgentOS.NET.", "Reply with the verdict.");

        followUp.Uploads.ShouldBeSameAs(target);
        followUp.Files.ShouldBeNull();   // already in the kept folder; never re-sent
    }

    [Fact]
    public void Tail_MasksCredentialsInUrlsAndKeys()
    {
        var tail = ReplyRecovery.Tail("git push https://hoang:ghp_0123456789abcdefABCDEF@github.com/o/r.git — token github_pat_11AAAAAAAA0123456789");

        tail.ShouldNotContain("ghp_0123456789abcdefABCDEF");
        tail.ShouldNotContain("hoang:");
        tail.ShouldNotContain("github_pat_11AAAAAAAA0123456789");
        tail.ShouldContain("https://***@github.com/o/r.git");
    }

    [Fact]
    public void Tail_MasksBeforeItCuts_ASecretAcrossTheCutLeavesNothingBehind()
    {
        // The token starts 10 characters before the tail's cut: cut first, the tail would begin mid-token, without the
        // "ghp_" prefix the mask keys on, and leak the rest.
        const string payload = "Q7w9E2r4T6y8U1i3O5p7A9s2D4f6G8h1J3k5L7z9";
        var text = "pushed with ghp_" + payload + new string(' ', ReplyRecovery.TailChars - payload.Length + 10) + "done";

        var tail = ReplyRecovery.Tail(text);

        tail.ShouldNotContain(payload[10..]);
        tail.ShouldNotContain(payload[^20..]);
    }

    [Theory]
    [InlineData("ghu_abcdefghijklmnop1234", "abcdefghijklmnop1234")]
    [InlineData("ghr_abcdefghijklmnop1234", "efghijklmnop1234")]
    [InlineData("auth eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    [InlineData("curl -H 'Authorization: Basic dXNlcjpwYXNzd29yZA=='", "dXNlcjpwYXNzd29yZA")]
    [InlineData("PAT: abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst", "qrstuvwxyz234567abcdefghij")]
    [InlineData("password: hunter2hunter2", "hunter2hunter2")]
    [InlineData("\"token\": \"s3cr3t-value-123\"", "s3cr3t-value-123")]
    [InlineData("api_key: 'abcd1234efgh'", "abcd1234efgh")]
    public void Tail_MasksMoreCredentialShapes(string text, string secret) =>
        ReplyRecovery.Tail(text).ShouldNotContain(secret);

    [Fact]
    public async Task VerifyRecovered_FailedOutcome_IsLeftAlone_AndTheRemoteIsNotAsked()
    {
        var prs = new FakePrs(exists: true);
        var req = Request(prs);
        var failed = new RepoWorkOutcome(req.Repos[0].SessionRepoId, false, "acme", "chess", "", "", "Build failed.");

        var outcome = await IssueWorkAgent.VerifyRecoveredAsync(failed, null, true, null, req, req.Repos[0], NullLogger.Instance, CancellationToken.None);

        outcome.ShouldBeSameAs(failed);
        prs.Asked.ShouldBeEmpty();
    }

    // ── Reviewer + Testing agents ──────────────────────────────────────────────────────────────

    private static AcceptanceRequest Change(DesignCapture? design = null) =>
        new(Guid.NewGuid(), "tenant-a", "member-1", new WorkRepo(Guid.NewGuid(), "acme", "chess", "main", "issue-102-ai-fix"),
            102, "Migrate to the new board API", TicketBody, "claude") { Design = design };

    private static ReviewAgent Reviewer(ILlmClient llm, ISessionRunFeed? feed = null) =>
        new(AgentTestHelpers.FactoryReturning(llm), NullLogger<ReviewAgent>.Instance, feed);

    private static AcceptanceAgent Tester(ILlmClient llm, IAgentProfiles? profiles = null) =>
        new(AgentTestHelpers.FactoryReturning(llm), NullLogger<AcceptanceAgent>.Instance, profiles: profiles);

    private const string Pass = """{"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":"dotnet test: 212 passed"}],"baseFailures":[],"summary":"Passes."}""";

    [Fact]
    public async Task Review_NoVerdict_AsksOnce_AndUsesTheRecoveredVerdict()
    {
        var feed = new CapturingFeed();
        var (llm, asked) = Llm(Reply("I read the diff and the tests.", "req-7"),
            Answer("""{"verdict":"approve","findings":[],"summary":"Looks right."}"""));

        var verdict = await Reviewer(llm, feed).RunAsync(Change());

        verdict.Ran.ShouldBeTrue();
        verdict.Approved.ShouldBeTrue();
        asked.Count.ShouldBe(2);
        asked[1].Purpose.ShouldBe(LlmCallPurposes.ReplyRecovery);
        asked[1].ResumeOf.ShouldBe("req-7");
        asked[1].SystemPrompt.ShouldStartWith("You are the Reviewer Agent of AgentOS.NET");
        asked[1].UserPrompt.ShouldContain("issue-102-ai-fix");
        (asked[1].SystemPrompt + asked[1].UserPrompt).ShouldNotContain("SECRET-TICKET-MARKER");
        feed.Events.ShouldContain(e => e.Kind == SessionRunEventKind.ReplyRecovered && e.Message.Contains("approved"));
    }

    [Fact]
    public async Task Review_ApprovalFromAFreshConversation_IsSetAside_NotApproved()
    {
        var feed = new CapturingFeed();
        var (llm, _) = Llm(Reply("Reading…"), Answer("""{"verdict":"approve","findings":[],"summary":"Looks right."}""", resumed: false));

        var verdict = await Reviewer(llm, feed).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        verdict.Approved.ShouldBeFalse();
        verdict.Error.ShouldNotBeNull().ShouldContain(ReplyRecovery.NotResumedNote);
        feed.Events.ShouldContain(e => e.Kind == SessionRunEventKind.ReplyRecovered && e.Message.Contains("set aside"));
    }

    [Fact]
    public async Task Review_ChangesFromAFreshConversation_Stand()
    {
        var (llm, _) = Llm(Reply("Reading…"),
            Answer("""{"verdict":"changes","findings":[{"severity":"must-fix","file":"a.cs","line":3,"comment":"Null."}],"summary":"One defect."}""", resumed: false));

        var verdict = await Reviewer(llm).RunAsync(Change());

        verdict.Ran.ShouldBeTrue();
        verdict.Approved.ShouldBeFalse();
        verdict.Findings.ShouldHaveSingleItem().Comment.ShouldBe("Null.");
    }

    [Fact]
    public async Task Review_RunThatCantBeContinued_AsksNothing()
    {
        var (llm, asked) = Llm(Reply("Reading…", continuation: null), Answer("""{"verdict":"approve","findings":[],"summary":""}"""));

        (await Reviewer(llm).RunAsync(Change())).Ran.ShouldBeFalse();
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Review_FollowUpCouldNotFinish_StaysNotRun_NeverChangesRequested()
    {
        var (llm, asked) = Llm(Reply("Reading…"), Answer("""{"error":"I had not opened the diff yet."}"""));

        var verdict = await Reviewer(llm).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        verdict.Error.ShouldNotBeNull().ShouldContain("asked once more; still none");
        asked.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"verdict":"approve","findings":[],"summary":"","error":"I stopped half way."}""")]   // error wins
    [InlineData("""{"verdict":"lgtm","findings":[],"summary":"Fine."}""")]                              // no valid verdict
    [InlineData("""{"summary":"The word \"verdict\" alone is not one."}""")]
    [InlineData("""["verdict","approve"]""")]
    public void Review_Recovered_RejectsWhatIsNotAVerdict(string reply) =>
        ReviewAgent.Recovered(reply, resumed: true, out _).ShouldBeNull();

    [Fact]
    public async Task Review_VerdictPresent_AsksNothingMore()
    {
        var (llm, asked) = Llm(Reply("""{"verdict":"changes","findings":[{"severity":"must-fix","file":"a.cs","line":3,"comment":"Null."}],"summary":"One defect."}"""));

        (await Reviewer(llm).RunAsync(Change())).Approved.ShouldBeFalse();
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Testing_NoVerdict_AsksOnce_AndUsesTheRecoveredVerdict()
    {
        var (llm, asked) = Llm(Reply("Build passed, 212 tests passed, then the context ran out."), Answer(Pass));

        var verdict = await Tester(llm).RunAsync(Change());

        verdict.Ran.ShouldBeTrue();
        verdict.Passed.ShouldBeTrue();
        asked.Count.ShouldBe(2);
        asked[1].SystemPrompt.ShouldStartWith("You are the Testing Agent of AgentOS.NET");
        asked[1].Timeout.ShouldBe(ReplyRecovery.Timeout);
        asked[1].UserPrompt.ShouldNotContain("screenshots");   // not a UI ticket
    }

    [Theory]
    [InlineData("""{"verdict":"pass","criteria":[],"summary":"Built fine."}""", "named no criterion")]
    [InlineData("""{"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":"ok"},{"criterion":"F-2","result":"not-checked","evidence":"no data"}],"summary":""}""", "not every criterion was checked")]
    [InlineData("""{"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":""}],"summary":""}""", "no evidence")]
    [InlineData("""{"verdict":"pass","criteria":[{"criterion":"F-1","result":"skipped","evidence":"later"}],"summary":""}""", "not every criterion was checked")]
    public async Task Testing_RecoveredPassWithoutEveryCriterionChecked_IsNotRun_NeverAPass(string reply, string why)
    {
        var (llm, _) = Llm(Reply("…"), Answer(reply, resumed: true));

        var verdict = await Tester(llm).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        verdict.Passed.ShouldBeFalse();
        verdict.Error.ShouldNotBeNull().ShouldContain(why);
    }

    [Fact]
    public async Task Testing_PassFromAFreshConversation_IsNotRun()
    {
        var (llm, _) = Llm(Reply("…"), Answer(Pass, resumed: false));

        var verdict = await Tester(llm).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        verdict.Passed.ShouldBeFalse();
        verdict.Error.ShouldNotBeNull().ShouldContain(ReplyRecovery.NotResumedNote);
    }

    [Fact]
    public async Task Testing_FailFromAFreshConversation_Stands()
    {
        var (llm, _) = Llm(Reply("…"),
            Answer("""{"verdict":"fail","criteria":[{"criterion":"F-1","result":"fail","evidence":"dotnet test: 3 failed"}],"summary":"Fails."}""", resumed: false));

        var verdict = await Tester(llm).RunAsync(Change());

        verdict.Ran.ShouldBeTrue();
        verdict.Passed.ShouldBeFalse();
        verdict.Failures.ShouldHaveSingleItem().Criterion.ShouldBe("F-1");
    }

    [Fact]
    public void Testing_Recovered_PassClaimWithAFailingCriterion_IsAFail()
    {
        var verdict = AcceptanceAgent.Recovered(
            """{"verdict":"pass","criteria":[{"criterion":"F-1","result":"fail","evidence":"3 failed"}],"summary":""}""", resumed: true, out var setAside)
            .ShouldNotBeNull();

        verdict.Passed.ShouldBeFalse();
        setAside.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":"ok"}],"summary":"","error":"I stopped."}""")]
    [InlineData("""{"verdict":"maybe","criteria":[],"summary":""}""")]
    [InlineData("""{"error":"I had not built it yet."}""")]
    public void Testing_Recovered_RejectsWhatIsNotAVerdict(string reply) =>
        AcceptanceAgent.Recovered(reply, resumed: true, out _).ShouldBeNull();

    [Fact]
    public async Task Testing_UiTicket_RecoveredVerdict_KeepsItsScreenshots()
    {
        // The first run took the screenshots, then ran out of context before its verdict. The follow-up works in the kept
        // folder with the same upload target, lists them, and its run sends them back: the Designer agent sees them.
        var capture = new DesignCapture(DesignScope.For(Guid.NewGuid(), 102, 0));
        var shot = new StoredFileInfo(Guid.NewGuid(), "tenant-a", capture.ScopeKey, ".agentos/screenshots/home-390-dark.png", 3, "x",
            "image/png", "member-1", DateTimeOffset.UtcNow);
        var (llm, asked) = Llm(Reply("Took the screenshots, then the context was full."),
            Answer("""
                {"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":"e2e green"}],"summary":"ok",
                 "screenshots":[{"path":".agentos/screenshots/home-390-dark.png","screen":"Home","width":390,"theme":"dark"}]}
                """) with { Uploaded = [shot] });

        var verdict = await Tester(llm).RunAsync(Change(capture));

        asked[1].Uploads.ShouldNotBeNull().ScopeKey.ShouldBe(capture.ScopeKey);
        asked[1].UserPrompt.ShouldContain("\"screenshots\":[");
        verdict.Passed.ShouldBeTrue();
        var s = verdict.Screenshots.ShouldHaveSingleItem();
        s.Path.ShouldBe(".agentos/screenshots/home-390-dark.png");
        s.Screen.ShouldBe("Home");
    }

    [Fact]
    public async Task Testing_FollowUpWithoutVerdict_StaysNotRun_AfterOneAttempt()
    {
        var (llm, asked) = Llm(Reply("…"), Answer("still no verdict"), Answer(Pass));

        var verdict = await Tester(llm).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Testing_RecoverySwitchedOff_AsksNothing()
    {
        var (llm, asked) = Llm(Reply("…"), Answer(Pass));

        var verdict = await Tester(llm, new Profiles(recover: false)).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        asked.Count.ShouldBe(1);
    }

    // ── Setting (ADR-0011) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_IsDeclared_OnByDefault()
    {
        var d = new AutopilotSettings().Describe().Single(x => x.Key == AutopilotSettings.RecoverMissingReplyKey);

        d.Key.ShouldBe("Autopilot:RecoverMissingReply");
        d.Kind.ShouldBe(AgentOs.Domain.Configuration.SettingKind.Boolean);
        d.DefaultValue.ShouldBe("true");
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────────────────────

    private sealed class FakePrs(bool? exists, string?[]? heads = null) : IPrCreationService
    {
        public List<string> Asked { get; } = [];

        public int HeadsAsked { get; private set; }

        public Task<PrCreationResult> CreateFromBranchAsync(WorkspaceDescriptor workspace, string branch, string title, string body,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool?> BranchExistsAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default)
        {
            Asked.Add($"{workspace.Owner}/{workspace.Repo}:{branch}");
            return Task.FromResult(exists);
        }

        public Task<string?> GetBranchHeadAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default)
        {
            var head = heads is { Length: > 0 } h ? h[Math.Min(HeadsAsked, h.Length - 1)] : null;
            HeadsAsked++;
            return Task.FromResult(head);
        }
    }

    // No ValueTask members on NSubstitute (CA2012): a small fake.
    private sealed class Profiles(bool recover) : IAgentProfiles
    {
        public ValueTask<AgentOptions> ResolveAsync(string role, AgentOptions fallback, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fallback);

        public ValueTask<int> MaxIterationsAsync(int fallback, CancellationToken cancellationToken = default) => ValueTask.FromResult(fallback);

        public ValueTask<bool> RecoverMissingReplyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(recover);
    }

    private sealed class CapturingFeed : ISessionRunFeed
    {
        public List<SessionRunEvent> Events { get; } = [];
        public void Publish(SessionRunEvent evt) => Events.Add(evt);
        public IAsyncEnumerable<SessionRunEvent> Subscribe(string tenantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
