// GAP-04 — member removal order. Their paired computers are revoked BEFORE the Keycloak account is deleted, so a failure
// can never leave a departed member's runner live (the old order deleted first, then revoked with the request token: a
// failure after the delete left the runners paired and a retry got 404). Once the delete is committed the rest (a second
// revoke sweep, the member.removed audit row) runs with CancellationToken.None and never throws. Covers the shared
// MemberRemoval helper and the Api endpoint (DELETE /tenants/{t}/members/{u}); the Users app calls the same helper.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class MemberRemovalTests
{
    private readonly List<string> _steps = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly List<CancellationToken> _auditTokens = [];
    private readonly IKeycloakAdminClient _kc = Substitute.For<IKeycloakAdminClient>();
    private readonly IAuditLog _log = Substitute.For<IAuditLog>();

    public MemberRemovalTests()
    {
        _kc.GetUserTenantAsync("leaver", Arg.Any<CancellationToken>()).Returns("t1");
        _kc.DeleteUserAsync("leaver", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _steps.Add("delete");
            return Task.CompletedTask;
        });
        _log.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _audit.Add(ci.Arg<AuditEntry>());
            _auditTokens.Add(ci.Arg<CancellationToken>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RemoveAsync_RevokesComputersBeforeDeletingTheAccount_ThenAudits()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var directory = new OrderedDirectory(_steps) { FirstSweep = [a, b] };
        var disconnector = new Disconnector();

        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin", "10.0.0.1", "ua"),
            _kc, _log, directory, disconnector, ct: TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(MemberRemovalOutcome.Removed);
        result.ComputersRevoked.ShouldBe(2);
        result.Warning.ShouldBeNull();
        _steps.ShouldBe(["revoke", "delete", "revoke"]);   // the second sweep catches a computer paired in between
        disconnector.Disconnected.ShouldBe([a, b]);
        var row = _audit.ShouldHaveSingleItem();
        row.Action.ShouldBe(AuditActions.MemberRemoved);
        row.TenantId.ShouldBe("t1");
        row.UserId.ShouldBe("admin");
        row.Target.ShouldBe("user=leaver,runnersRevoked=2");
        row.IpAddress.ShouldBe("10.0.0.1");
    }

    // The account is gone once the delete returns: the audit row must be written even if the caller went away.
    [Fact]
    public async Task RemoveAsync_AfterTheDelete_UsesNoCancellation()
    {
        using var cts = new CancellationTokenSource();
        _kc.DeleteUserAsync("leaver", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();   // the request is aborted right after Keycloak committed the delete
            return Task.CompletedTask;
        });
        var directory = new OrderedDirectory(_steps);

        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin"),
            _kc, _log, directory, new Disconnector(), ct: cts.Token);

        result.Removed.ShouldBeTrue();
        directory.Tokens[^1].CanBeCanceled.ShouldBeFalse();   // the post-delete sweep
        _auditTokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveAsync_RevokeFails_NothingIsDeletedOrAudited()
    {
        var directory = new OrderedDirectory(_steps) { FailOnSweep = 1 };

        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin"),
            _kc, _log, directory, new Disconnector(), ct: TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(MemberRemovalOutcome.ComputersNotRevoked);
        result.Removed.ShouldBeFalse();
        result.Error!.ShouldContain("nothing was removed");
        await _kc.DidNotReceive().DeleteUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_DeleteFails_ComputersStayRevoked_AndTheResultSaysNotRemoved()
    {
        _kc.DeleteUserAsync("leaver", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Keycloak is down"));
        var directory = new OrderedDirectory(_steps) { FirstSweep = [Guid.NewGuid()] };

        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin"),
            _kc, _log, directory, new Disconnector(), ct: TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(MemberRemovalOutcome.AccountNotDeleted);
        result.ComputersRevoked.ShouldBe(1);
        result.Error.ShouldBe("Keycloak is down");
        _audit.ShouldBeEmpty();
    }

    // A failure after the delete must not report "Remove failed" for a member who is gone: Removed + a warning, and the
    // audit row records it.
    [Fact]
    public async Task RemoveAsync_SweepAfterTheDeleteFails_StillRemovedWithAWarning_AndAudited()
    {
        var directory = new OrderedDirectory(_steps) { FirstSweep = [Guid.NewGuid()], FailOnSweep = 2 };

        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin"),
            _kc, _log, directory, new Disconnector(), ct: TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(MemberRemovalOutcome.Removed);
        result.Warning.ShouldBe(MemberRemoval.RevokeAfterDeleteFailed);
        _audit.ShouldHaveSingleItem().Target.ShouldBe("user=leaver,runnersRevoked=1,runnerRevokeFailed=true");
    }

    [Fact]
    public async Task RemoveAsync_NoRunnerModule_RemovesAndAuditsThePlainRow()
    {
        var result = await MemberRemoval.RemoveAsync("t1", "leaver", new MemberRemovalActor("admin"),
            _kc, _log, runners: null, disconnector: null, ct: TestContext.Current.CancellationToken);

        result.Removed.ShouldBeTrue();
        _audit.ShouldHaveSingleItem().Target.ShouldBe("user=leaver");
    }

    // ── Api: DELETE /tenants/{tenantId}/members/{userId} ──

    [Fact]
    public async Task RemoveMemberEndpoint_RevokesRunners_DeletesTheUser_AndWritesTheAuditRow()
    {
        var a = Guid.NewGuid();
        var directory = new OrderedDirectory(_steps) { FirstSweep = [a] };
        var disconnector = new Disconnector();

        var result = await TenantEndpoints.RemoveMemberAsync("t1", "leaver", _kc, _log, Admin("t1"), new DefaultHttpContext(),
            TestContext.Current.CancellationToken, directory, disconnector);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        _steps.ShouldBe(["revoke", "delete", "revoke"]);
        disconnector.Disconnected.ShouldBe([a]);
        var row = _audit.ShouldHaveSingleItem();
        row.Action.ShouldBe(AuditActions.MemberRemoved);
        row.Target.ShouldBe("user=leaver,runnersRevoked=1");
    }

    [Fact]
    public async Task RemoveMemberEndpoint_RevokeFails_Returns503_AndKeepsTheUser()
    {
        var directory = new OrderedDirectory(_steps) { FailOnSweep = 1 };

        var result = await TenantEndpoints.RemoveMemberAsync("t1", "leaver", _kc, _log, Admin("t1"), new DefaultHttpContext(),
            TestContext.Current.CancellationToken, directory, new Disconnector());

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        await _kc.DidNotReceive().DeleteUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberEndpoint_DeleteFails_Returns502()
    {
        _kc.DeleteUserAsync("leaver", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Keycloak is down"));

        var result = await TenantEndpoints.RemoveMemberAsync("t1", "leaver", _kc, _log, Admin("t1"), new DefaultHttpContext(),
            TestContext.Current.CancellationToken, new OrderedDirectory(_steps), new Disconnector());

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
        _audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveMemberEndpoint_UserOfAnotherTenant_Is404_AndNothingIsRevoked()
    {
        _kc.GetUserTenantAsync("leaver", Arg.Any<CancellationToken>()).Returns("t2");
        var directory = new OrderedDirectory(_steps) { FirstSweep = [Guid.NewGuid()] };

        var result = await TenantEndpoints.RemoveMemberAsync("t1", "leaver", _kc, _log, Admin("t1"), new DefaultHttpContext(),
            TestContext.Current.CancellationToken, directory, new Disconnector());

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        _steps.ShouldBeEmpty();
    }

    private static ITenantContext Admin(string tenant)
    {
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns(tenant);
        ctx.UserId.Returns("admin");
        return ctx;
    }

    // Revokes the member's runners, recording each sweep in the shared step list; can fail on a given sweep (1-based).
    private sealed class OrderedDirectory(List<string> steps) : IRunnerDirectory
    {
        private int _sweeps;

        public List<Guid> FirstSweep { get; init; } = [];
        public int FailOnSweep { get; init; }
        public List<CancellationToken> Tokens { get; } = [];

        public Task<RunnerIdentity?> FindForPairingAsync(Guid runnerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RunnerIdentity?>(null);

        public Task<bool> MarkPairedAsync(Guid runnerId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> RevokeForMemberAsync(string tenantId, string ownerUserId, CancellationToken cancellationToken = default)
        {
            _sweeps++;
            Tokens.Add(cancellationToken);
            if (_sweeps == FailOnSweep) { throw new InvalidOperationException("database unavailable"); }
            steps.Add("revoke");
            // Idempotent like the real one: what the first sweep revoked is not revoked again.
            return Task.FromResult<IReadOnlyList<Guid>>(_sweeps == 1 ? FirstSweep : []);
        }
    }

    private sealed class Disconnector : IRunnerDisconnector
    {
        public List<Guid> Disconnected { get; } = [];
        public IReadOnlyCollection<Guid> ConnectedRunners => [];

        public int Disconnect(Guid runnerId)
        {
            Disconnected.Add(runnerId);
            return 1;
        }

        public int DisconnectTenant(string tenantId) => 0;
    }
}
