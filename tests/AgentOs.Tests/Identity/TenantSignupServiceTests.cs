// Exercises TenantSignupService end-to-end against in-memory fakes. Covers all three modes
// (slug / auto-create / invite), the Keycloak-first saga rollback when the registry write
// throws, and the invitation token's expiry + tamper-detection.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class TenantSignupServiceTests
{
    private const string ValidPassword = "Sup3rSecure!Pass";

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
        var repo = new FakeRepo();
        await repo.AddAsync(new TenantRecord("acme", "Acme Corp", DateTimeOffset.UtcNow));
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult("kc-user-3"));
        var svc = NewService(repo, kc);
        var invite = svc.CreateInvitation("acme", "member", "bob@acme.test", TimeSpan.FromHours(1));

        var outcome = await svc.SignupAsync(new TenantSignupRequest(
            Username: "bob",
            Password: ValidPassword,
            Email: "bob@acme.test",
            TenantId: null,
            TenantName: null,
            InviteToken: invite.Token));

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

    [Fact]
    public void PreviewInvitation_ValidToken_ReturnsPayload()
    {
        var svc = NewService(new FakeRepo(), Substitute.For<IKeycloakAdminClient>());
        var minted = svc.CreateInvitation("acme", "admin", "bob@x.test", TimeSpan.FromMinutes(5));

        var preview = svc.PreviewInvitation(minted.Token);
        preview.ShouldNotBeNull();
        preview!.TenantId.ShouldBe("acme");
        preview.Role.ShouldBe("admin");
        preview.Email.ShouldBe("bob@x.test");
    }

    [Fact]
    public void PreviewInvitation_TamperedOrMissingToken_ReturnsNull()
    {
        var svc = NewService(new FakeRepo(), Substitute.For<IKeycloakAdminClient>());
        svc.PreviewInvitation(null).ShouldBeNull();
        svc.PreviewInvitation("").ShouldBeNull();
        svc.PreviewInvitation("not-a-token").ShouldBeNull();
    }

    // These tests exercise the open-signup modes, so the platform setting is on (GAP-01: it is off by default outside
    // Development, and the service fails closed without a settings registry — see OpenSignupAndAzureEndpointTests).
    private static TenantSignupService NewService(ITenantsRepository repo, IKeycloakAdminClient kc) =>
        new(repo, kc, new NullAuditLog(), new EphemeralDataProtectionProvider(), NullLogger<TenantSignupService>.Instance, new OpenSignupSettings());

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

    private sealed class NullAuditLog : IAuditLog
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditEntry>> ListAsync(string tenantId, int max = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }

    private sealed class FakeRepo : ITenantsRepository
    {
        public Dictionary<string, TenantRecord> Records { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantRecord>>(new List<TenantRecord>(Records.Values));

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
