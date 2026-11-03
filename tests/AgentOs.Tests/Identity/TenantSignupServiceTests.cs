// Exercises TenantSignupService end-to-end against in-memory fakes. Covers all three modes
// (slug / auto-create / invite), the Keycloak-first saga rollback when the registry write
// throws, and — GAP-11 — invitations as single-use, email-bound, revocable rows: a second use, the wrong address, a
// revoked or an expired invitation is refused (and audited) before any account is created; a failed account creation
// gives the invitation back; a link minted before invitations were rows no longer opens.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Invitations;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class TenantSignupServiceTests
{
    private const string ValidPassword = "Sup3rSecure!Pass";
    private static readonly AuditActor Admin = new("admin-1", "10.0.0.1", "tests");

    [Fact]
    public async Task SignupAsync_SlugMode_CreatesTenantAndKeycloakUser()
    {
        var repo = new FakeRepo();
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult("kc-user-1"));
        var svc = NewService(repo, kc);

        var outcome = await svc.SignupAsync(new TenantSignupRequest(
            Username: "alice",
            Password: ValidPassword,
            Email: "alice@example.com",
            TenantId: "acme",
            TenantName: "Acme Corp",
            InviteToken: null));

        outcome.Mode.ShouldBe(SignupMode.Slug);
        outcome.TenantId.ShouldBe("acme");
        outcome.KeycloakUserId.ShouldBe("kc-user-1");
        repo.Records.ShouldContainKey("acme");
        repo.Records["acme"].Name.ShouldBe("Acme Corp");
    }

    [Fact]
    public async Task SignupAsync_AutoCreateMode_GeneratesUniqueSlugFromUsername()
    {
        var repo = new FakeRepo();
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult("kc-user-2"));
        var svc = NewService(repo, kc);

        var outcome = await svc.SignupAsync(new TenantSignupRequest(
            Username: "Alice",
            Password: ValidPassword,
            Email: null,
            TenantId: null,
            TenantName: null,
            InviteToken: null));

        outcome.Mode.ShouldBe(SignupMode.AutoCreate);
        outcome.TenantId.ShouldStartWith("alice-");
        outcome.TenantId.Length.ShouldBeLessThanOrEqualTo(32);
        repo.Records.ShouldContainKey(outcome.TenantId);
    }

    [Fact]
    public async Task SignupAsync_InviteMode_JoinsExistingTenantWithoutCreatingRow()
    {
        var (svc, kc, repo, _, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        var outcome = await svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token));

        outcome.Mode.ShouldBe(SignupMode.Invite);
        outcome.TenantId.ShouldBe("acme");
        repo.Records.Count.ShouldBe(1); // no new tenant row
        await kc.Received(1).CreateUserAsync(
            "bob", "bob@acme.test", "acme",
            Arg.Is<IReadOnlyList<string>>(r => r!.Count == 1 && r[0] == "member"),
            true, ValidPassword, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignupAsync_DbWriteFails_RollsBackKeycloakUser()
    {
        var repo = new ThrowingRepo();
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult("kc-user-x"));
        var svc = NewService(repo, kc);

        await Should.ThrowAsync<InvalidOperationException>(async () => await svc.SignupAsync(new TenantSignupRequest(
            Username: "carol",
            Password: ValidPassword,
            Email: null,
            TenantId: "acme",
            TenantName: null,
            InviteToken: null)));

        await kc.Received(1).DeleteUserAsync("kc-user-x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignupAsync_SlugAlreadyExists_RejectsBeforeTouchingKeycloak()
    {
        var repo = new FakeRepo();
        await repo.AddAsync(new TenantRecord("acme", "Acme", DateTimeOffset.UtcNow));
        var kc = Substitute.For<IKeycloakAdminClient>();
        var svc = NewService(repo, kc);

        await Should.ThrowAsync<InvalidOperationException>(async () => await svc.SignupAsync(new TenantSignupRequest(
            Username: "alice", Password: ValidPassword, Email: null,
            TenantId: "acme", TenantName: null, InviteToken: null)));

        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default);
    }

    // ── GAP-11: invitations are single-use, bound to an address, revocable, and expire ──

    [Fact]
    public async Task PreviewInvitationAsync_ValidToken_ReturnsThePendingRow()
    {
        var (svc, _, _, _, _) = Invitable();
        var minted = await svc.CreateInvitationAsync("acme", "admin", "Bob@X.test", TimeSpan.FromMinutes(5), Admin);

        var preview = await svc.PreviewInvitationAsync(minted.Token);

        preview.ShouldNotBeNull();
        preview.Id.ShouldBe(minted.Id);
        preview.TenantId.ShouldBe("acme");
        preview.Role.ShouldBe("admin");
        preview.Email.ShouldBe("Bob@X.test");
        preview.Status.ShouldBe(InvitationStatus.Pending);
        preview.CanAccept.ShouldBeTrue();
    }

    [Fact]
    public async Task PreviewInvitationAsync_TamperedOrMissingToken_ReturnsNull()
    {
        var (svc, _, _, _, _) = Invitable();
        (await svc.PreviewInvitationAsync(null)).ShouldBeNull();
        (await svc.PreviewInvitationAsync("")).ShouldBeNull();
        (await svc.PreviewInvitationAsync("not-a-token")).ShouldBeNull();
    }

    // A link minted before invitations were rows (stateless, reusable, purpose v1) no longer opens: honouring it would
    // keep the old gap open until it expired.
    [Fact]
    public async Task PreviewAndSignup_PreUpgradeStatelessToken_AreRefused()
    {
        var dp = new EphemeralDataProtectionProvider();
        var (svc, kc, _, _, _) = Invitable(dp);
        var legacy = dp.CreateProtector("AgentOs.Modules.Tenants.Invitation.v1").ToTimeLimitedDataProtector().Protect(
            JsonSerializer.Serialize(new LegacyPayload("acme", "admin", null, DateTimeOffset.UtcNow), WebJson),
            TimeSpan.FromHours(1));

        (await svc.PreviewInvitationAsync(legacy)).ShouldBeNull();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("mallory", "m@x.test", legacy)));
        error.Message.ShouldBe(InvitationRules.Describe(InvitationAcceptance.NotFound));
        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default);
    }

    // Reuse: the second sign-up with the same link is refused before Keycloak is touched, and the attempt is audited.
    [Fact]
    public async Task SignupAsync_InvitationUsedTwice_SecondIsRefused()
    {
        var (svc, kc, _, audit, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        await svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token));
        var error = await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("bob2", "bob@acme.test", invite.Token)));

        error.Message.ShouldBe(InvitationRules.Describe(InvitationAcceptance.AlreadyUsed));
        await kc.Received(1).CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Accepted);
        audit.Actions.ShouldContain(AuditActions.InvitationAccepted);
        audit.Entries.ShouldContain(e => e.Action == AuditActions.InvitationRefused && e.Target!.Contains("reason=AlreadyUsed", StringComparison.Ordinal));
    }

    // Two people racing with one link: exactly one gets an account (the claim is one conditional write).
    [Fact]
    public async Task SignupAsync_ConcurrentUsesOfOneInvitation_OnlyOneSucceeds()
    {
        var (svc, kc, _, _, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await svc.SignupAsync(Join($"bob{i}", "bob@acme.test", invite.Token));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })));

        attempts.Count(ok => ok).ShouldBe(1);
        await kc.Received(1).CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // Bound to the address: another address (or none) is refused; the invitee can still use it afterwards.
    [Theory]
    [InlineData("eve@evil.test", InvitationAcceptance.EmailMismatch)]
    [InlineData("bob@acme.test.evil", InvitationAcceptance.EmailMismatch)]
    [InlineData(null, InvitationAcceptance.EmailRequired)]
    public async Task SignupAsync_WrongOrMissingEmail_IsRefused_AndTheInvitationStaysUsable(string? email, InvitationAcceptance expected)
    {
        var (svc, kc, _, audit, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("eve", email, invite.Token)));

        error.Message.ShouldBe(InvitationRules.Describe(expected));
        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default);
        audit.Entries.ShouldContain(e => e.Action == AuditActions.InvitationRefused && e.TenantId == "acme");
        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Pending);

        // The right person, typing the address in another case and with stray spaces, still gets in.
        (await svc.SignupAsync(Join("bob", "  BOB@Acme.Test ", invite.Token))).Mode.ShouldBe(SignupMode.Invite);
    }

    [Fact]
    public async Task RevokeInvitationAsync_Pending_LinkStopsWorking_AndIsAudited()
    {
        var (svc, kc, _, audit, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        (await svc.RevokeInvitationAsync("acme", invite.Id, Admin)).ShouldBeTrue();

        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Revoked);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token)));
        error.Message.ShouldBe(InvitationRules.Describe(InvitationAcceptance.Revoked));
        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default);
        audit.Entries.ShouldContain(e => e.Action == AuditActions.InvitationRevoked && e.UserId == "admin-1" && e.IpAddress == "10.0.0.1");
        (await svc.RevokeInvitationAsync("acme", invite.Id, Admin)).ShouldBeFalse();   // already revoked
    }

    [Fact]
    public async Task RevokeInvitationAsync_UsedOrOtherTenant_ReturnsFalse()
    {
        var (svc, _, _, _, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);

        (await svc.RevokeInvitationAsync("other", invite.Id, Admin)).ShouldBeFalse();   // not this tenant's invitation
        await svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token));
        (await svc.RevokeInvitationAsync("acme", invite.Id, Admin)).ShouldBeFalse();    // used: nothing to revoke
        (await svc.ListInvitationsAsync("acme")).Single().Status.ShouldBe(InvitationStatus.Accepted);
    }

    [Fact]
    public async Task SignupAsync_ExpiredInvitation_IsRefused()
    {
        var (svc, kc, _, _, clock) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(2), Admin);

        clock.Advance(TimeSpan.FromHours(2));

        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Expired);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token)));
        error.Message.ShouldBe(InvitationRules.Describe(InvitationAcceptance.Expired));
        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default);
    }

    // Keycloak refused the account (a taken username): the invitation goes back to pending so the invitee can retry.
    [Fact]
    public async Task SignupAsync_AccountCreationFails_ReleasesTheInvitation()
    {
        var (svc, kc, _, _, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "member", "bob@acme.test", TimeSpan.FromHours(1), Admin);
        kc.CreateUserAsync("taken", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Keycloak create-user failed (409): User exists with same username"));

        await Should.ThrowAsync<InvalidOperationException>(() => svc.SignupAsync(Join("taken", "bob@acme.test", invite.Token)));
        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Pending);

        (await svc.SignupAsync(Join("bob", "bob@acme.test", invite.Token))).Mode.ShouldBe(SignupMode.Invite);
        (await svc.PreviewInvitationAsync(invite.Token))!.Status.ShouldBe(InvitationStatus.Accepted);
    }

    [Theory]
    [InlineData("owner", "bob@acme.test", 1)]
    [InlineData("member", "", 1)]
    [InlineData("member", "not-an-address", 1)]
    [InlineData("member", "bob@acme.test", 0)]
    [InlineData("member", "bob@acme.test", 169)]
    public async Task CreateInvitationAsync_BadRoleAddressOrLifetime_Throws(string role, string email, int hours)
    {
        var (svc, _, _, _, _) = Invitable();
        await Should.ThrowAsync<ArgumentException>(() =>
            svc.CreateInvitationAsync("acme", role, email, TimeSpan.FromHours(hours), Admin));
        (await svc.ListInvitationsAsync("acme")).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateInvitationAsync_IsListedAndAudited()
    {
        var (svc, _, _, audit, _) = Invitable();
        var invite = await svc.CreateInvitationAsync("acme", "admin", " carol@acme.test ", TimeSpan.FromHours(3), Admin);

        var listed = (await svc.ListInvitationsAsync("acme")).ShouldHaveSingleItem();
        listed.Id.ShouldBe(invite.Id);
        listed.Email.ShouldBe("carol@acme.test");
        listed.Role.ShouldBe("admin");
        listed.CreatedBy.ShouldBe("admin-1");
        listed.Status.ShouldBe(InvitationStatus.Pending);
        (await svc.ListInvitationsAsync("other")).ShouldBeEmpty();
        audit.Entries.ShouldContain(e => e.Action == AuditActions.InvitationMinted && e.TenantId == "acme"
            && e.Target!.Contains("email=carol@acme.test", StringComparison.Ordinal) && e.UserAgent == "tests");
    }

    // ── helpers ──

    private static TenantSignupRequest Join(string username, string? email, string token)
        => new(username, ValidPassword, email, TenantId: null, TenantName: null, InviteToken: token);

    // These tests exercise the open-signup modes, so the platform setting is on (GAP-01: it is off by default outside
    // Development, and the service fails closed without a settings registry — see OpenSignupAndAzureEndpointTests).
    private static TenantSignupService NewService(ITenantsRepository repo, IKeycloakAdminClient kc) =>
        new(repo, kc, new CapturingAuditLog(), new EphemeralDataProtectionProvider(), NullLogger<TenantSignupService>.Instance, new OpenSignupSettings());

    private static (TenantSignupService Service, IKeycloakAdminClient Kc, FakeRepo Repo, CapturingAuditLog Audit, ManualClock Clock) Invitable(
        IDataProtectionProvider? dp = null)
    {
        var repo = new FakeRepo();
        repo.Records["acme"] = new TenantRecord("acme", "Acme", DateTimeOffset.UtcNow);
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult("kc-" + ci.ArgAt<string>(0)));
        var audit = new CapturingAuditLog();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        var svc = new TenantSignupService(repo, kc, audit, dp ?? new EphemeralDataProtectionProvider(), NullLogger<TenantSignupService>.Instance,
            new OpenSignupSettings(), new InMemoryInvitationStore(), clock);
        return (svc, kc, repo, audit, clock);
    }

    // ISettings is ValueTask-returning — a hand-written fake, never substituted.
    private sealed class OpenSignupSettings : AgentOs.Domain.Configuration.ISettings
    {
        public long Version => 0;

        public ValueTask<AgentOs.Domain.Configuration.SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentOs.Domain.Configuration.SettingValue("true", AgentOs.Domain.Configuration.SettingSource.Platform));

        public ValueTask<IReadOnlyList<AgentOs.Domain.Configuration.SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentOs.Domain.Configuration.SettingView>>([]);

        public ValueTask<AgentOs.Domain.Configuration.SettingWriteResult> SetAsync(
            string key, string? value, AgentOs.Domain.Configuration.SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AgentOs.Domain.Configuration.SettingWriteResult.Success);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditEntry> _entries = new();

        public IReadOnlyList<AuditEntry> Entries => [.. _entries];

        public IReadOnlyList<string> Actions => [.. _entries.Select(e => e.Action)];

        public Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
        {
            _entries.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([.. _entries.Where(e => e.TenantId == tenantId)]);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // The pre-upgrade (v1) invitation payload: stateless, no id.
    private sealed record LegacyPayload(string TenantId, string Role, string? Email, DateTimeOffset IssuedAtUtc);

    private sealed class FakeRepo : ITenantsRepository
    {
        public Dictionary<string, TenantRecord> Records { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantRecord>>([.. Records.Values]);

        public Task<TenantRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Records.TryGetValue(id, out var r) ? r : null);

        public Task AddAsync(TenantRecord tenant, CancellationToken ct = default)
        {
            Records[tenant.Id] = tenant;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRepo : ITenantsRepository
    {
        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantRecord>>([]);

        public Task<TenantRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<TenantRecord?>(null);

        public Task AddAsync(TenantRecord tenant, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated DB failure");
    }
}
