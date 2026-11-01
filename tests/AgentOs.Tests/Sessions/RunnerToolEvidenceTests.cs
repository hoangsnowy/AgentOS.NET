// Governance of CLI runs on a member's computer, server half: the runner's ReportToolCalls batches cross the SignalR
// wire intact, the broker accepts them only from the connection the run was dispatched to (a stopped run's last batch
// within a grace window), sanitises them again, and every call — refusals included — becomes an evidence row under the
// run's tenant, task, agent role and runner. Another tenant never sees them, and can't write into them.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Tools;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Tools.Evidence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using RunnerSide = AgentOs.RemoteAgent.RunnerToolEvent;
using ServerSide = AgentOs.Modules.RemoteAgent.RunnerToolEvent;

namespace AgentOs.Tests.Sessions;

public sealed class RunnerToolEvidenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wire_RunnerBatch_RoundTripsThroughTheHubProtocolIntoServerTypes()
    {
        var protocol = new JsonHubProtocol();
        var sent = new RunnerSide[]
        {
            new("toolu_01A", "Bash", "git clone https://github.com/o/r.git .", true, false, T0, 1500),
            new("toolu_03C", "Bash", "git push --force origin main", false, true, T0.AddSeconds(3), 10),
        };
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("ReportToolCalls", ["req-1", "scoped", sent]), writer);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        protocol.TryParseMessage(ref buffer, new Binder(), out var message).ShouldBeTrue();

        var invocation = message.ShouldBeOfType<InvocationMessage>();
        invocation.Target.ShouldBe("ReportToolCalls");
        invocation.Arguments[0].ShouldBe("req-1");
        invocation.Arguments[1].ShouldBe("scoped");
        var received = invocation.Arguments[2].ShouldBeAssignableTo<IReadOnlyList<ServerSide>>().ShouldNotBeNull();
        received.ShouldBe(
        [
            new ServerSide("toolu_01A", "Bash", "git clone https://github.com/o/r.git .", true, false, T0, 1500),
            new ServerSide("toolu_03C", "Bash", "git push --force origin main", false, true, T0.AddSeconds(3), 10),
        ]);
    }

    [Fact]
    public async Task Hub_ReportToolCalls_RecordsEveryCallAsMemberComputerEvidence()
    {
        var (broker, runnerId) = DispatchedRun("tenant-a", "conn-a", "req-1", sessionId: "11111111-1111-1111-1111-111111111111", role: "Coding");
        var log = new InMemoryToolInvocationLog();
        var hub = Hub(broker, log, "conn-a");

        await hub.ReportToolCalls("req-1", "scoped",
        [
            new ServerSide("toolu_01A", "Bash", "dotnet build -c Release", true, false, T0, 2000),
            new ServerSide("toolu_02B", "Write", "src/Login.cs", false, false, T0.AddSeconds(3), 5),
            new ServerSide("toolu_03C", "Bash", "git push --force origin main", false, true, T0.AddSeconds(4), 0),
        ]);

        var rows = await log.ListRecentAsync("tenant-a", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer));
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(r => r.TenantId == "tenant-a" && r.RunId == "req-1" && r.SessionId == "11111111-1111-1111-1111-111111111111"
            && r.AgentRole == "Coding" && r.RunnerId == runnerId.ToString("D")
            && r.Origin == ToolEvidenceOrigins.MemberComputer && r.PolicySource == ToolEvidencePolicies.CliScopedPermissions);
        var push = rows.Single(r => r.CallId == "toolu_03C");
        push.Denied.ShouldBeTrue();
        push.IsError.ShouldBeTrue();
        push.Output.ShouldStartWith("Refused by the computer's scoped permissions");
        push.InputSummary.ShouldBe("git push --force origin main");
        var build = rows.Single(r => r.CallId == "toolu_01A");
        build.IsError.ShouldBeFalse();
        build.Duration.ShouldBe(TimeSpan.FromSeconds(2));
        rows.Single(r => r.CallId == "toolu_02B").IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_AnotherConnection_CannotReportIntoTheRun()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1");
        var log = new InMemoryToolInvocationLog();
        broker.RegisterRunner("conn-b", new RunnerConnection(Guid.NewGuid(), "tenant-b", "mallory"));

        await Hub(broker, log, "conn-b").ReportToolCalls("req-1", "scoped", [Call("toolu_x")]);

        (await log.ListRecentAsync("tenant-a")).ShouldBeEmpty();
        (await log.ListRecentAsync("tenant-b")).ShouldBeEmpty();
    }

    [Fact]
    public void Broker_UntrackedRun_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();

        broker.AcceptToolCalls("conn-a", "never-dispatched", "scoped", [Call("toolu_1")]).ShouldBeNull();
    }

    [Fact]
    public void Broker_TrackedButNotYetDispatched_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        broker.TrackRun("req-1", new RemoteRunContext("tenant-a", null, null));

        broker.AcceptToolCalls("conn-a", "req-1", "scoped", [Call("toolu_1")]).ShouldBeNull();
    }

    [Fact]
    public async Task Broker_StoppedRun_AcceptsTheLastBatchWithinTheGraceWindowOnly()
    {
        var clock = new ManualClock(T0);
        var broker = new InProcessRemoteAgentBroker(clock);
        broker.RegisterRunner("conn-a", new RunnerConnection(Guid.NewGuid(), "tenant-a", "member-1"));
        broker.TrackRun("req-1", new RemoteRunContext("tenant-a", null, null));
        using var stop = new CancellationTokenSource();
        var pending = broker.DispatchAsync(new RemoteExecRequest("req-1", "s", "u", "m"), new RunnerTarget("tenant-a", "member-1"), TimeSpan.FromMinutes(5), stop.Token);
        // The member pressed Stop: the server stops waiting, the runner kills the CLI and reports what it did.
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);

        clock.Advance(TimeSpan.FromSeconds(30));
        broker.AcceptToolCalls("conn-a", "req-1", "scoped", [Call("toolu_1")]).ShouldNotBeNull();

        clock.Advance(InProcessRemoteAgentBroker.ToolCallGrace);
        broker.AcceptToolCalls("conn-a", "req-1", "scoped", [Call("toolu_2")]).ShouldBeNull();
        broker.ToolCallTotals("req-1").ShouldBe((0, 0)); // forgotten after the window
    }

    [Fact]
    public void Broker_SanitisesWhatTheWireCarries()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1");

        var batch = broker.AcceptToolCalls("conn-a", "req-1", "who-knows",
        [
            new ServerSide("", "Bash; rm -rf /", "curl -H 'Authorization: Bearer abcdefghijklmnop' x\nsecond line", true, true, T0.AddYears(5), -4),
        ]).ShouldNotBeNull();

        var c = batch.Calls.ShouldHaveSingleItem();
        c.ToolUseId.ShouldNotBeNullOrWhiteSpace();
        c.Tool.ShouldBe("unknown");
        c.Summary.ShouldNotContain("abcdefghijklmnop");
        c.Summary.ShouldNotContain("second line");
        c.Ok.ShouldBeFalse(); // a refusal is never "ok"
        c.DurationMs.ShouldBe(0);
        c.StartedUtc.ShouldBeLessThan(T0.AddYears(1));
        batch.Permissions.ShouldBe(RunnerPermissions.Prompt);
    }

    [Fact]
    public void Broker_CapsCallsPerBatchAndPerRun_AndCountsTotals()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1");
        var many = Enumerable.Range(0, 500).Select(i => Call($"toolu_{i}", denied: i % 10 == 0)).ToList();

        broker.AcceptToolCalls("conn-a", "req-1", "scoped", many).ShouldNotBeNull().Calls.Count.ShouldBe(InProcessRemoteAgentBroker.MaxToolCallsPerBatch);
        for (var i = 0; i < 20; i++) { broker.AcceptToolCalls("conn-a", "req-1", "scoped", many); }

        broker.ToolCallTotals("req-1").ToolCalls.ShouldBe(InProcessRemoteAgentBroker.MaxToolCallsPerRun);
        broker.AcceptToolCalls("conn-a", "req-1", "scoped", many).ShouldBeNull();
    }

    [Fact]
    public async Task Evidence_IsTenantIsolated()
    {
        var log = new InMemoryToolInvocationLog();
        var a = DispatchedRun("tenant-a", "conn-a", "req-a", role: "Coding");
        var b = DispatchedRun("tenant-b", "conn-b", "req-b", role: "Testing");
        await Hub(a.Broker, log, "conn-a").ReportToolCalls("req-a", "scoped", [Call("toolu_a1"), Call("toolu_a2", denied: true)]);
        await Hub(b.Broker, log, "conn-b").ReportToolCalls("req-b", "yolo", [Call("toolu_b1")]);

        var rowsA = await log.ListRecentAsync("tenant-a", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer));
        var rowsB = await log.ListRecentAsync("tenant-b", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer));

        rowsA.Select(r => r.CallId).ShouldBe(["toolu_a2", "toolu_a1"], ignoreOrder: true);
        rowsB.ShouldHaveSingleItem().PolicySource.ShouldBe(ToolEvidencePolicies.CliSkipPermissions);
        (await log.SummarizeMemberComputerAsync("tenant-a", null)).ShouldHaveSingleItem().ShouldBe(new ToolCallsByRole("Coding", 1, 2, 1));
        (await log.SummarizeMemberComputerAsync("tenant-b", null)).ShouldHaveSingleItem().ShouldBe(new ToolCallsByRole("Testing", 1, 1, 0));
        (await log.SummarizeMemberComputerAsync("tenant-c", null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task LlmClient_TracksTheRunAndPublishesTheTotalsOnTheTaskFeed()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = Guid.NewGuid();
        broker.RegisterRunner("conn-a", new RunnerConnection(runner, "tenant-a", "member-1"));
        var log = new InMemoryToolInvocationLog();
        var feed = new CapturingFeed();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<ISessionRunFeed>(feed)
            .BuildServiceProvider();
        var hub = Hub(broker, log, "conn-a");
        // The "runner": reports two calls (one refused) for whatever it is sent, then completes.
        broker.Dispatched += d => _ = Task.Run(async () =>
        {
            await hub.ReportToolCalls(d.Request.Id, "scoped", [Call("toolu_1"), Call("toolu_2", denied: true)]);
            broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "done", null));
        });
        var client = new RemoteAgentLlmClient(broker, services, Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteAgentLlmClient>.Instance);
        var session = Guid.NewGuid();

        using (AgentOs.SharedKernel.Identity.AmbientIdentity.Push("tenant-a", "member-1", session))
        {
            await client.SendAsync(new AgentOs.Domain.Llm.LlmRequest("system", "user", "claude-sonnet-5-5") { AgentRole = "Coding" });
        }

        var rows = await log.ListRecentAsync("tenant-a", new ToolEvidenceFilter(ToolEvidenceOrigins.MemberComputer, session.ToString("D")));
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.AgentRole == "Coding" && r.RunnerId == runner.ToString("D"));
        feed.Events.ShouldContain(e => e.SessionId == session && e.Message.Contains("2 tool call(s) on the computer (1 refused", StringComparison.Ordinal));
    }

    [Fact]
    public void Broker_RunnerReconnectedMidRun_IsStillAcceptedFromItsNewConnection()
    {
        // SignalR's auto-reconnect gives the same runner a new connection id: its later batches still count.
        var (broker, runnerId) = DispatchedRun("tenant-a", "conn-a", "req-1");
        broker.RegisterRunner("conn-a2", new RunnerConnection(runnerId, "tenant-a", "member-1"));

        broker.AcceptToolCalls("conn-a2", "req-1", "scoped", [Call("toolu_after_reconnect")]).ShouldNotBeNull();
    }

    [Fact]
    public void Broker_AnotherRunnerOfTheSameTenant_IsRefused()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1");
        broker.RegisterRunner("conn-other", new RunnerConnection(Guid.NewGuid(), "tenant-a", "member-2"));

        broker.AcceptToolCalls("conn-other", "req-1", "scoped", [Call("toolu_x")]).ShouldBeNull();
    }

    [Fact]
    public void Broker_SameRunnerIdUnderAnotherTenant_IsRefused()
    {
        var (broker, runnerId) = DispatchedRun("tenant-a", "conn-a", "req-1");
        broker.RegisterRunner("conn-b", new RunnerConnection(runnerId, "tenant-b", "member-1"));

        broker.AcceptToolCalls("conn-b", "req-1", "scoped", [Call("toolu_x")]).ShouldBeNull();
    }

    [Fact]
    public void Broker_ReportedPermissions_ArePinnedByTheFirstBatch()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1");

        broker.AcceptToolCalls("conn-a", "req-1", "scoped", [Call("toolu_1")]).ShouldNotBeNull().Permissions.ShouldBe(RunnerPermissions.Scoped);
        // A later batch can't relabel the run's evidence.
        broker.AcceptToolCalls("conn-a", "req-1", "yolo", [Call("toolu_2")]).ShouldNotBeNull().Permissions.ShouldBe(RunnerPermissions.Scoped);
    }

    [Fact]
    public async Task Evidence_KeepsWhatTheServerAskedForNextToWhatTheComputerReported()
    {
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1", requestedAutonomy: "scoped");
        var log = new InMemoryToolInvocationLog();

        await Hub(broker, log, "conn-a").ReportToolCalls("req-1", "yolo", [Call("toolu_1")]);

        var row = (await log.ListRecentAsync("tenant-a")).ShouldHaveSingleItem();
        row.PolicySource.ShouldBe(ToolEvidencePolicies.CliSkipPermissions); // as the computer reported it
        row.RequestedAutonomy.ShouldBe("scoped"); // as the server asked
        row.InputSummary.ShouldBe("dotnet test");
    }

    [Theory]
    [InlineData("Bash\nrm -rf /")]
    [InlineData("Bash\u0007")]
    [InlineData("<script>")]
    [InlineData("Bash; rm")]
    public void SafeToolName_UnsafeCharacters_AreUnknown(string tool) =>
        InProcessRemoteAgentBroker.SafeToolName(tool).ShouldBe("unknown");

    [Fact]
    public void SafeToolName_OverLongName_IsCutThenChecked()
    {
        InProcessRemoteAgentBroker.SafeToolName(new string('a', 70)).ShouldBe(new string('a', 64));
        // The unsafe part inside the first 64 characters is still caught after the cut.
        InProcessRemoteAgentBroker.SafeToolName(new string('a', 10) + "\n<x>" + new string('b', 80)).ShouldBe("unknown");
        InProcessRemoteAgentBroker.SafeToolName("mcp__github__create_issue").ShouldBe("mcp__github__create_issue");
        InProcessRemoteAgentBroker.SafeToolName("  ").ShouldBe("unknown");
    }

    [Fact]
    public void Broker_RefusedCalls_AreCountedByReason()
    {
        var refused = new List<(long Count, string? Reason)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "agentos.evidence.remote_refused") { l.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags) { if (tag.Key == "reason") { reason = tag.Value as string; } }
            lock (refused) { refused.Add((value, reason)); }
        });
        listener.Start();
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-metrics");
        broker.RegisterRunner("conn-x", new RunnerConnection(Guid.NewGuid(), "tenant-x", "mallory"));

        broker.AcceptToolCalls("conn-x", "req-metrics", "scoped", [Call("toolu_1"), Call("toolu_2")]).ShouldBeNull();
        broker.AcceptToolCalls("conn-a", "req-metrics-unknown", "scoped", [Call("toolu_3")]).ShouldBeNull();
        var many = Enumerable.Range(0, InProcessRemoteAgentBroker.MaxToolCallsPerBatch + 5).Select(i => Call($"toolu_m{i}")).ToList();
        broker.AcceptToolCalls("conn-a", "req-metrics", "scoped", many).ShouldNotBeNull();

        lock (refused)
        {
            refused.ShouldContain((2L, RunnerToolRefusals.WrongConnection));
            refused.ShouldContain((1L, RunnerToolRefusals.UnknownRun));
            refused.ShouldContain((5L, RunnerToolRefusals.BatchCap));
        }
    }

    [Fact]
    public async Task Hub_PushesTheRunsTenantBeforeRecording()
    {
        // RLS stage 2: a write that happens inline (the evidence queue full) must run with app.tenant_id set.
        var (broker, _) = DispatchedRun("tenant-a", "conn-a", "req-1", sessionId: "11111111-1111-1111-1111-111111111111");
        var log = new AmbientCapturingLog();

        await Hub(broker, log, "conn-a").ReportToolCalls("req-1", "scoped", [Call("toolu_1")]);

        log.Seen.ShouldHaveSingleItem().ShouldBe(("tenant-a", (Guid?)Guid.Parse("11111111-1111-1111-1111-111111111111")));
        AgentOs.SharedKernel.Identity.AmbientIdentity.Current.ShouldBeNull(); // restored afterwards
    }

    // ── helpers ──

    private static ServerSide Call(string id, bool denied = false) =>
        new(id, "Bash", "dotnet test", !denied, denied, DateTimeOffset.UtcNow, 10);

    private static (InProcessRemoteAgentBroker Broker, Guid RunnerId) DispatchedRun(string tenant, string connection, string requestId,
        string? sessionId = null, string? role = null, string? requestedAutonomy = null)
    {
        var broker = new InProcessRemoteAgentBroker();
        var runnerId = Guid.NewGuid();
        broker.RegisterRunner(connection, new RunnerConnection(runnerId, tenant, "member-1"));
        broker.TrackRun(requestId, new RemoteRunContext(tenant, sessionId, role, requestedAutonomy));
        // Dispatch (binds the run to the connection), then let the runner answer; the run stays tracked for the grace window.
        var pending = broker.DispatchAsync(new RemoteExecRequest(requestId, "s", "u", "m"), new RunnerTarget(tenant, "member-1"), TimeSpan.FromMinutes(5));
        broker.CompleteFromServer(new RemoteExecResult(requestId, true, "ok", null));
        pending.GetAwaiter().GetResult();
        return (broker, runnerId);
    }

    private static RemoteAgentHub Hub(IRemoteAgentBroker broker, IToolInvocationLog log, string connectionId) =>
        new(broker, Substitute.For<IRunnerDirectory>(), Substitute.For<IRunnerPairingService>(), log) { Context = new CallerContext(connectionId) };

    private sealed class Binder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(string), typeof(string), typeof(IReadOnlyList<ServerSide>)];
        public Type GetReturnType(string invocationId) => typeof(object);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }

    private sealed class CallerContext(string connectionId) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class AmbientCapturingLog : IToolInvocationLog
    {
        public List<(string? Tenant, Guid? Session)> Seen { get; } = [];

        public Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
        {
            var current = AgentOs.SharedKernel.Identity.AmbientIdentity.Current;
            lock (Seen) { Seen.Add((current?.TenantId, current?.SessionId)); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(string tenantId, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolInvocationEvidence>>([]);
    }

    private sealed class CapturingFeed : ISessionRunFeed
    {
        public List<SessionRunEvent> Events { get; } = [];
        public void Publish(SessionRunEvent evt) { lock (Events) { Events.Add(evt); } }
        public IAsyncEnumerable<SessionRunEvent> Subscribe(string tenantId, CancellationToken ct) => throw new NotSupportedException();
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
