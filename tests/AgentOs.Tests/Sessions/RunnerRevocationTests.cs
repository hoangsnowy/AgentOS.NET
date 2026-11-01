// GAP-04 (docs/security/threat-model.md §15): revoking a runner used to change only its row — the connection it already
// held kept taking work until it happened to reconnect. Now:
//   • the broker's Disconnect(runnerId) takes every connection of that runner out of routing and aborts it;
//   • removing a member revokes all of their runners in that tenant (RevokeForMemberAsync) and disconnects them;
//   • RunnerRevalidationService re-checks live runners against their row (a revoke made on the other host, or a row
//     that disappeared) and disconnects them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.RemoteAgent;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunnerRevocationTests
{
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
        directory.RevokedFor.ShouldBe(("t1", "leaver"));
    }

    // ── Backstop: revalidation ──

    [Fact]
    public async Task RevalidateAsync_DisconnectsRevokedAndMissingRunners_KeepsPairedOnes()
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
        var services = new ServiceCollection().AddSingleton<IRunnerDirectory>(directory).BuildServiceProvider();
        var service = new RunnerRevalidationService(disconnector, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunnerRevalidationService>.Instance);

        (await service.RevalidateAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        disconnector.Disconnected.OrderBy(x => x).ShouldBe(new[] { revoked, gone }.OrderBy(x => x));
    }

    [Fact]
    public async Task RevalidateAsync_NothingConnected_DoesNotTouchTheDatabase()
    {
        var directory = new FakeDirectory();
        var services = new ServiceCollection().AddSingleton<IRunnerDirectory>(directory).BuildServiceProvider();
        var service = new RunnerRevalidationService(new FakeDisconnector(), services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunnerRevalidationService>.Instance);

        (await service.RevalidateAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        directory.Lookups.ShouldBe(0);
    }

    // ── helpers ──

    private static RunnerEntity Row(Guid id, string tenant, string owner, string status) => new()
    {
        Id = id, TenantId = tenant, OwnerUserId = owner, Label = "box", TokenHash = "h", Status = status, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class Tenant(string id) : ITenantContext
    {
        public string TenantId => id;
        public string? UserId => "admin";
        public string? UserName => "admin";
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;
        public bool IsAdmin => true;
    }

    private sealed class FakeDirectory : IRunnerDirectory
    {
        public Dictionary<Guid, RunnerIdentity> Rows { get; } = [];
        public List<Guid> Revoke { get; init; } = [];
        public (string, string)? RevokedFor { get; private set; }
        public int Lookups { get; private set; }

        public Task<RunnerIdentity?> FindForPairingAsync(Guid runnerId, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return Task.FromResult(Rows.GetValueOrDefault(runnerId));
        }

        public Task<bool> MarkPairedAsync(Guid runnerId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> RevokeForMemberAsync(string tenantId, string ownerUserId, CancellationToken cancellationToken = default)
        {
            RevokedFor = (tenantId, ownerUserId);
            return Task.FromResult<IReadOnlyList<Guid>>(Revoke);
        }
    }

    private sealed class FakeDisconnector : IRunnerDisconnector
    {
        public List<Guid> Connected { get; init; } = [];
        public List<Guid> Disconnected { get; } = [];
        public IReadOnlyCollection<Guid> ConnectedRunners => Connected;

        public int Disconnect(Guid runnerId)
        {
            Disconnected.Add(runnerId);
            return 1;
        }
    }
}
