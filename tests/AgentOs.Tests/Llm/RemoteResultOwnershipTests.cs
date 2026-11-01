// AgentOs.Tests/Llm/RemoteResultOwnershipTests.cs
// GAP-06 (docs/security/threat-model.md §15): a runner result, tool-call result or progress report is accepted only
// from the connection the work was dispatched to — or, after its SignalR auto-reconnect, a connection of the SAME runner
// in the SAME tenant. Any other authenticated runner (another member, another tenant) is refused.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentOs.Modules.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public class RemoteResultOwnershipTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static RunnerConnection Runner(string tenant = "t1", string member = "m1") => new(Guid.NewGuid(), tenant, member);

    [Fact]
    public async Task Complete_FromDispatchedConnection_ResolvesRequest()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-a", Runner());

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-a", new RemoteExecResult("r1", true, "real", null)).ShouldBeTrue();
        (await task).Content.ShouldBe("real");
    }

    [Fact]
    public async Task Complete_FromOtherRunnerSameTenant_IsRefusedAndRequestStaysPending()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-b", new RemoteExecResult("r1", true, "forged", null)).ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();

        broker.Complete("conn-a", new RemoteExecResult("r1", true, "real", null)).ShouldBeTrue();
        (await task).Content.ShouldBe("real");
    }

    [Fact]
    public async Task Complete_FromRunnerInAnotherTenant_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner("t1"));
        using var b = broker.RegisterRunner("conn-x", Runner("t2"));

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-x", new RemoteExecResult("r1", false, "", "poison")).ShouldBeFalse();
        broker.Complete("", new RemoteExecResult("r1", false, "", "poison")).ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();
        broker.Complete("conn-a", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;
    }

    // Same runner, same tenant, new connection id (auto-reconnect mid-run): its answer still lands.
    [Fact]
    public async Task Complete_FromSameRunnerAfterReconnect_IsAccepted()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner();
        using var first = broker.RegisterRunner("conn-old", runner);   // disposed below to simulate the drop; idempotent

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);
        first.Dispose();
        using var second = broker.RegisterRunner("conn-new", runner);

        broker.Complete("conn-new", new RemoteExecResult("r1", true, "after reconnect", null)).ShouldBeTrue();
        (await task).Content.ShouldBe("after reconnect");
    }

    // The same runner id presented under another tenant does not count as "the same runner".
    [Fact]
    public async Task Complete_SameRunnerIdDifferentTenant_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner("t1");
        using var a = broker.RegisterRunner("conn-a", runner);
        using var b = broker.RegisterRunner("conn-b", runner with { TenantId = "t2" });

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-b", new RemoteExecResult("r1", true, "forged", null)).ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();
        broker.Complete("conn-a", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;
    }

    [Fact]
    public void Complete_UnknownRequest_ReturnsFalse()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner());
        broker.Complete("conn-a", new RemoteExecResult("nobody-waits", true, "x", null)).ShouldBeFalse();
    }

    [Fact]
    public async Task CompleteToolCall_FromOtherRunner_IsRefused_FromDispatchedConnection_Resolves()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));

        var task = broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait);

        broker.CompleteToolCall("conn-b", new RunnerToolResult("r1", "tc1", true, "forged", null)).ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();

        broker.CompleteToolCall("conn-a", new RunnerToolResult("r1", "tc1", true, "real", null)).ShouldBeTrue();
        (await task).JsonOutput.ShouldBe("real");
    }

    [Fact]
    public async Task CompleteToolCall_FromSameRunnerAfterReconnect_IsAccepted()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner();
        using var first = broker.RegisterRunner("conn-old", runner);   // disposed below to simulate the drop; idempotent

        var task = broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait);
        first.Dispose();
        using var second = broker.RegisterRunner("conn-new", runner);

        broker.CompleteToolCall("conn-new", new RunnerToolResult("r1", "tc1", true, "ok", null)).ShouldBeTrue();
        (await task).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReportProgress_FromOtherRunner_IsIgnored()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        var steps = new List<string>();
        broker.Progressed += p => steps.Add(p.Step);

        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.ReportProgress("conn-b", "r1", "forged");
        broker.ReportProgress("conn-a", "r1", "testing");
        broker.Complete("conn-a", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;

        steps.ShouldBe(["testing"]);
    }

    // The server-side completion (approval denial / delivery failure in the transport) stays trusted.
    [Fact]
    public async Task Complete_ServerSide_HasNoOwnerCheck()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner());
        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete(new RemoteExecResult("r1", false, "", "Denied by approval gate."));

        (await task).Error.ShouldBe("Denied by approval gate.");
    }
}
