// AgentOs.Tests/Llm/RemoteResultOwnershipTests.cs
// GAP-06 (docs/security/threat-model.md §15): a runner result, tool-call result or progress report is accepted only
// from the connection the work was dispatched to — or, after its SignalR auto-reconnect, a connection of the SAME runner
// in the SAME tenant. Any other authenticated runner (another member, another tenant) is refused. The same owner rule
// covers ticket-file reads and files sent back (N4), so a runner that auto-reconnects mid-run still gets / gives its files.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.RemoteAgent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
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

        var task = OnFirstConnection(broker, runner, () => broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait));
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

        var task = OnFirstConnection(broker, runner, () => broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait));
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

    // The server-side completion (approval denial / delivery failure in the transport) stays trusted — and is named so.
    [Fact]
    public async Task CompleteFromServer_HasNoOwnerCheck()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner());
        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.CompleteFromServer(new RemoteExecResult("r1", false, "", "Denied by approval gate."));

        (await task).Error.ShouldBe("Denied by approval gate.");
    }

    [Fact]
    public async Task CompleteToolCallFromServer_HasNoOwnerCheck()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner());
        var task = broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait);

        broker.CompleteToolCallFromServer(new RunnerToolResult("r1", "tc1", false, "", "Denied by approval gate."));

        (await task).Error.ShouldBe("Denied by approval gate.");
    }

    // ── S3: late answers, refusal evidence, progress after a reconnect ──

    // The runner finishes after the server stopped waiting: its answer is dropped quietly (false), never an exception on
    // the hub path, and counted as kind=unknown.
    [Fact]
    public async Task Complete_LateAfterTimeout_ReturnsFalseWithoutThrowing()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner());
        var task = broker.DispatchAsync(new RemoteExecRequest("r-late", "s", "u", "m"), new RunnerTarget("t1", "m1"), TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<TimeoutException>(task);

        var accepted = true;
        Should.NotThrow(() => accepted = broker.Complete("conn-a", new RemoteExecResult("r-late", true, "too late", null)));
        accepted.ShouldBeFalse();

        var toolTask = broker.DispatchToolCallAsync(new RunnerToolCall("r-late", "tc-late", "shell", "{}"), new RunnerTarget("t1", "m1"), TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<TimeoutException>(toolTask);
        Should.NotThrow(() => accepted = broker.CompleteToolCall("conn-a", new RunnerToolResult("r-late", "tc-late", true, "too late", null)));
        accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task Refusals_AreCountedByKind_AndWrongConnectionIsLoggedAsWarning()
    {
        var refused = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "agentos.remote.result_refused") { l.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? kind = null;
            foreach (var tag in tags) { if (tag.Key == "kind") { kind = tag.Value as string; } }
            lock (refused) { for (var i = 0; i < value; i++) { refused.Add(kind); } }
        });
        listener.Start();

        var logger = new FakeLogger<InProcessRemoteAgentBroker>();
        var broker = new InProcessRemoteAgentBroker(logger: logger);
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        var request = broker.DispatchAsync(new RemoteExecRequest("r-count", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);
        var toolCall = broker.DispatchToolCallAsync(new RunnerToolCall("r-count", "tc-count", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-b", new RemoteExecResult("r-count", true, "forged", null)).ShouldBeFalse();
        broker.CompleteToolCall("conn-b", new RunnerToolResult("r-count", "tc-count", true, "forged", null)).ShouldBeFalse();
        broker.ReportProgress("conn-b", "r-count", "forged");
        broker.Complete("conn-a", new RemoteExecResult("r-nobody-waits", true, "x", null)).ShouldBeFalse();

        broker.Complete("conn-a", new RemoteExecResult("r-count", true, "ok", null)).ShouldBeTrue();
        broker.CompleteToolCall("conn-a", new RunnerToolResult("r-count", "tc-count", true, "ok", null)).ShouldBeTrue();
        await request;
        await toolCall;

        // The meter is process-wide (other tests may add to it in parallel): assert what this test caused is there.
        lock (refused)
        {
            refused.ShouldContain(RemoteResultRefusals.Request);
            refused.ShouldContain(RemoteResultRefusals.ToolCall);
            refused.ShouldContain(RemoteResultRefusals.Progress);
            refused.ShouldContain(RemoteResultRefusals.Unknown);
        }

        var records = logger.Collector.GetSnapshot();
        var warnings = records.Where(r => r.Level == LogLevel.Warning).Select(r => r.Message).ToList();
        warnings.Count.ShouldBe(3);
        warnings.ShouldContain(m => m.Contains("Runner request for r-count refused", StringComparison.Ordinal));
        warnings.ShouldContain(m => m.Contains("Runner tool_call for tc-count refused", StringComparison.Ordinal));
        warnings.ShouldContain(m => m.Contains("Runner progress for r-count refused", StringComparison.Ordinal));
        // A late / unknown id is routine: Information, not a warning.
        records.ShouldContain(r => r.Level == LogLevel.Information && r.Message.Contains("r-nobody-waits", StringComparison.Ordinal));
    }

    // The runner-supplied id is scrubbed before it reaches the log (no forged log lines).
    [Fact]
    public async Task Refusal_LogsAScrubbedId()
    {
        var logger = new FakeLogger<InProcessRemoteAgentBroker>();
        var broker = new InProcessRemoteAgentBroker(logger: logger);
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        const string forged = "r-x\nERROR fake line";
        var task = broker.DispatchAsync(new RemoteExecRequest(forged, "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);

        broker.Complete("conn-b", new RemoteExecResult(forged, true, "forged", null)).ShouldBeFalse();
        broker.CompleteFromServer(new RemoteExecResult(forged, true, "ok", null));
        await task;

        logger.Collector.LatestRecord.Message.ShouldNotContain("\n");
    }

    [Fact]
    public async Task ReportProgress_FromSameRunnerAfterReconnect_IsAccepted()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner(member: "m1");
        using var other = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        var steps = new List<string>();
        broker.Progressed += p => steps.Add(p.Step);

        var task = OnFirstConnection(broker, runner, () => broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait));
        using var second = broker.RegisterRunner("conn-new", runner);

        broker.ReportProgress("conn-new", "r1", "testing");
        broker.ReportProgress("conn-b", "r1", "forged");
        broker.Complete("conn-new", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;

        steps.ShouldBe(["testing"]);
    }

    // ── N4: ticket files and files sent back survive the runner's auto-reconnect mid-run ──

    [Fact]
    public async Task ReadRunFile_FromSameRunnerAfterReconnect_IsServed_OtherRunnerIsNot()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner(member: "m1");
        using var other = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        var content = "ticket notes"u8.ToArray();
        broker.StageFiles("r1", [new RunFile("notes.txt", content, RunFile.Hash(content), "text/plain")]);

        var task = OnFirstConnection(broker, runner, () => broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait));
        using var second = broker.RegisterRunner("conn-new", runner);

        broker.ReadRunFile("conn-b", "r1", 0, 0).ShouldBeNull();      // another member's runner, same tenant
        var chunk = broker.ReadRunFile("conn-new", "r1", 0, 0).ShouldNotBeNull();
        chunk.Data.ShouldBe(content);
        chunk.Last.ShouldBeTrue();

        broker.Complete("conn-new", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;
        broker.UnstageFiles("r1").ShouldBe(1);
    }

    [Fact]
    public async Task UploadRunFile_FromSameRunnerAfterReconnect_IsAccepted_OtherRunnerIsNot()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Runner(member: "m1");
        using var other = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        using var foreign = broker.RegisterRunner("conn-x", runner with { TenantId = "t2" });
        broker.ExpectUploads("r1", RunCollect.Screenshots).ShouldBeTrue();

        var task = OnFirstConnection(broker, runner, () => broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m", Collect: RunCollect.Screenshots), new RunnerTarget("t1", "m1"), Wait));
        using var second = broker.RegisterRunner("conn-new", runner);

        broker.UploadRunFile("conn-b", "r1", new RunUploadChunk("a.png", 0, Png(), true)).ShouldNotBeNull();
        broker.UploadRunFile("conn-x", "r1", new RunUploadChunk("a.png", 0, Png(), true)).ShouldNotBeNull(); // same id, other tenant
        broker.UploadRunFile("conn-new", "r1", new RunUploadChunk("a.png", 0, Png(), true)).ShouldBeNull();

        broker.Complete("conn-new", new RemoteExecResult("r1", true, "ok", null)).ShouldBeTrue();
        await task;
        broker.TakeUploads("r1").ShouldHaveSingleItem().Path.ShouldBe("a.png");
    }

    // ── S1: the hub passes the CALLER's connection id — the broker decides on it ──

    [Fact]
    public void Hub_PassesCallerConnectionId_ToTheBroker()
    {
        var broker = Substitute.For<IRemoteAgentBroker>();
        using var hub = Hub(broker, "conn-a");
        var result = new RemoteExecResult("r1", true, "ok", null);
        var toolResult = new RunnerToolResult("r1", "tc1", true, "ok", null);

        hub.CompleteRequest(result);
        hub.CompleteToolCall(toolResult);
        hub.ReportProgress("r1", "testing");

        broker.Received(1).Complete("conn-a", result);
        broker.Received(1).CompleteToolCall("conn-a", toolResult);
        broker.Received(1).ReportProgress("conn-a", "r1", "testing");
        broker.DidNotReceive().CompleteFromServer(Arg.Any<RemoteExecResult>());
        broker.DidNotReceive().CompleteToolCallFromServer(Arg.Any<RunnerToolResult>());
    }

    [Fact]
    public async Task Hub_ResultFromAnotherConnection_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-a", Runner(member: "m1"));
        using var b = broker.RegisterRunner("conn-b", Runner(member: "m2"));
        var steps = new List<string>();
        broker.Progressed += p => steps.Add(p.Step);
        var request = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait);
        var toolCall = broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait);

        using (var intruder = Hub(broker, "conn-b"))
        {
            intruder.CompleteRequest(new RemoteExecResult("r1", true, "forged", null));
            intruder.CompleteToolCall(new RunnerToolResult("r1", "tc1", true, "forged", null));
            intruder.ReportProgress("r1", "forged");
        }
        request.IsCompleted.ShouldBeFalse();
        toolCall.IsCompleted.ShouldBeFalse();

        using (var owner = Hub(broker, "conn-a"))
        {
            owner.ReportProgress("r1", "testing");
            owner.CompleteToolCall(new RunnerToolResult("r1", "tc1", true, "real", null));
            owner.CompleteRequest(new RemoteExecResult("r1", true, "real", null));
        }
        (await toolCall).JsonOutput.ShouldBe("real");
        (await request).Content.ShouldBe("real");
        steps.ShouldBe(["testing"]);
    }

    // Dispatch while the runner is on its first connection ("conn-old"), which then drops — as just before a SignalR
    // auto-reconnect gives the same runner a new connection id. The registration is disposed under `using`.
    private static T OnFirstConnection<T>(InProcessRemoteAgentBroker broker, RunnerConnection runner, Func<T> dispatch)
    {
        using var first = broker.RegisterRunner("conn-old", runner);
        return dispatch();
    }

    private static RemoteAgentHub Hub(IRemoteAgentBroker broker, string connectionId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        return new RemoteAgentHub(broker, Substitute.For<IRunnerDirectory>(), Substitute.For<IRunnerPairingService>()) { Context = context };
    }

    // A minimal PNG the upload rules accept (the 8-byte signature + padding).
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
}
