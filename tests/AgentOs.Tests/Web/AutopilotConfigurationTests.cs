// Autopilot is configured in Settings › Autopilot (ADR-0011) and waits out a member's plan limit:
// the descriptors are bounded with today's values as defaults, the typed read clamps whatever is stored, the plan-limit
// pause logs once when it starts and once when it ends, work a limit cut short is recognised from its stored error,
// and the acceptance-run gate honours the configured cap per member's computer.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Web.Services.BoardTasks;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class AutopilotConfigurationTests
{
    // ── Settings ──

    [Fact]
    public void Describe_EveryBoundIsAWholeNumberInARange_DefaultingToTodaysValues()
    {
        var all = new AutopilotSettings().Describe().ToDictionary(d => d.Key);

        all.Values.ShouldAllBe(d => d.Category == AutopilotSettings.Category && d.Scope == SettingScope.Tenant && !string.IsNullOrWhiteSpace(d.Help));
        (string Key, string Default, decimal Min, decimal Max)[] expected =
        [
            (AutopilotSettings.MaxParallelKey, "2", 1, 6),
            (AutopilotSettings.MaxParallelTestsKey, "1", 1, 3),
            (AutopilotSettings.MaxFixRoundsKey, "3", 0, 10),
            (AutopilotSettings.MaxAgentRetriesKey, "2", 0, 5),
            (AutopilotSettings.ChecksTimeoutMinutesKey, "60", 5, 240),
            (AutopilotSettings.ChecksPollSecondsKey, "60", 15, 600),
        ];
        foreach (var (key, def, min, max) in expected)
        {
            var d = all[key];
            d.Kind.ShouldBe(SettingKind.WholeNumber);
            d.DefaultValue.ShouldBe(def);
            d.Min.ShouldBe(min);
            d.Max.ShouldBe(max);
        }
        all[AutopilotSettings.DecidedByKey].Kind.ShouldBe(SettingKind.Choice);
        all[AutopilotSettings.TicketEngineKey].Kind.ShouldBe(SettingKind.Choice);
    }

    private sealed class FakeSettings(Func<string, string?> values) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SettingValue(tenantId == "t1" ? values(key) : null, SettingSource.Tenant));

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SettingWriteResult> SetAsync(string key, string? value, SettingScope scope, string? tenantId, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadLimits_Unset_IsTheDefault()
    {
        (await AutopilotSettings.ReadLimitsAsync(new FakeSettings(_ => null), "t1")).ShouldBe(AutopilotLimits.Default);
        AutopilotLimits.Default.ShouldBe(new AutopilotLimits(2, 1, 3, 2, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task ReadLimits_ReadsTheTenantsValues_ClampedToTheirRanges()
    {
        var settings = new FakeSettings(key => key switch
        {
            AutopilotSettings.MaxParallelKey => "4",
            AutopilotSettings.MaxParallelTestsKey => "99", // a seeded value can't escape the range
            AutopilotSettings.MaxFixRoundsKey => "0",
            AutopilotSettings.ChecksTimeoutMinutesKey => "90",
            _ => null,
        });

        var limits = await AutopilotSettings.ReadLimitsAsync(settings, "t1");

        limits.MaxParallel.ShouldBe(4);
        limits.MaxParallelTests.ShouldBe(3);
        limits.MaxFixRounds.ShouldBe(0);
        limits.MaxAgentRetries.ShouldBe(2);
        limits.ChecksTimeout.ShouldBe(TimeSpan.FromMinutes(90));
        limits.ChecksPoll.ShouldBe(TimeSpan.FromSeconds(60));
        limits.Uncapped.MaxParallel.ShouldBe(int.MaxValue);
        limits.Uncapped.MaxFixRounds.ShouldBe(0); // uncapped slots, never uncapped rounds
    }

    // The Orchestrator agent's budget: the configured rounds + its own retries, against the persisted count.
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 0)]
    public void RetriesLeft_UsesTheConfiguredBudget(int fixRounds, int maxAgentRetries, int expected) =>
        ProjectOrchestrator.RetriesLeft(fixRounds, AutopilotLimits.Default with { MaxFixRounds = 1, MaxAgentRetries = maxAgentRetries }).ShouldBe(expected);

    // ── Plan-limit pause ──

    private static PlanLimitHold Hold(int hour) =>
        new(new PlanLimit("Claude", "You've hit your session limit", new DateTimeOffset(2026, 10, 24, hour, 50, 0, TimeSpan.FromHours(7)), true),
            new DateTimeOffset(2026, 10, 24, hour - 7, 51, 0, TimeSpan.Zero));

    [Fact]
    public void Pause_LogsOnceWhenTheWaitStarts_AndOnceWhenItEnds()
    {
        var pause = new PlanLimitPause();
        var board = Guid.NewGuid();

        pause.Observe(board, null).Paused.ShouldBeFalse();
        pause.Observe(board, Hold(23)).ShouldBe((true, "Your computer's Claude plan hit its limit — Autopilot waits until 23:50, nothing is lost."));
        var later = pause.Observe(board, Hold(23)); // every later pass: still paused, nothing logged
        later.Paused.ShouldBeTrue();
        later.Line.ShouldBeNull();
        pause.Observe(Guid.NewGuid(), Hold(23)).Line.ShouldNotBeNull(); // each board logs its own line
        pause.Observe(board, null).ShouldBe((false, "Your computer's Claude plan is open again — Autopilot carries on where it stopped."));
        var after = pause.Observe(board, null);
        after.Paused.ShouldBeFalse();
        after.Line.ShouldBeNull();
    }

    [Fact]
    public void Pause_ALaterResetIsAnnounced()
    {
        var pause = new PlanLimitPause();
        var board = Guid.NewGuid();
        pause.Observe(board, Hold(20));

        pause.Observe(board, Hold(23)).Line.ShouldNotBeNull();
    }

    [Fact]
    public void CutShortByPlanLimit_ReadsTheStoredErrors()
    {
        var session = new RemoteSessionEntity { Id = Guid.NewGuid(), Status = "Failed" };
        var limited = new SessionRepoEntity { SessionId = session.Id, Status = "Failed", Error = "LLM call failed: You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)" };
        var broken = new SessionRepoEntity { SessionId = session.Id, Status = "Failed", Error = "push rejected" };

        ProjectOrchestrator.CutShortByPlanLimit(session, new[] { limited }.ToLookup(r => r.SessionId)).ShouldBeTrue();
        ProjectOrchestrator.CutShortByPlanLimit(session, new[] { broken }.ToLookup(r => r.SessionId)).ShouldBeFalse();
        ProjectOrchestrator.CutShortByPlanLimit(new RemoteSessionEntity { Id = session.Id, Error = "You've hit your usage limit." },
            Array.Empty<SessionRepoEntity>().ToLookup(r => r.SessionId)).ShouldBeTrue();
    }

    // ── Acceptance-run gate ──

    [Fact]
    public async Task RunGate_HonoursTheCapPerComputer()
    {
        var gate = new RunGate();
        using var a = await gate.EnterAsync("t1/alice", 1);
        using var other = await gate.EnterAsync("t1/bob", 1); // another member's computer never waits on alice's

        var second = gate.EnterAsync("t1/alice", 1);
        await Task.Delay(50);
        second.IsCompleted.ShouldBeFalse();
        gate.Held("t1/alice").ShouldBe(1);

        a.Dispose();
        using (await second.WaitAsync(TimeSpan.FromSeconds(5))) { gate.Held("t1/alice").ShouldBe(1); }
        gate.Held("t1/alice").ShouldBe(0);
    }

    [Fact]
    public async Task RunGate_ARaisedCap_LetsTwoIn()
    {
        var gate = new RunGate();
        using var a = await gate.EnterAsync("k", 2);
        using var b = await gate.EnterAsync("k", 2).WaitAsync(TimeSpan.FromSeconds(5));

        gate.Held("k").ShouldBe(2);
    }
}
