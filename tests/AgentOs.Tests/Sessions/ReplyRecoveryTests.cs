// A Board agent run that ends without the JSON reply its prompt asks for (the CLI ran out of turns or context after doing
// the work — VNChess #102 lost a 20-minute migration that way) is asked ONCE for the reply instead of discarded. These
// pin the parsing paths (present / absent / recovered / recovery fails), the one-attempt bound, the branch-on-the-remote
// check through IPrCreationService, the follow-up prompt's narrow context (no ticket text, no secrets), the setting, the
// feed event, and that the Reviewer and Testing agents recover their verdicts the same way.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Tests.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ReplyRecoveryTests
{
    private const string TicketBody = "Migrate the engine to the new board API. SECRET-TICKET-MARKER.";

    private static IssueWorkRequest Request(IPrCreationService? prs = null) => new(
        SessionId: Guid.NewGuid(),
        TenantId: "tenant-a",
        MemberId: "member-1",
        Repos: [new WorkRepo(Guid.NewGuid(), "acme", "chess", "main")],
        IssueNumber: 102,
        IssueTitle: "Migrate to the new board API",
        IssueBody: TicketBody,
        ProviderOverride: "RemoteAgent")
    {
        BranchExists = prs is null ? null : (r, branch, ct) => prs.BranchExistsAsync(
            new WorkspaceDescriptor(Guid.NewGuid(), "tenant-a", SourceProviderKind.GitHub, r.Owner, r.Repo, null, r.DefaultBranch, "pat"), branch, ct),
    };

    private static LlmResponse Reply(string content, string? continuation = null) =>
        AgentTestHelpers.StubResponse(content) with { Continuation = continuation };

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
        var (llm, asked) = Llm(Reply(NoJson, continuation: "req-1"), Reply(Recovered));

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
    public async Task IssueWork_RecoveredBranchNotOnTheRemote_IsDoneButNotPushed()
    {
        var (llm, _) = Llm(Reply(NoJson), Reply(Recovered));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: false)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldStartWith("The work was done but not pushed — branch issue-102-ai-fix");
        outcome.Error.ShouldContain("git push -u origin issue-102-ai-fix");
    }

    [Fact]
    public async Task IssueWork_AgentSaysNotPushed_IsDoneButNotPushed_EvenWhenTheRemoteCantBeChecked()
    {
        var (llm, _) = Llm(Reply(NoJson), Reply(Recovered.Replace("\"pushed\":true", "\"pushed\":false", StringComparison.Ordinal)));

        var outcome = (await Agent(llm).RunAsync(Request(new FakePrs(exists: null)))).Repos.ShouldHaveSingleItem();

        outcome.Ok.ShouldBeFalse();
        outcome.Error.ShouldNotBeNull().ShouldStartWith("The work was done but not pushed");
    }

    [Fact]
    public async Task IssueWork_RemoteCantBeChecked_TheAgentsWordStands()
    {
        var (llm, _) = Llm(Reply(NoJson), Reply(Recovered));

        (await Agent(llm).RunAsync(Request(new FakePrs(exists: null)))).Repos.ShouldHaveSingleItem().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task IssueWork_FollowUpAlsoWithoutJson_FailsAfterExactlyOneMoreCall()
    {
        var feed = new CapturingFeed();
        var (llm, asked) = Llm(Reply(NoJson), Reply("Still thinking about it."), Reply(Recovered));

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
        var (llm, asked) = Llm(Reply(NoJson), Reply(Recovered));

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
        var (llm, asked) = Llm(Reply(withSecrets), Reply(Recovered));

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
    public void Tail_MasksCredentialsInUrlsAndKeys()
    {
        var tail = ReplyRecovery.Tail("git push https://hoang:ghp_0123456789abcdefABCDEF@github.com/o/r.git — token github_pat_11AAAAAAAA0123456789");

        tail.ShouldNotContain("ghp_0123456789abcdefABCDEF");
        tail.ShouldNotContain("hoang:");
        tail.ShouldNotContain("github_pat_11AAAAAAAA0123456789");
        tail.ShouldContain("https://***@github.com/o/r.git");
    }

    [Fact]
    public async Task VerifyRecovered_FailedOutcome_IsLeftAlone_AndTheRemoteIsNotAsked()
    {
        var prs = new FakePrs(exists: true);
        var req = Request(prs);
        var failed = new RepoWorkOutcome(req.Repos[0].SessionRepoId, false, "acme", "chess", "", "", "Build failed.");

        var outcome = await IssueWorkAgent.VerifyRecoveredAsync(failed, null, req, req.Repos[0], NullLogger.Instance, CancellationToken.None);

        outcome.ShouldBeSameAs(failed);
        prs.Asked.ShouldBeEmpty();
    }

    // ── Reviewer + Testing agents ──────────────────────────────────────────────────────────────

    private static AcceptanceRequest Change() =>
        new(Guid.NewGuid(), "tenant-a", "member-1", new WorkRepo(Guid.NewGuid(), "acme", "chess", "main", "issue-102-ai-fix"),
            102, "Migrate to the new board API", TicketBody, "claude");

    [Fact]
    public async Task Review_NoVerdict_AsksOnce_AndUsesTheRecoveredVerdict()
    {
        var feed = new CapturingFeed();
        var (llm, asked) = Llm(Reply("I read the diff and the tests.", "req-7"),
            Reply("""{"verdict":"approve","findings":[],"summary":"Looks right."}"""));

        var verdict = await new ReviewAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<ReviewAgent>.Instance, feed).RunAsync(Change());

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
    public async Task Review_FollowUpCouldNotFinish_StaysNotRun_NeverChangesRequested()
    {
        var (llm, asked) = Llm(Reply("Reading…"), Reply("""{"error":"I had not opened the diff yet."}"""));

        var verdict = await new ReviewAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<ReviewAgent>.Instance).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        verdict.Error.ShouldNotBeNull().ShouldContain("asked once more; still none");
        asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Review_VerdictPresent_AsksNothingMore()
    {
        var (llm, asked) = Llm(Reply("""{"verdict":"changes","findings":[{"severity":"must-fix","file":"a.cs","line":3,"comment":"Null."}],"summary":"One defect."}"""));

        (await new ReviewAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<ReviewAgent>.Instance).RunAsync(Change())).Approved.ShouldBeFalse();
        asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Testing_NoVerdict_AsksOnce_AndUsesTheRecoveredVerdict()
    {
        var (llm, asked) = Llm(Reply("Build passed, 212 tests passed, then the context ran out."),
            Reply("""{"verdict":"pass","criteria":[{"criterion":"F-1","result":"pass","evidence":"dotnet test: 212 passed"}],"baseFailures":[],"summary":"Passes."}"""));

        var verdict = await new AcceptanceAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<AcceptanceAgent>.Instance).RunAsync(Change());

        verdict.Ran.ShouldBeTrue();
        verdict.Passed.ShouldBeTrue();
        asked.Count.ShouldBe(2);
        asked[1].SystemPrompt.ShouldStartWith("You are the Testing Agent of AgentOS.NET");
        asked[1].Timeout.ShouldBe(ReplyRecovery.Timeout);
    }

    [Fact]
    public async Task Testing_FollowUpWithoutVerdict_StaysNotRun_AfterOneAttempt()
    {
        var (llm, asked) = Llm(Reply("…"), Reply("still no verdict"), Reply("""{"verdict":"pass","criteria":[],"summary":""}"""));

        var verdict = await new AcceptanceAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<AcceptanceAgent>.Instance).RunAsync(Change());

        verdict.Ran.ShouldBeFalse();
        asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Testing_RecoverySwitchedOff_AsksNothing()
    {
        var (llm, asked) = Llm(Reply("…"), Reply("""{"verdict":"pass","criteria":[],"summary":""}"""));

        var verdict = await new AcceptanceAgent(AgentTestHelpers.FactoryReturning(llm), NullLogger<AcceptanceAgent>.Instance,
            profiles: new Profiles(recover: false)).RunAsync(Change());

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

    private sealed class FakePrs(bool? exists) : IPrCreationService
    {
        public List<string> Asked { get; } = [];

        public Task<PrCreationResult> CreateFromBranchAsync(WorkspaceDescriptor workspace, string branch, string title, string body,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool?> BranchExistsAsync(WorkspaceDescriptor workspace, string branch, CancellationToken ct = default)
        {
            Asked.Add($"{workspace.Owner}/{workspace.Repo}:{branch}");
            return Task.FromResult(exists);
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
