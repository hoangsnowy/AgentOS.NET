// AgentOs.Tests/Llm/RemoteAgentTests.cs
// Unit tests for the "remote dev-IDE agent" runtime: the dispatch broker + the RemoteAgentLlmClient
// that wraps a remote reply as a zero-cost LlmResponse.
//
// M3 — dispatch is targeted (tenant + member), not broadcast. A runner registers with a RunnerConnection
// identity; a RunnerTarget resolves to exactly one connection within its tenant.

using System;
using System.Threading;
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

public class RemoteAgentTests
{
    private static RunnerConnection Conn(string tenant = "default", string member = "member-1") =>
        new(Guid.NewGuid(), tenant, member);

    private static RunnerTarget Target(string tenant = "default", string member = "member-1") =>
        new(tenant, member);

    // RemoteAgentLlmClient takes the root IServiceProvider and resolves ITenantContext per call inside a
    // fresh scope (so it can be resolved from the root provider — see LlmClientScopeValidationTests).
    // Registering the context as SCOPED here exercises that same path rather than a shortcut.
    private static IServiceProvider Tenant(string tenant = "default", string? member = "member-1")
    {
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns(tenant);
        ctx.UserId.Returns(member);
        var services = new ServiceCollection();
        services.AddScoped(_ => ctx);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Broker_DispatchThenComplete_ReturnsRunnerResult()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        RemoteDispatch? dispatched = null;
        broker.Dispatched += d => dispatched = d;

        var task = broker.DispatchAsync(new RemoteExecRequest("id1", "sys", "user", "model"), Target(), TimeSpan.FromSeconds(5));
        dispatched.ShouldNotBeNull();
        dispatched!.Request.Id.ShouldBe("id1");
        dispatched.ConnectionId.ShouldBe("conn-1");

        broker.Complete(new RemoteExecResult("id1", true, "result text", null));

        var result = await task;
        result.Ok.ShouldBeTrue();
        result.Content.ShouldBe("result text");
    }

    // Stop: the caller's token cancels the dispatch AND tells the runner to kill the work (it would run on unseen).
    [Fact]
    public async Task Broker_CallerCancels_RaisesCancelledForThatRunner()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        RemoteCancel? cancelled = null;
        broker.Cancelled += c => cancelled = c;
        using var stop = new CancellationTokenSource();

        var task = broker.DispatchAsync(new RemoteExecRequest("id-stop", "sys", "user", "model"), Target(), TimeSpan.FromMinutes(5), stop.Token);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(task);
        cancelled.ShouldBe(new RemoteCancel("id-stop", "conn-1"));
    }

    [Fact]
    public async Task Broker_Timeout_RaisesCancelled()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        RemoteCancel? cancelled = null;
        broker.Cancelled += c => cancelled = c;

        await Should.ThrowAsync<TimeoutException>(
            broker.DispatchAsync(new RemoteExecRequest("id-late", "sys", "user", "model"), Target(), TimeSpan.FromMilliseconds(50)));
        cancelled.ShouldBe(new RemoteCancel("id-late", "conn-1"));
    }

    [Fact]
    public async Task Broker_Completed_NeverRaisesCancelled()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        var cancelled = false;
        broker.Cancelled += _ => cancelled = true;
        using var stop = new CancellationTokenSource();

        var task = broker.DispatchAsync(new RemoteExecRequest("id-ok", "sys", "user", "model"), Target(), TimeSpan.FromSeconds(5), stop.Token);
        broker.Complete(new RemoteExecResult("id-ok", true, "done", null));
        (await task).Ok.ShouldBeTrue();
        await stop.CancelAsync();

        cancelled.ShouldBeFalse();
    }

    // Live progress: only for a request someone waits on, and the client hands it to that request only.
    [Fact]
    public async Task Client_RunnerReportsProgress_ReachesThatRequestOnly()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        broker.Dispatched += d =>
        {
            broker.ReportProgress(d.ConnectionId, d.Request.Id, "testing");
            broker.ReportProgress(d.ConnectionId, "someone-else", "sending");
            broker.Complete(new RemoteExecResult(d.Request.Id, true, "ok", null));
        };
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);
        var steps = new List<string>();

        await client.SendAsync(new LlmRequest("s", "u", "m") { Progress = new AgentOs.Domain.Sessions.InlineProgress<string>(steps.Add) });

        steps.ShouldBe(["testing"]);
    }

    // Metrics first: a current runner sends the CLI's real usage; the ~4-chars-per-token estimate is only a fallback.
    [Fact]
    public async Task Client_RunnerReportsUsage_UsesTheRealTokens()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        broker.Dispatched += d => broker.Complete(new RemoteExecResult(d.Request.Id, true, "ok", null)
        {
            InputTokens = 250_000, OutputTokens = 9_000, CacheReadTokens = 200_000, CacheWriteTokens = 30_000,
        });
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        var r = await client.SendAsync(new LlmRequest("s", "u", "m"));

        r.InputTokens.ShouldBe(250_000);
        r.OutputTokens.ShouldBe(9_000);
        r.CacheReadTokens.ShouldBe(200_000);
        r.CacheWriteTokens.ShouldBe(30_000);
        r.CostUsd.ShouldBe(0m); // the member's subscription — never billed on the server
    }

    [Fact]
    public async Task Client_OlderRunnerWithoutUsage_Estimates()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        broker.Dispatched += d => broker.Complete(new RemoteExecResult(d.Request.Id, true, new string('x', 400), null));
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        var r = await client.SendAsync(new LlmRequest("s", "u", "m"));

        r.OutputTokens.ShouldBe(100);
        r.CacheReadTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Broker_NoRunner_DispatchThrows()
    {
        var broker = new InProcessRemoteAgentBroker();
        broker.HasAgent.ShouldBeFalse();

        await Should.ThrowAsync<InvalidOperationException>(
            () => broker.DispatchAsync(new RemoteExecRequest("x", "s", "u", "m"), Target(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Broker_NoResponse_TimesOut()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());

        await Should.ThrowAsync<TimeoutException>(
            () => broker.DispatchAsync(new RemoteExecRequest("id", "s", "u", "m"), Target(), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Broker_RunnerDisposed_NoLongerConnected()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        broker.HasAgent.ShouldBeTrue();
        reg.Dispose();
        broker.HasAgent.ShouldBeFalse();
    }

    [Fact]
    public void Broker_HasRunnerFor_MatchesTenantAndMember()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn("tenant-a", "alice"));

        broker.HasRunnerFor(Target("tenant-a", "alice")).ShouldBeTrue();
        broker.HasRunnerFor(Target("tenant-a", "bob")).ShouldBeFalse();
    }

    [Fact]
    public void Broker_DoesNotResolveAcrossTenants()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn("tenant-a", "alice"));

        // Same member id but a different tenant must NOT resolve — closes the cross-tenant leak.
        broker.HasRunnerFor(Target("tenant-b", "alice")).ShouldBeFalse();
    }

    [Fact]
    public async Task Broker_TargetsTheMatchingMembersConnection()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var a = broker.RegisterRunner("conn-alice", Conn("tenant-a", "alice"));
        using var b = broker.RegisterRunner("conn-bob", Conn("tenant-a", "bob"));
        RemoteDispatch? dispatched = null;
        broker.Dispatched += d => dispatched = d;

        var task = broker.DispatchAsync(new RemoteExecRequest("id", "s", "u", "m"), Target("tenant-a", "bob"), TimeSpan.FromSeconds(5));
        dispatched!.ConnectionId.ShouldBe("conn-bob");

        broker.Complete(new RemoteExecResult("id", true, "ok", null));
        (await task).Ok.ShouldBeTrue();
    }

    [Fact]
    public void Broker_EmptyMember_MatchesAnyRunnerInTenant()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn("default", "operator-pseudo"));

        // Operator mode: no member id on the target → any runner in the tenant is a match.
        broker.HasRunnerFor(new RunnerTarget("default", string.Empty)).ShouldBeTrue();
    }

    [Fact]
    public async Task Client_WithRunner_ReturnsContent_AtZeroCostWithEstimatedTokens()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        // Auto-answer every dispatched request as the remote runner would.
        broker.Dispatched += d => broker.Complete(new RemoteExecResult(d.Request.Id, true, "// generated by dev IDE", null));

        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);
        var response = await client.SendAsync(new LlmRequest("sys", "build X", "claude-sonnet-4"));

        response.Content.ShouldBe("// generated by dev IDE");
        // The runner spends the member's own subscription, so the SERVER cost is genuinely $0.
        response.CostUsd.ShouldBe(0m);
        // But tokens are no longer reported as 0 — the CLI gives no usage numbers, so the client estimates
        // them (~4 chars/token) from the prompt and the reply. Real, non-zero, honest estimates.
        response.InputTokens.ShouldBeGreaterThan(0);
        response.OutputTokens.ShouldBeGreaterThan(0);
        response.Provider.ShouldBe("RemoteAgent");
    }

    [Fact]
    public async Task Client_NoRunner_ThrowsLlmException()
    {
        var broker = new InProcessRemoteAgentBroker();
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        await Should.ThrowAsync<LlmException>(
            () => client.SendAsync(new LlmRequest("sys", "build X", "model")));
    }

    [Fact]
    public async Task Client_RunnerReportsFailure_ThrowsLlmException()
    {
        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", Conn());
        broker.Dispatched += d => broker.Complete(new RemoteExecResult(d.Request.Id, false, "", "compile failed"));

        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        var ex = await Should.ThrowAsync<LlmException>(
            () => client.SendAsync(new LlmRequest("sys", "build X", "model")));
        ex.Message.ShouldContain("compile failed");
    }

    // ── Background identity (AmbientIdentity overrides the blank background ITenantContext) ──────────

    private static IRemoteAgentBroker BrokerCapturing(out Func<RunnerTarget?> target, out Func<TimeSpan?> timeout)
    {
        RunnerTarget? capturedTarget = null;
        TimeSpan? capturedTimeout = null;
        var broker = Substitute.For<IRemoteAgentBroker>();
        broker.HasRunnerFor(Arg.Do<RunnerTarget>(t => capturedTarget = t)).Returns(true);
        broker.DispatchAsync(
                Arg.Any<RemoteExecRequest>(),
                Arg.Any<RunnerTarget>(),
                Arg.Do<TimeSpan>(t => capturedTimeout = t),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new RemoteExecResult(((RemoteExecRequest)ci[0]!).Id, true, "ok", null)));
        target = () => capturedTarget;
        timeout = () => capturedTimeout;
        return broker;
    }

    [Fact]
    public async Task Client_AmbientIdentity_OverridesBlankTenantContext()
    {
        var broker = BrokerCapturing(out var target, out _);
        // ITenantContext is the blank background value (would target the wrong/"default" tenant)…
        var client = new RemoteAgentLlmClient(broker, Tenant("default", null), NullLogger<RemoteAgentLlmClient>.Instance);

        using (AmbientIdentity.Push("real-tenant", "real-member"))
        {
            await client.SendAsync(new LlmRequest("s", "u", "m"));
        }

        // …so the dispatch must target the seeded ambient tenant + member, not the blank context.
        target().ShouldNotBeNull();
        target()!.TenantId.ShouldBe("real-tenant");
        target()!.MemberUserId.ShouldBe("real-member");
    }

    [Fact]
    public async Task Client_NoAmbient_FallsBackToTenantContext()
    {
        var broker = BrokerCapturing(out var target, out _);
        var client = new RemoteAgentLlmClient(broker, Tenant("ctx-tenant", "ctx-user"), NullLogger<RemoteAgentLlmClient>.Instance);

        await client.SendAsync(new LlmRequest("s", "u", "m"));

        target()!.TenantId.ShouldBe("ctx-tenant");
        target()!.MemberUserId.ShouldBe("ctx-user");
    }

    [Fact]
    public async Task Client_ForwardsCliProfile_ToDispatch()
    {
        RemoteExecRequest? captured = null;
        var broker = Substitute.For<IRemoteAgentBroker>();
        broker.HasRunnerFor(Arg.Any<RunnerTarget>()).Returns(true);
        broker.DispatchAsync(
                Arg.Do<RemoteExecRequest>(r => captured = r),
                Arg.Any<RunnerTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new RemoteExecResult(((RemoteExecRequest)ci[0]!).Id, true, "ok", null)));
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        await client.SendAsync(new LlmRequest("s", "u", "m", Cli: "codex") { Autonomy = WorkspaceAutonomy.Scoped });

        captured.ShouldNotBeNull();
        captured!.Cli.ShouldBe("codex");
        captured.Autonomy.ShouldBe(WorkspaceAutonomy.Scoped);
    }

    [Fact]
    public async Task Client_ForwardsRequestTimeout_ElseDefault()
    {
        var broker = BrokerCapturing(out _, out var timeout);
        var client = new RemoteAgentLlmClient(broker, Tenant(), NullLogger<RemoteAgentLlmClient>.Instance);

        await client.SendAsync(new LlmRequest("s", "u", "m"));
        timeout().ShouldBe(RemoteAgentLlmClient.DispatchTimeout);

        await client.SendAsync(new LlmRequest("s", "u", "m", Timeout: TimeSpan.FromMinutes(20)));
        timeout().ShouldBe(TimeSpan.FromMinutes(20));
    }
}
