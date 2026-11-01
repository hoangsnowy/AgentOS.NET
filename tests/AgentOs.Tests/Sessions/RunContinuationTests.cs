// The runner side of asking a run again for its missing reply: a run with hands whose reply looked unfinished keeps its
// folder (and Claude Code session) for ONE follow-up, a run that stopped at its turn or context limit is reported as a
// reply without its answer (not a failure that discards the work), and the follow-up resumes that conversation only
// when it can. Plus the RemoteAgent client: ResumeOf travels to the runner, and a scoped run's reply can be continued.

using System;
using System.IO;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.RemoteAgent;
using AgentOs.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunContinuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private const string Session = "6b0b2f5e-1c1d-4b8a-9d5e-2f6a1d3c4b5a";

    [Fact]
    public void Parse_ResultEvent_CarriesSubtypeAndSessionId()
    {
        var (_, final) = CliProgress.Parse($$"""{"type":"result","subtype":"error_max_turns","is_error":false,"session_id":"{{Session}}","num_turns":200}""");

        final.ShouldNotBeNull();
        final!.Subtype.ShouldBe("error_max_turns");
        final.SessionId.ShouldBe(Session);
        RunContinuation.StoppedShort(final).ShouldBeTrue();
        RunContinuation.ContextFull(final).ShouldBeFalse();
        RunContinuation.StoppedNote(final).ShouldContain("turn limit");
    }

    [Fact]
    public void StoppedShort_ContextFull_IsDetected_AndNotResumed()
    {
        var final = new CliProgress.Result(true, "Prompt is too long", null, null) { Subtype = "success", SessionId = Session };

        RunContinuation.StoppedShort(final).ShouldBeTrue();
        RunContinuation.ContextFull(final).ShouldBeTrue();
        RunContinuation.ResumeArgs("claude", new RunContinuation.Parked("/tmp/x", Session, true, Now)).ShouldBeEmpty();
    }

    [Fact]
    public void StoppedShort_OrdinaryAnswerOrError_IsNot()
    {
        RunContinuation.StoppedShort(new CliProgress.Result(false, "{\"branch\":\"b\"}", null, null) { Subtype = "success" }).ShouldBeFalse();
        RunContinuation.StoppedShort(new CliProgress.Result(true, "Not logged in · Please run /login", null, null)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("I did the work and ran out of turns.", true)]
    [InlineData("Done. {\"branch\":\"issue-1-ai-fix\"}", false)]
    public void LooksUnfinished_NoJsonObject(string reply, bool expected) => RunContinuation.LooksUnfinished(reply).ShouldBe(expected);

    [Fact]
    public void Park_ThenTake_HandsTheFolderOutOnce()
    {
        var dir = Directory.CreateTempSubdirectory("agentos-run-").FullName;
        try
        {
            var runs = new RunContinuation();
            runs.Park("req-1", dir, Session, false, Now);

            var parked = runs.Take("req-1", Now.AddMinutes(3)).ShouldNotBeNull();
            parked.Folder.ShouldBe(dir);
            parked.SessionId.ShouldBe(Session);
            runs.Take("req-1", Now.AddMinutes(3)).ShouldBeNull();   // one follow-up only
            runs.Take("someone-else", Now).ShouldBeNull();
            runs.Take(null, Now).ShouldBeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Expired_ReturnsTheFoldersToDelete_AndForgetsThem()
    {
        var runs = new RunContinuation();
        runs.Park("old", "/runs/old", null, false, Now);
        runs.Park("new", "/runs/new", null, false, Now.AddMinutes(10));

        runs.Expired(Now.Add(RunContinuation.Keep).AddMinutes(1)).ShouldBe(["/runs/old"]);
        runs.Count.ShouldBe(1);
        runs.Take("old", Now).ShouldBeNull();
    }

    [Fact]
    public void Take_AfterTheKeepWindow_OrFolderGone_IsNull()
    {
        var runs = new RunContinuation();
        runs.Park("late", Path.Join(Path.GetTempPath(), "agentos-missing-" + Guid.NewGuid().ToString("N")), Session, false, Now);

        runs.Take("late", Now).ShouldBeNull();   // the folder is gone
    }

    [Theory]
    [InlineData("--dangerous")]
    [InlineData("abc def")]
    [InlineData("x;rm -rf ~")]
    public void Park_SessionIdThatIsNotAPlainId_IsDropped(string session)
    {
        var dir = Directory.CreateTempSubdirectory("agentos-run-").FullName;
        try
        {
            var runs = new RunContinuation();
            runs.Park("req", dir, session, false, Now);
            var parked = runs.Take("req", Now).ShouldNotBeNull();
            parked.SessionId.ShouldBeNull();
            RunContinuation.ResumeArgs("claude", parked).ShouldBeEmpty();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", true)]
    [InlineData("codex", false)]
    public void ResumeArgs_OnlyClaudeResumesItsSession(string command, bool resumes)
    {
        var args = RunContinuation.ResumeArgs(command, new RunContinuation.Parked("/runs/x", Session, false, Now));

        if (resumes) { args.ShouldBe(["--resume", Session]); }
        else { args.ShouldBeEmpty(); }
        RunContinuation.ResumeArgs(command, null).ShouldBeEmpty();
    }

    // ── RemoteAgentLlmClient ───────────────────────────────────────────────────────────────────

    private static IServiceProvider Tenant()
    {
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns("default");
        ctx.UserId.Returns("member-1");
        var services = new ServiceCollection();
        services.AddScoped(_ => ctx);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Client_ScopedRun_CanBeContinued_AndAFollowUpNamesIt()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "default", "member-1"));
        RemoteExecRequest? last = null;
        broker.Dispatched += d =>
        {
            last = d.Request;
            broker.Complete(new RemoteExecResult(d.Request.Id, true, "no json here", null));
        };
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        var first = await client.SendAsync(new LlmRequest("s", "u", "m") { Autonomy = WorkspaceAutonomy.Scoped });
        first.Continuation.ShouldBe(last!.Id);
        last.ResumeOf.ShouldBeNull();

        await client.SendAsync(new LlmRequest("s", "u", "m") { Autonomy = WorkspaceAutonomy.Scoped, ResumeOf = first.Continuation });
        last.ResumeOf.ShouldBe(first.Continuation);

        (await client.SendAsync(new LlmRequest("s", "u", "m"))).Continuation.ShouldBeNull();   // answer-only runs keep nothing
    }
}
