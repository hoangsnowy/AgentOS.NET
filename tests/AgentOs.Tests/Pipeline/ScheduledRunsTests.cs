// Scheduled runs (parity item 3): the spec (once / cron, ≥ 15-minute cadence, UTC), the tenant-explicit service
// (validation, per-tenant cap, isolation), and the worker (claims each occurrence once, runs it through the
// orchestrator under the owner's identity, records skips for budget / run limits, never catch-up storms).

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Scheduling;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Pipeline;

public sealed class ScheduledRunsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- spec ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0 9 * * *", null)]
    [InlineData("*/15 * * * *", null)]
    [InlineData("* * * * *", "15 minutes")]
    [InlineData("*/5 * * * *", "15 minutes")]
    [InlineData("0,5 9 * * *", "15 minutes")]          // fine most of the day, two runs 5 min apart at 09:00
    [InlineData("not cron", "Not a valid cron")]
    [InlineData("", "Enter a cron")]
    public void Validate_Cron_EnforcesTheMinimumCadence(string cron, string? error)
    {
        var result = ScheduleSpec.Every(cron).Validate(Noon);
        if (error is null) { result.ShouldBeNull(); } else { result.ShouldNotBeNull().ShouldContain(error); }
    }

    [Fact]
    public void Once_MustBeInTheFuture_AndFiresOnlyOnce()
    {
        ScheduleSpec.At(Noon.AddMinutes(-1)).Validate(Noon).ShouldNotBeNull();
        var spec = ScheduleSpec.At(Noon.AddHours(1));
        spec.Validate(Noon).ShouldBeNull();
        spec.NextAfter(Noon).ShouldBe(Noon.AddHours(1));
        spec.NextAfter(Noon.AddHours(1)).ShouldBeNull();
    }

    [Fact]
    public void Presets_AreCronUnderneath_AndDescribeThemselves()
    {
        ScheduleSpec.Daily(new TimeOnly(9, 30)).Describe().ShouldBe("Every day at 09:30 UTC");
        ScheduleSpec.Weekdays(new TimeOnly(9, 0)).Describe().ShouldBe("Weekdays at 09:00 UTC");
        ScheduleSpec.Weekly(DayOfWeek.Monday, new TimeOnly(8, 5)).Describe().ShouldBe("Every Monday at 08:05 UTC");
        ScheduleSpec.Every("0 */6 * * *").Describe().ShouldBe("Cron 0 */6 * * * (UTC)");
        ScheduleSpec.At(Noon).Describe().ShouldBe("Once · 2026-10-19 12:00 UTC");
        ScheduleSpec.Daily(new TimeOnly(9, 0)).NextAfter(Noon).ShouldBe(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        ScheduleSpec.Weekdays(new TimeOnly(9, 0)).NextAfter(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero))   // Friday after 09:00
            .ShouldBe(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));                                       // → Monday
    }

    // ---- service ---------------------------------------------------------------------------------------

    private static (ScheduledRunService Service, InMemoryScheduledRunStore Store, ManualClock Clock) NewService()
    {
        var store = new InMemoryScheduledRunStore();
        var clock = new ManualClock(Noon);
        return (new ScheduledRunService(store, clock, new ServiceCollection().BuildServiceProvider()), store, clock);
    }

    [Fact]
    public async Task Create_ValidatesStoryAndSchedule_AndComputesTheNextRun()
    {
        var (svc, _, _) = NewService();
        var ct = TestContext.Current.CancellationToken;
        (await svc.CreateAsync("acme", "  ", ScheduleSpec.Daily(new TimeOnly(9, 0)), "alice", ct)).Ok.ShouldBeFalse();
        (await svc.CreateAsync("acme", "Nightly smoke test", ScheduleSpec.Every("* * * * *"), "alice", ct)).Error.ShouldNotBeNull();

        var ok = await svc.CreateAsync("acme", "Nightly smoke test", ScheduleSpec.Daily(new TimeOnly(2, 0)), "alice", ct);
        ok.Ok.ShouldBeTrue();
        ok.Run!.NextRunAtUtc.ShouldBe(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero));
        ok.Run.CreatedBy.ShouldBe("alice");
    }

    [Fact]
    public async Task Schedules_AreTenantIsolated_AndCapped()
    {
        var (svc, _, _) = NewService();
        var ct = TestContext.Current.CancellationToken;
        var created = await svc.CreateAsync("acme", "A", ScheduleSpec.Daily(new TimeOnly(9, 0)), "alice", ct);
        (await svc.ListAsync("globex", ct)).ShouldBeEmpty();
        (await svc.DeleteAsync("globex", created.Run!.Id, "mallory", ct)).Ok.ShouldBeFalse();   // other tenant can't touch it
        (await svc.SetEnabledAsync("globex", created.Run.Id, false, "mallory", ct)).Ok.ShouldBeFalse();
        (await svc.ListAsync("acme", ct)).ShouldHaveSingleItem();

        for (var i = 1; i < ScheduledRunService.MaxPerTenant; i++)
        {
            (await svc.CreateAsync("acme", $"S{i}", ScheduleSpec.Daily(new TimeOnly(9, 0)), "alice", ct)).Ok.ShouldBeTrue();
        }
        (await svc.CreateAsync("acme", "one too many", ScheduleSpec.Daily(new TimeOnly(9, 0)), "alice", ct)).Error.ShouldNotBeNull().ShouldContain("20");
    }

    [Fact]
    public async Task Pause_ClearsTheNextRun_ResumeRecomputesIt()
    {
        var (svc, store, clock) = NewService();
        var ct = TestContext.Current.CancellationToken;
        var run = (await svc.CreateAsync("acme", "A", ScheduleSpec.Daily(new TimeOnly(9, 0)), "alice", ct)).Run!;
        (await svc.SetEnabledAsync("acme", run.Id, false, "alice", ct)).Ok.ShouldBeTrue();
        (await store.ListAsync("acme", ct)).Single().NextRunAtUtc.ShouldBeNull();
        clock.Now = Noon.AddDays(3);
        await svc.SetEnabledAsync("acme", run.Id, true, "alice", ct);
        (await store.ListAsync("acme", ct)).Single().NextRunAtUtc.ShouldBe(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    }

    // ---- worker ----------------------------------------------------------------------------------------

    private static PipelineResult Result(PipelineStatus status) => new(null!, null!, null!, null!, [], status, null!);

    private static (ScheduledRunWorker Worker, InMemoryScheduledRunStore Store, ManualClock Clock, IOrchestratorAgent Orchestrator) NewWorker()
    {
        var store = new InMemoryScheduledRunStore();
        var clock = new ManualClock(Noon);
        var orchestrator = Substitute.For<IOrchestratorAgent>();
        var provider = new ServiceCollection()
            .AddSingleton<IScheduledRunStore>(store)
            .AddSingleton(orchestrator)
            .BuildServiceProvider();
        return (new ScheduledRunWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<ScheduledRunWorker>.Instance), store, clock, orchestrator);
    }

    private static ScheduledRunInfo Due(ScheduleSpec spec, DateTimeOffset next, string story = "Nightly") =>
        new(Guid.NewGuid(), story, spec, true, next, null, null, "alice", Noon.AddDays(-1));

    [Fact]
    public async Task Tick_RunsADueSchedule_OnceUnderTheOwnersIdentity_AndRecordsTheOutcome()
    {
        var (worker, store, _, orchestrator) = NewWorker();
        var ct = TestContext.Current.CancellationToken;
        string? seenTenant = null;
        orchestrator.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            seenTenant = AmbientIdentity.Current?.TenantId;
            return Result(PipelineStatus.Done);
        });
        var run = Due(ScheduleSpec.Daily(new TimeOnly(11, 59)), Noon.AddMinutes(-1));
        await store.AddAsync("acme", run, ct);

        await Task.WhenAll(await worker.RunDueAsync(ct));
        await Task.WhenAll(await worker.RunDueAsync(ct));   // same tick again: already claimed → nothing

        await orchestrator.Received(1).RunAsync(Arg.Is<UserStory>(s => s.Description == "Nightly"), Arg.Any<CancellationToken>());
        seenTenant.ShouldBe("acme");
        var after = (await store.ListAsync("acme", ct)).Single();
        after.LastStatus.ShouldBe("Done");
        after.NextRunAtUtc.ShouldBe(new DateTimeOffset(2026, 10, 20, 11, 59, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Tick_MissedOccurrences_RunOnce_NoCatchUpStorm()
    {
        var (worker, store, _, orchestrator) = NewWorker();
        var ct = TestContext.Current.CancellationToken;
        orchestrator.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(Result(PipelineStatus.Done));
        // Host was down for 5 days of an hourly schedule.
        await store.AddAsync("acme", Due(ScheduleSpec.Every("0 * * * *"), Noon.AddDays(-5)), ct);

        await Task.WhenAll(await worker.RunDueAsync(ct));

        await orchestrator.Received(1).RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>());
        (await store.ListAsync("acme", ct)).Single().NextRunAtUtc.ShouldBe(Noon.AddHours(1));
    }

    [Fact]
    public async Task Tick_BudgetAndRunLimits_AreRecordedAsSkipped_OneOffsFinish()
    {
        var (worker, store, _, orchestrator) = NewWorker();
        var ct = TestContext.Current.CancellationToken;
        orchestrator.RunAsync(Arg.Is<UserStory>(s => s.Description == "budget"), Arg.Any<CancellationToken>())
            .Returns<PipelineResult>(_ => throw new BudgetExceededException("acme", 10m, 10m));
        orchestrator.RunAsync(Arg.Is<UserStory>(s => s.Description == "limit"), Arg.Any<CancellationToken>())
            .Returns<PipelineResult>(_ => throw new RunAdmissionDeniedException("busy"));
        await store.AddAsync("acme", Due(ScheduleSpec.At(Noon.AddMinutes(-1)), Noon.AddMinutes(-1), "budget"), ct);
        await store.AddAsync("acme", Due(ScheduleSpec.Daily(new TimeOnly(11, 0)), Noon.AddMinutes(-2), "limit"), ct);

        await Task.WhenAll(await worker.RunDueAsync(ct));

        var rows = await store.ListAsync("acme", ct);
        var once = rows.Single(r => r.Story == "budget");
        once.LastStatus.ShouldBe("Skipped — budget cap reached");
        once.Enabled.ShouldBeFalse();            // a fired one-off is done
        once.NextRunAtUtc.ShouldBeNull();
        rows.Single(r => r.Story == "limit").LastStatus.ShouldBe("Skipped — run limit reached");
    }
}
