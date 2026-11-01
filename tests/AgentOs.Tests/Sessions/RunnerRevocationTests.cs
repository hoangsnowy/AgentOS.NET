// GAP-04 (docs/security/threat-model.md §15): revoking a runner used to change only its row — the connection it already
// held kept taking work until it happened to reconnect. Now:
//   • the broker's Disconnect(runnerId) takes every connection of that runner out of routing, fails the work in flight on
//     it at once (plain-language reason, Cancelled raised for requests) and aborts the connections;
//   • nothing the revoked runner sends is heard any more — not even in the moment between Unregister and the abort;
//   • a handshake that read the row just before the revoke can't register it again (the broker remembers the revoke);
//   • removing a member revokes all of their runners in that tenant (RevokeForMemberAsync) and disconnects them;
//   • RunnerRevalidationService re-checks live runners against their row in ONE batched lookup per sweep (a revoke made
//     on the other host, or a row that disappeared) and disconnects them;
//   • POST /runners/{id}/revoke calls the disconnector.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Endpoints;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunnerRevocationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // ── Broker: Disconnect ──

    [Fact]
    public void Disconnect_AbortsEveryConnectionOfThatRunner_AndStopsRouting()
    {
        var broker = new InProcessRemoteAgentBroker();
        var revoked = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        var other = new RunnerConnection(Guid.NewGuid(), "t1", "m2");
        var aborted = new List<string>();
        using var a = broker.RegisterRunner("conn-a", revoked, () => aborted.Add("conn-a"));
        using var a2 = broker.RegisterRunner("conn-a2", revoked, () => aborted.Add("conn-a2"));
        using var b = broker.RegisterRunner("conn-b", other, () => aborted.Add("conn-b"));

        broker.Disconnect(revoked.RunnerId).ShouldBe(2);

        aborted.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["conn-a", "conn-a2"]);
        broker.IsConnected(revoked.RunnerId).ShouldBeFalse();
        broker.HasRunnerFor(new RunnerTarget("t1", "m1")).ShouldBeFalse();   // nothing new is dispatched to it
        broker.IsConnected(other.RunnerId).ShouldBeTrue();
        broker.ConnectedRunners.ShouldBe([other.RunnerId]);
    }

    [Fact]
    public void Disconnect_NotConnected_ReturnsZero()
        => new InProcessRemoteAgentBroker().Disconnect(Guid.NewGuid()).ShouldBe(0);

    // The transport's abort may race the connection closing on its own — Disconnect must not throw.
    [Fact]
    public void Disconnect_AbortThrowsBecauseAlreadyClosing_StillUnregisters()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        using var reg = broker.RegisterRunner("conn-a", runner, () => throw new ObjectDisposedException("connection"));

        broker.Disconnect(runner.RunnerId).ShouldBe(1);
        broker.IsConnected(runner.RunnerId).ShouldBeFalse();
    }

    [Fact]
    public async Task Disconnect_ThenDispatch_FailsFast_InsteadOfReachingTheRevokedRunner()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        using var reg = broker.RegisterRunner("conn-a", runner, () => { });
        broker.Disconnect(runner.RunnerId);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), TimeSpan.FromSeconds(5)));
    }

    // MUST-FIX 1: the caller of a request in flight learns at once, in plain words — not after the timeout with
    // "did not respond within Ns" — and the runner is told to cancel it.
    [Fact]
    public async Task Disconnect_RequestInFlight_FailsAtOnceWithPlainReason_AndRaisesCancelled()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        var cancels = new List<RemoteCancel>();
        broker.Cancelled += cancels.Add;
        using var reg = broker.RegisterRunner("conn-a", runner, () => { });
        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait,
            TestContext.Current.CancellationToken);

        broker.Disconnect(runner.RunnerId);

        var ex = await Should.ThrowAsync<RunnerRevokedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("The computer was disconnected because its pairing was revoked.");
        cancels.ShouldBe([new RemoteCancel("r1", "conn-a")]);
    }

    [Fact]
    public async Task Disconnect_ToolCallInFlight_FailsAtOnce_OtherRunnersWorkIsUntouched()
    {
        var broker = new InProcessRemoteAgentBroker();
        var revoked = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        var other = new RunnerConnection(Guid.NewGuid(), "t1", "m2");
        using var a = broker.RegisterRunner("conn-a", revoked, () => { });
        using var b = broker.RegisterRunner("conn-b", other, () => { });
        var revokedCall = broker.DispatchToolCallAsync(new RunnerToolCall("r1", "tc1", "shell", "{}"), new RunnerTarget("t1", "m1"), Wait,
            TestContext.Current.CancellationToken);
        var otherCall = broker.DispatchToolCallAsync(new RunnerToolCall("r2", "tc2", "shell", "{}"), new RunnerTarget("t1", "m2"), Wait,
            TestContext.Current.CancellationToken);

        broker.Disconnect(revoked.RunnerId);

        await Should.ThrowAsync<RunnerRevokedException>(() => revokedCall.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        otherCall.IsCompleted.ShouldBeFalse();
        broker.CompleteToolCall("conn-b", new RunnerToolResult("r2", "tc2", true, "ok", null)).ShouldBeTrue();
        (await otherCall).Ok.ShouldBeTrue();
    }

    // MUST-FIX 1: a result that arrives after the connection was taken out of routing but before the transport has
    // aborted it is refused (the abort callback is exactly that moment).
    [Fact]
    public async Task Disconnect_ResultArrivingBetweenUnregisterAndAbort_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        bool? acceptedDuringAbort = null;
        using var reg = broker.RegisterRunner("conn-a", runner,
            () => acceptedDuringAbort = broker.Complete("conn-a", new RemoteExecResult("r1", true, "late forged answer", null)));
        var task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait,
            TestContext.Current.CancellationToken);

        broker.Disconnect(runner.RunnerId);

        acceptedDuringAbort.ShouldBe(false);
        await Should.ThrowAsync<RunnerRevokedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Complete_FromAConnectionThatIsNoLongerRegistered_IsRefused()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        Task<RemoteExecResult> task;
        using (broker.RegisterRunner("conn-a", runner))
        {
            task = broker.DispatchAsync(new RemoteExecRequest("r1", "s", "u", "m"), new RunnerTarget("t1", "m1"), Wait,
                TestContext.Current.CancellationToken);
        }

        broker.Complete("conn-a", new RemoteExecResult("r1", true, "from a closed connection", null)).ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();
        broker.CompleteFromServer(new RemoteExecResult("r1", false, "", "cleanup"));
    }

    // A handshake that read the row as Paired just before the revoke must not put the runner back into routing.
    [Fact]
    public void RegisterRunner_AfterDisconnect_IsRefusedAndAborted()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runner = new RunnerConnection(Guid.NewGuid(), "t1", "m1");
        broker.Disconnect(runner.RunnerId);
        var aborted = false;

        using var reg = broker.RegisterRunner("conn-late", runner, () => aborted = true);

        aborted.ShouldBeTrue();
        broker.IsRevoked(runner.RunnerId).ShouldBeTrue();
        broker.IsConnected(runner.RunnerId).ShouldBeFalse();
        broker.HasRunnerFor(new RunnerTarget("t1", "m1")).ShouldBeFalse();
    }

    [Fact]
    public void IsRevoked_IsForgottenAfterTheMemoryWindow()
    {
        var time = new ManualTime();
        var broker = new InProcessRemoteAgentBroker(time);
        var runner = Guid.NewGuid();
        broker.Disconnect(runner);
        broker.IsRevoked(runner).ShouldBeTrue();

        time.Advance(InProcessRemoteAgentBroker.RevokedMemory);

        broker.IsRevoked(runner).ShouldBeFalse();   // the revoked row refuses every handshake on its own by then
    }

    [Fact]
    public void DisconnectTenant_CutsOffOnlyThatTenantsRunners()
    {
        var broker = new InProcessRemoteAgentBroker();
        var purged1 = new RunnerConnection(Guid.NewGuid(), "purged", "m1");
        var purged2 = new RunnerConnection(Guid.NewGuid(), "purged", "m2");
        var kept = new RunnerConnection(Guid.NewGuid(), "kept", "m1");
        using var a = broker.RegisterRunner("conn-a", purged1, () => { });
        using var a2 = broker.RegisterRunner("conn-a2", purged1, () => { });
        using var b = broker.RegisterRunner("conn-b", purged2, () => { });
        using var c = broker.RegisterRunner("conn-c", kept, () => { });

        broker.DisconnectTenant("purged").ShouldBe(3);

        broker.ConnectedRunners.ShouldBe([kept.RunnerId]);
        broker.HasRunnerFor(new RunnerTarget("purged", "")).ShouldBeFalse();
    }

    // ── Hub: a revoked runner can't come back ──

    [Fact]
    public async Task OnConnectedAsync_RevokedRunnerRow_IsAbortedAndNeverRegistered()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runnerId = Guid.NewGuid();
        var directory = new FakeDirectory { Rows = { [runnerId] = new RunnerIdentity(runnerId, "t1", "m1", "hash", "Revoked") } };
        var (hub, context) = Hub(broker, directory, runnerId, "conn-reconnect");

        await hub.OnConnectedAsync();

        context.Received(1).Abort();
        broker.IsConnected(runnerId).ShouldBeFalse();
        directory.MarkedPaired.ShouldBeEmpty();
    }

    // The row still read "Paired" (the revoke happened on this instance a moment later, or its write is not visible
    // yet), but the broker remembers the revoke: the reconnect is refused all the same.
    [Fact]
    public async Task OnConnectedAsync_RunnerRevokedInMemory_IsAbortedEvenIfItsRowStillReadsPaired()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runnerId = Guid.NewGuid();
        var directory = new FakeDirectory { Rows = { [runnerId] = new RunnerIdentity(runnerId, "t1", "m1", "hash", "Paired") } };
        broker.Disconnect(runnerId);
        var (hub, context) = Hub(broker, directory, runnerId, "conn-reconnect");

        await hub.OnConnectedAsync();

        context.Received(1).Abort();
        broker.IsConnected(runnerId).ShouldBeFalse();
        directory.MarkedPaired.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnConnectedAsync_PairedRunner_IsRegistered()
    {
        var broker = new InProcessRemoteAgentBroker();
        var runnerId = Guid.NewGuid();
        var directory = new FakeDirectory { Rows = { [runnerId] = new RunnerIdentity(runnerId, "t1", "m1", "hash", "Paired") } };
        var (hub, context) = Hub(broker, directory, runnerId, "conn-ok");

        await hub.OnConnectedAsync();

        context.DidNotReceive().Abort();
        broker.IsConnected(runnerId).ShouldBeTrue();
        directory.MarkedPaired.ShouldBe([runnerId]);
        await hub.OnDisconnectedAsync(null);
        broker.IsConnected(runnerId).ShouldBeFalse();
    }

    // ── Api: POST /runners/{id}/revoke ──

    [Fact]
    public async Task RevokeRunnerAsync_OwnRunner_RevokesTheRowAndDisconnectsIt()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<IRunnerRepository>();
        repo.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new RunnerEntity { Id = id, TenantId = "t1", OwnerUserId = "m1", Status = "Paired" });
        repo.SetStatusAsync(id, "Revoked", Arg.Any<CancellationToken>()).Returns(true);
        var disconnector = new FakeDisconnector();

        var result = await SessionEndpoints.RevokeRunnerAsync(id, repo, new Tenant("t1", "m1"), new ClaimsPrincipal(new ClaimsIdentity()),
            TestContext.Current.CancellationToken, disconnector);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        disconnector.Disconnected.ShouldBe([id]);
    }

    [Fact]
    public async Task RevokeRunnerAsync_SomeoneElsesRunner_IsForbidden_AndNothingIsDisconnected()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<IRunnerRepository>();
        repo.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new RunnerEntity { Id = id, TenantId = "t1", OwnerUserId = "someone-else" });
        var disconnector = new FakeDisconnector();

        var result = await SessionEndpoints.RevokeRunnerAsync(id, repo, new Tenant("t1", "m1"), new ClaimsPrincipal(new ClaimsIdentity()),
            TestContext.Current.CancellationToken, disconnector);

        result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        disconnector.Disconnected.ShouldBeEmpty();
        await repo.DidNotReceive().SetStatusAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeRunnerAsync_NoTransportOnThisHost_StillRevokes()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<IRunnerRepository>();
        repo.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new RunnerEntity { Id = id, TenantId = "t1", OwnerUserId = "m1" });
        repo.SetStatusAsync(id, "Revoked", Arg.Any<CancellationToken>()).Returns(true);

        var result = await SessionEndpoints.RevokeRunnerAsync(id, repo, new Tenant("t1", "m1"), new ClaimsPrincipal(new ClaimsIdentity()),
            TestContext.Current.CancellationToken);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status204NoContent);
    }

    // ── Member removal ──

    [Fact]
    public async Task RevokeForMemberAsync_RevokesOnlyThatMembersRunnersInThatTenant()
    {
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"runner-revoke-{Guid.NewGuid()}").Options;
        var mine1 = Guid.NewGuid();
        var mine2 = Guid.NewGuid();
        var alreadyRevoked = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await using (var db = new SessionsDbContext(options, new Tenant("t1")))
        {
            db.Runners.AddRange(
                Row(mine1, "t1", "leaver", "Paired"),
                Row(mine2, "t1", "leaver", "Pending"),
                Row(alreadyRevoked, "t1", "leaver", "Revoked"),
                Row(colleague, "t1", "stayer", "Paired"),
                Row(otherTenant, "t2", "leaver", "Paired"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        IReadOnlyList<Guid> revoked;
        await using (var db = new SessionsDbContext(options, new Tenant("t1")))
        {
            revoked = await new EfRunnerDirectory(db).RevokeForMemberAsync("t1", "leaver", TestContext.Current.CancellationToken);
        }

        revoked.OrderBy(x => x).ShouldBe(new[] { mine1, mine2 }.OrderBy(x => x));
        await using (var db = new SessionsDbContext(options, new Tenant("t1")))
        {
            var status = await db.Runners.IgnoreQueryFilters().ToDictionaryAsync(r => r.Id, r => r.Status, TestContext.Current.CancellationToken);
            status[mine1].ShouldBe("Revoked");
            status[mine2].ShouldBe("Revoked");
            status[colleague].ShouldBe("Paired");
            status[otherTenant].ShouldBe("Paired");
        }
    }

    [Fact]
    public async Task RevokeMemberAsync_DisconnectsEveryRevokedRunner()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var directory = new FakeDirectory { Revoke = [a, b] };
        var disconnector = new FakeDisconnector();

        var revoked = await RunnerRevocation.RevokeMemberAsync(directory, disconnector, "t1", "leaver", TestContext.Current.CancellationToken);

        revoked.ShouldBe([a, b]);
        disconnector.Disconnected.ShouldBe([a, b]);
        directory.RevokedFor.ShouldBe([("t1", "leaver")]);
    }

    // ── Backstop: revalidation ──

    [Fact]
    public async Task RevalidateAsync_DisconnectsRevokedAndMissingRunners_KeepsPairedOnes_InOneLookup()
    {
        var paired = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var directory = new FakeDirectory
        {
            Rows =
            {
                [paired] = new RunnerIdentity(paired, "t1", "m1", "h", "Paired"),
                [revoked] = new RunnerIdentity(revoked, "t1", "m2", "h", "Revoked"),
            },
        };
        var disconnector = new FakeDisconnector { Connected = [paired, revoked, gone] };
        using var services = new ServiceCollection().AddSingleton<IRunnerDirectory>(directory).BuildServiceProvider();
        var service = new RunnerRevalidationService(disconnector, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunnerRevalidationService>.Instance);

        (await service.RevalidateAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        disconnector.Disconnected.OrderBy(x => x).ShouldBe(new[] { revoked, gone }.OrderBy(x => x));
        directory.BatchLookups.ShouldBe(1);   // one WHERE Id IN (…) per sweep
        directory.Lookups.ShouldBe(0);        // never one query per runner
    }

    [Fact]
    public async Task RevalidateAsync_NothingConnected_DoesNotTouchTheDatabase()
    {
        var directory = new FakeDirectory();
        using var services = new ServiceCollection().AddSingleton<IRunnerDirectory>(directory).BuildServiceProvider();
        var service = new RunnerRevalidationService(new FakeDisconnector(), services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunnerRevalidationService>.Instance);

        (await service.RevalidateAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        directory.Lookups.ShouldBe(0);
        directory.BatchLookups.ShouldBe(0);
    }

    [Fact]
    public async Task FindStatusesAsync_ReturnsEveryExistingRowAcrossTenants_AndSkipsMissingIds()
    {
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"runner-statuses-{Guid.NewGuid()}").Options;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var missing = Guid.NewGuid();
        await using (var db = new SessionsDbContext(options, new Tenant("t1")))
        {
            db.Runners.AddRange(Row(a, "t1", "m1", "Paired"), Row(b, "t2", "m2", "Revoked"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = new SessionsDbContext(options, new Tenant("someone-else")))
        {
            var statuses = await new EfRunnerDirectory(db).FindStatusesAsync([a, b, missing], TestContext.Current.CancellationToken);

            statuses.Count.ShouldBe(2);
            statuses[a].ShouldBe("Paired");
            statuses[b].ShouldBe("Revoked");
            statuses.ContainsKey(missing).ShouldBeFalse();
        }
    }

    // ── helpers ──

    private static (RemoteAgentHub Hub, HubCallerContext Context) Hub(IRemoteAgentBroker broker, IRunnerDirectory directory, Guid runnerId, string connectionId)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-AgentOS-Runner-Id"] = runnerId.ToString();
        http.Request.Headers["X-AgentOS-Runner-Token"] = "token";
        var features = new FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        context.Features.Returns(features);
        var pairing = Substitute.For<IRunnerPairingService>();
        pairing.Verify("token", "hash").Returns(true);
        return (new RemoteAgentHub(broker, directory, pairing) { Context = context }, context);
    }

    private sealed class HttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static RunnerEntity Row(Guid id, string tenant, string owner, string status) => new()
    {
        Id = id, TenantId = tenant, OwnerUserId = owner, Label = "box", TokenHash = "h", Status = status, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class Tenant(string id, string? userId = "admin") : ITenantContext
    {
        public string TenantId => id;
        public string? UserId => userId;
        public string? UserName => userId;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;
        public bool IsAdmin => true;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class FakeDirectory : IRunnerDirectory
    {
        public Dictionary<Guid, RunnerIdentity> Rows { get; } = [];
        public List<Guid> Revoke { get; init; } = [];
        public List<(string Tenant, string User)> RevokedFor { get; } = [];
        public List<Guid> MarkedPaired { get; } = [];
        public int Lookups { get; private set; }
        public int BatchLookups { get; private set; }

        public Task<RunnerIdentity?> FindForPairingAsync(Guid runnerId, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return Task.FromResult(Rows.GetValueOrDefault(runnerId));
        }

        public Task<IReadOnlyDictionary<Guid, string>> FindStatusesAsync(IReadOnlyCollection<Guid> runnerIds, CancellationToken cancellationToken = default)
        {
            BatchLookups++;
            IReadOnlyDictionary<Guid, string> statuses = runnerIds.Where(Rows.ContainsKey).ToDictionary(id => id, id => Rows[id].Status);
            return Task.FromResult(statuses);
        }

        public Task<bool> MarkPairedAsync(Guid runnerId, CancellationToken cancellationToken = default)
        {
            MarkedPaired.Add(runnerId);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<Guid>> RevokeForMemberAsync(string tenantId, string ownerUserId, CancellationToken cancellationToken = default)
        {
            RevokedFor.Add((tenantId, ownerUserId));
            return Task.FromResult<IReadOnlyList<Guid>>(Revoke);
        }
    }

    internal sealed class FakeDisconnector : IRunnerDisconnector
    {
        public List<Guid> Connected { get; init; } = [];
        public List<Guid> Disconnected { get; } = [];
        public List<string> DisconnectedTenants { get; } = [];
        public IReadOnlyCollection<Guid> ConnectedRunners => Connected;

        public int Disconnect(Guid runnerId)
        {
            Disconnected.Add(runnerId);
            return 1;
        }

        public int DisconnectTenant(string tenantId)
        {
            DisconnectedTenants.Add(tenantId);
            return 0;
        }
    }
}
