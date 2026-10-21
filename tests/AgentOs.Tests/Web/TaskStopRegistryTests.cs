// Board "Stop": process-wide cancellation per (tenant, task) that outlives the circuit that started the run.

using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class TaskStopRegistryTests
{
    private static readonly Guid Task1 = Guid.NewGuid();

    [Fact]
    public void Stop_RunningTask_CancelsItsToken()
    {
        var registry = new TaskStopRegistry();
        using var lease = registry.Begin("t1", Task1);

        registry.IsRunning("t1", Task1).ShouldBeTrue();
        registry.Stop("t1", Task1).ShouldBeTrue();

        lease.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Stop_OtherTenant_DoesNothing()
    {
        var registry = new TaskStopRegistry();
        using var lease = registry.Begin("t1", Task1);

        registry.Stop("t2", Task1).ShouldBeFalse();

        lease.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void Dispose_FinishedRun_IsNoLongerRunning()
    {
        var registry = new TaskStopRegistry();
        registry.Begin("t1", Task1).Dispose();

        registry.IsRunning("t1", Task1).ShouldBeFalse();
        registry.Stop("t1", Task1).ShouldBeFalse();
    }

    // Try again while the old run is still winding down: the old run is cancelled, and its late Dispose must not
    // untrack the new one.
    [Fact]
    public void Begin_Twice_CancelsTheFirstAndKeepsTrackingTheSecond()
    {
        var registry = new TaskStopRegistry();
        using var first = registry.Begin("t1", Task1);
        using var second = registry.Begin("t1", Task1);

        first.Token.IsCancellationRequested.ShouldBeTrue();
        first.Dispose(); // the old run winds down first (a second Dispose at scope end is a no-op)

        registry.IsRunning("t1", Task1).ShouldBeTrue();
        registry.Stop("t1", Task1).ShouldBeTrue();
        second.Token.IsCancellationRequested.ShouldBeTrue();
    }
}
