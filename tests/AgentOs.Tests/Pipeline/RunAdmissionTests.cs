// E0.6 — per-tenant run admission: one tenant's burst or parallel load can't starve the others. Concurrency is
// released when a lease is disposed (exactly once), the per-minute window slides, tenants are independent, the
// limits are platform-only settings, and a refused run never reaches an agent.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.Pipeline.Limits;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Pipeline;

public sealed class RunAdmissionTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void Concurrency_IsCapped_AndReleasedOnDispose()
    {
        var admission = new RunAdmission(clock: new ManualClock());
        using var a = admission.Admit("t1", maxConcurrent: 2, perMinute: 100);
        using var b = admission.Admit("t1", 2, 100);
        using var c = admission.Admit("t1", 2, 100);

        a.Admitted.ShouldBeTrue();
        b.Admitted.ShouldBeTrue();
        c.Admitted.ShouldBeFalse();
        c.Reason!.ShouldContain("2 run(s) in progress (limit 2)");

        a.Dispose();
        a.Dispose();   // idempotent: a double dispose must not free a second slot
        admission.Running("t1").ShouldBe(1);
        using var d = admission.Admit("t1", 2, 100);
        d.Admitted.ShouldBeTrue();
        using var e = admission.Admit("t1", 2, 100);
        e.Admitted.ShouldBeFalse();
    }

    [Fact]
    public void Burst_IsCapped_PerSlidingMinute()
    {
        var clock = new ManualClock();
        var admission = new RunAdmission(clock: clock);
        for (var i = 0; i < 3; i++)
        {
            admission.Admit("t1", 100, perMinute: 3).Dispose();
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        var refused = admission.Admit("t1", 100, 3);
        refused.Admitted.ShouldBeFalse();
        refused.Reason!.ShouldContain("3 runs in the last minute (limit 3)");

        clock.Advance(TimeSpan.FromSeconds(31));   // the first start is now > 60 s old
        admission.Admit("t1", 100, 3).Admitted.ShouldBeTrue();
    }

    [Fact]
    public void Tenants_AreIndependent()
    {
        var admission = new RunAdmission(clock: new ManualClock());
        admission.Admit("busy", 1, 100).Admitted.ShouldBeTrue();
        admission.Admit("busy", 1, 100).Admitted.ShouldBeFalse();
        admission.Admit("other", 1, 100).Admitted.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrency_HoldsUnderParallelAdmits()
    {
        var admission = new RunAdmission(clock: new ManualClock());
        var leases = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => admission.Admit("t1", 5, 1000))));
        leases.Count(l => l.Admitted).ShouldBe(5);
        foreach (var l in leases) { l.Dispose(); }
        admission.Running("t1").ShouldBe(0);
    }

    [Fact]
    public void Limits_ArePlatformOnlySettings()
    {
        var d = new RunLimitSettings().Describe().ToArray();
        d.Select(x => x.Key).ShouldBe([RunLimitSettings.MaxConcurrent, RunLimitSettings.PerMinute]);
        d.ShouldAllBe(x => x.Scope == SettingScope.Platform);   // a tenant can't raise its own ceiling
    }

    [Fact]
    public void Denied_EnsureAdmitted_Throws_WithTheReason()
    {
        var ex = Should.Throw<RunAdmissionDeniedException>(() => RunAdmissionLease.Deny("busy").EnsureAdmitted());
        ex.Message.ShouldBe("busy");
    }
}
