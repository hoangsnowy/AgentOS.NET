// A member's Claude / Codex plan limit, read from what their computer's CLI answered — real-shaped lines (claude -p
// on the lab, 2026-10: "You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)"; older Claude Code's
// "usage limit reached|<epoch>"; Codex's "try again in …") — and never from an answer that merely mentions a limit.
// The RemoteAgent provider turns a limit into PlanLimitException + a per-member wait Autopilot honours.

using System;
using System.Globalization;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class PlanLimitTests
{
    // 17:00 in Ho Chi Minh City (UTC+7), 11:00 in Berlin (CET, UTC+1 after 25 Oct 2026).
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    // The test host runs with invariant globalization (no ICU on Windows), so IANA zones are resolved here.
    private static TimeZoneInfo? Zones(string id) => id switch
    {
        "Asia/Ho_Chi_Minh" => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(7), id, id),
        "Europe/Berlin" => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(1), id, id),
        _ => null,
    };

    [Fact]
    public void Detect_ClaudeSessionLimit_ResetInTheNamedZone()
    {
        var limit = PlanLimitDetector.Detect("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", "claude", Now, Zones);

        limit.ShouldNotBeNull();
        limit!.Plan.ShouldBe("Claude");
        limit.ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 24, 23, 50, 0, TimeSpan.FromHours(7)));
        limit.InNamedZone.ShouldBeTrue();
        limit.ResetText.ShouldBe("23:50");
        limit.Message.ShouldBe("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)");
    }

    [Fact]
    public void Detect_ResetTimeAlreadyPassedToday_IsTomorrow()
    {
        var lateEvening = new DateTimeOffset(2026, 10, 24, 16, 55, 0, TimeSpan.Zero); // 23:55 in Ho Chi Minh City

        PlanLimitDetector.Detect("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", null, lateEvening, Zones)!
            .ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 25, 23, 50, 0, TimeSpan.FromHours(7)));
    }

    // The runner reports a non-zero exit as "exit N: <what the CLI printed>" after earlier output.
    [Fact]
    public void Detect_InARunnerError_KeepsOnlyTheCliLine()
    {
        var limit = PlanLimitDetector.Detect("exit 1: Cloning into 'repo'...\nYou've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", null, Now, Zones);

        limit!.Message.ShouldBe("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)");
        PlanLimitDetector.Detect("exit 1: You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", null, Now, Zones)!
            .Message.ShouldStartWith("You've hit");
    }

    [Fact]
    public void Detect_WeeklyLimit_WithADate()
    {
        PlanLimitDetector.Detect("You've reached your weekly limit · resets Oct 27, 5pm (Europe/Berlin)", null, Now, Zones)!
            .ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 27, 17, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Detect_OlderClaudeCode_EpochReset()
    {
        var reset = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero);
        var limit = PlanLimitDetector.Detect("Claude AI usage limit reached|" + reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), null, Now);

        limit!.ResetsAt.ShouldBe(reset);
        limit.InNamedZone.ShouldBeFalse();
        limit.ResetText.ShouldBe("15:00 UTC");
    }

    [Fact]
    public void Detect_Codex_TryAgainInADuration()
    {
        var limit = PlanLimitDetector.Detect(
            "■ You've hit your usage limit. Upgrade to Pro (https://openai.com/chatgpt/pricing) or try again in 2 hours 5 minutes.", "codex", Now);

        limit!.Plan.ShouldBe("Codex");
        limit.ResetsAt.ShouldBe(Now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5));
    }

    // A wall-clock reset without a zone can't be placed (the server doesn't know the computer's zone): still a limit.
    [Fact]
    public void Detect_ResetWithoutAZone_IsALimitWithAnUnknownReset()
    {
        var limit = PlanLimitDetector.Detect("5-hour limit reached ∙ resets 3pm", null, Now, Zones);

        limit.ShouldNotBeNull();
        limit!.ResetsAt.ShouldBeNull();
        limit.ResetText.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Approaching usage limit · resets at 3pm")]
    [InlineData("Added handling for when the API rate limit reached its cap.")]
    [InlineData("{\"branch\":\"issue-7-ai-fix\",\"summary\":\"Show the session limit in the footer\"}")]
    public void Detect_NotALimit_IsNull(string? text) => PlanLimitDetector.Detect(text, null, Now, Zones).ShouldBeNull();

    // A clean reply is work unless it is short: an answer that quotes a limit notice inside real output stays work.
    [Fact]
    public void FromReply_CleanReply_CountsOnlyWhenShort()
    {
        const string notice = "You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)";

        PlanLimitDetector.FromReply(true, notice, null, null, Now, Zones).ShouldNotBeNull();
        PlanLimitDetector.FromReply(true, new string('x', 700) + notice, null, null, Now, Zones).ShouldBeNull();
        PlanLimitDetector.FromReply(false, "", "exit 1: " + new string('x', 5000) + "\n" + notice, null, Now, Zones).ShouldNotBeNull();
        PlanLimitDetector.FromReply(true, "ok", null, null, Now, Zones).ShouldBeNull();
    }

    [Fact]
    public void IsPlanLimit_ReadsAStoredError() =>
        PlanLimitDetector.IsPlanLimit("LLM call failed: You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)").ShouldBeTrue();

    // ── The per-member wait ──

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Tracker_WaitsUntilTheResetPlusAMinute_PerMember()
    {
        var time = new ManualTime(Now);
        var tracker = new PlanLimitTracker(time);
        var limit = PlanLimitDetector.Detect("You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)", null, Now, Zones)!;

        tracker.Report("t1", "alice", limit);

        var hold = tracker.Current("t1", "alice");
        hold.ShouldNotBeNull();
        hold!.UntilUtc.ShouldBe(new DateTimeOffset(2026, 10, 24, 16, 51, 0, TimeSpan.Zero));
        hold.Sentence.ShouldBe("Your computer's Claude plan hit its limit — Autopilot waits until 23:50, nothing is lost.");
        tracker.Current("t1", "bob").ShouldBeNull();   // another member's computer, another plan
        tracker.Current("t2", "alice").ShouldBeNull(); // another tenant

        time.Now = new DateTimeOffset(2026, 10, 24, 16, 50, 30, TimeSpan.Zero);
        tracker.Current("t1", "alice").ShouldNotBeNull(); // the reset, but not yet the minute after it
        time.Now = new DateTimeOffset(2026, 10, 24, 16, 51, 0, TimeSpan.Zero);
        tracker.Current("t1", "alice").ShouldBeNull();
    }

    [Fact]
    public void Tracker_UnknownReset_ChecksAgainLater()
    {
        var tracker = new PlanLimitTracker(new ManualTime(Now));

        tracker.Report("t1", "alice", new PlanLimit("Claude", "5-hour limit reached", null));

        var hold = tracker.Current("t1", "alice")!;
        hold.UntilUtc.ShouldBe(Now + PlanLimitTracker.UnknownResetWait + PlanLimitTracker.Grace);
        hold.Sentence.ShouldBe("Your computer's Claude plan hit its limit — Autopilot checks again at 10:31 UTC, nothing is lost.");
    }

    // ── The provider ──

    [Fact]
    public async Task Client_PlanLimitReply_ThrowsTheTypedSignal_AndRecordsTheMembersWait()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "t1", "alice"));
        broker.Dispatched += d => broker.Complete(new RemoteExecResult(d.Request.Id, false, "",
            "You've hit your session limit · resets 11:50pm (UTC)"));
        var limits = new PlanLimitTracker(TimeProvider.System);
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns("t1");
        ctx.UserId.Returns("alice");
        var services = new ServiceCollection();
        services.AddScoped(_ => ctx);
        services.AddSingleton<IPlanLimits>(limits);
        using var sp = services.BuildServiceProvider();
        var client = new RemoteAgentLlmClient(broker, sp, NullLogger<RemoteAgentLlmClient>.Instance);

        var ex = await Should.ThrowAsync<PlanLimitException>(client.SendAsync(new LlmRequest("s", "u", "m")));

        ex.ShouldBeAssignableTo<LlmException>(); // callers that catch provider failures keep working
        ex.Limit!.Plan.ShouldBe("Claude");
        limits.Current("t1", "alice").ShouldNotBeNull();
    }
}
