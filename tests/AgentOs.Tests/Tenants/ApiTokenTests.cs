// E1.3 — API tokens: format + salted hashing, scopes, kinds (personal / service account), the full lifecycle
// (create → validate → revoke / expire) on EF, the max-lifetime setting, who may see and revoke what, the audit trail
// (create / use / refuse / revoke), the claims a token maps to, and the method → scope rule.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.ApiTokens;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class ApiTokenTests
{
    private static readonly ApiTokenActor Admin = new("alice", "user-alice", IsAdmin: true);
    private static readonly ApiTokenActor Member = new("bob", "user-bob", IsAdmin: false);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OneSetting(string key, string value) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string k, string? tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(k == key ? new SettingValue(value, SettingSource.Platform) : default);

        public ValueTask<System.Collections.Generic.IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SettingWriteResult> SetAsync(string k, string? v, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record Harness(ApiTokenService Svc, TenantsDbContext Db, Clock Clock, IAuditLog Audit);

    private static Harness Build(ISettings? settings = null)
    {
        var db = new TenantsDbContext(new DbContextOptionsBuilder<TenantsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        var audit = Substitute.For<IAuditLog>();
        return new Harness(new ApiTokenService(db, audit, clock, settings, throttle: new ApiTokenAuditThrottle()), db, clock, audit);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiTokenRequest Request(string name = "ci-deploy", string kind = ApiTokenKind.Service, string scope = ApiTokenScopes.Run,
        int days = 30, ApiTokenActor? actor = null, string tenant = "acme")
        => new(tenant, name, kind, [scope], days, actor ?? Admin);

    // ── format + hashing ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_NewToken_HasSchemePrefixAndSecret()
    {
        var (token, prefix) = ApiTokenFormat.New();
        token.ShouldStartWith("aos_" + prefix + "_");
        prefix.Length.ShouldBe(10);
        ApiTokenFormat.PrefixOf(token).ShouldBe(prefix);
        ApiTokenFormat.New().Token.ShouldNotBe(token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.x.y")]
    [InlineData("aos_short")]
    [InlineData("aos_abcdefghij-secret")]
    public void Format_Malformed_HasNoPrefix(string? token) => ApiTokenFormat.PrefixOf(token).ShouldBeNull();

    [Fact]
    public void Hash_IsSaltedHmac_AndLegacyIsPlainSha256()
    {
        var (token, _) = ApiTokenFormat.New();
        var saltA = ApiTokenFormat.NewSalt();
        var saltB = ApiTokenFormat.NewSalt();

        saltA.ShouldNotBe(saltB);
        ApiTokenFormat.Hash(token, saltA).Length.ShouldBe(64);
        ApiTokenFormat.Hash(token, saltA).ShouldNotBe(ApiTokenFormat.Hash(token, saltB));          // same token, different rows
        ApiTokenFormat.Hash(token, saltA).ShouldBe(Convert.ToHexStringLower(
            HMACSHA256.HashData(Convert.FromHexString(saltA), Encoding.UTF8.GetBytes(token))));
        ApiTokenFormat.Hash(token, string.Empty).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        ApiTokenFormat.HashEquals(ApiTokenFormat.Hash(token, saltA), ApiTokenFormat.Hash(token, saltA)).ShouldBeTrue();
        ApiTokenFormat.HashEquals(ApiTokenFormat.Hash(token, saltA), ApiTokenFormat.Hash(token, saltB)).ShouldBeFalse();
    }

    // ── scopes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("read", "read")]
    [InlineData("run", "read run")]
    [InlineData("admin", "read run admin")]
    [InlineData(" RUN ", "read run")]
    public void Scopes_AreClosedUpwards(string requested, string expected)
        => ApiTokenScopes.Format(ApiTokenScopes.Normalize([requested])).ShouldBe(expected);

    [Fact]
    public void Scopes_RejectUnknownAndEmpty()
    {
        Should.Throw<InvalidOperationException>(() => ApiTokenScopes.Normalize(["write"]));
        Should.Throw<InvalidOperationException>(() => ApiTokenScopes.Normalize([]));
        Should.Throw<InvalidOperationException>(() => ApiTokenScopes.Normalize(["platform-admin"]));
    }

    [Fact]
    public void Scopes_MapToRole_AndLegacyRoles()
    {
        ApiTokenScopes.RoleFor(ApiTokenScopes.Normalize(["run"])).ShouldBe("member");
        ApiTokenScopes.RoleFor(ApiTokenScopes.Normalize(["admin"])).ShouldBe("admin");
        ApiTokenScopes.FromLegacyRole("member").ShouldBe("read run");
        ApiTokenScopes.FromLegacyRole("admin").ShouldBe("read run admin");
        ApiTokenScopes.Allows("read run", "run").ShouldBeTrue();
        ApiTokenScopes.Allows("read", "run").ShouldBeFalse();
        ApiTokenScopes.Allows(null, "read").ShouldBeFalse();
    }

    [Theory]
    [InlineData("GET", "read")]
    [InlineData("HEAD", "read")]
    [InlineData("OPTIONS", "read")]
    [InlineData("POST", "run")]
    [InlineData("PUT", "run")]
    [InlineData("PATCH", "run")]
    [InlineData("DELETE", "run")]
    public void ScopeMiddleware_MapsMethodToScope(string method, string scope)
        => ApiTokenScopeMiddleware.RequiredScope(method).ShouldBe(scope);

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_Create_Validate_Revoke()
    {
        var h = Build();
        var (info, token) = await h.Svc.CreateAsync(Request(), Ct);

        var row = await h.Db.ApiTokens.SingleAsync(Ct);
        row.Salt.Length.ShouldBe(32);
        row.TokenHash.ShouldBe(ApiTokenFormat.Hash(token, row.Salt));
        row.TokenHash.ShouldNotContain(token);                                                   // never stored in clear
        row.Scopes.ShouldBe("read run");
        row.Role.ShouldBe("member");

        var identity = await h.Svc.ValidateAsync(token, null, Ct);
        identity.ShouldNotBeNull();
        identity.Id.ShouldBe(info.Id);
        identity.TenantId.ShouldBe("acme");
        identity.Kind.ShouldBe(ApiTokenKind.Service);
        identity.Scopes.ShouldBe(["read", "run"]);
        identity.Role.ShouldBe("member");

        (await h.Svc.RevokeAsync("other", info.Id, Admin with { Name = "mallory" }, Ct)).ShouldBeFalse();   // another tenant can't
        (await h.Svc.RevokeAsync("acme", info.Id, Admin, Ct)).ShouldBeTrue();
        (await h.Svc.RevokeAsync("acme", info.Id, Admin, Ct)).ShouldBeFalse();                            // already revoked
        (await h.Svc.ValidateAsync(token, null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Validate_RefusesWrongSecret_AndExpired_AndRecordsLastUse()
    {
        var h = Build();
        var (_, token) = await h.Svc.CreateAsync(Request(scope: ApiTokenScopes.Admin, days: 1), Ct);
        var forged = token[..(token.LastIndexOf('_') + 1)] + "forged-secret-forged-secret-forged-secret";

        (await h.Svc.ValidateAsync(forged, null, Ct)).ShouldBeNull();
        (await h.Svc.ValidateAsync(token, null, Ct)).ShouldNotBeNull();
        (await h.Db.ApiTokens.SingleAsync(Ct)).LastUsedAtUtc.ShouldBe(h.Clock.Now);

        h.Clock.Now = h.Clock.Now.AddDays(2);
        (await h.Svc.ValidateAsync(token, null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Validate_LegacyUnsaltedRow_StillWorks_WithScopesFromRole()
    {
        var h = Build();
        var (token, prefix) = ApiTokenFormat.New();
        h.Db.ApiTokens.Add(new ApiTokenEntity
        {
            Id = Guid.NewGuid(), TenantId = "acme", Name = "old", Prefix = prefix, Salt = string.Empty,
            TokenHash = ApiTokenFormat.Hash(token, string.Empty), Role = "admin", Scopes = string.Empty, Kind = ApiTokenKind.Service,
            CreatedBy = "alice", CreatedAtUtc = h.Clock.Now, ExpiresAtUtc = h.Clock.Now.AddDays(10),
        });
        await h.Db.SaveChangesAsync(Ct);

        var identity = await h.Svc.ValidateAsync(token, null, Ct);
        identity.ShouldNotBeNull();
        identity.Scopes.ShouldBe(["read", "run", "admin"]);
        identity.Role.ShouldBe("admin");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public async Task Create_RefusesExpiryOutsideTheDefaultCap(int days)
    {
        var h = Build();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Request(days: days), Ct));
    }

    [Fact]
    public async Task Create_HonoursTheMaxLifetimeSetting()
    {
        var h = Build(new OneSetting(ApiTokenFormat.MaxLifetimeKey, "30"));

        (await h.Svc.MaxLifetimeDaysAsync("acme", Ct)).ShouldBe(30);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Request(days: 31), Ct));
        ex.Message.ShouldContain("30");
        (await h.Svc.CreateAsync(Request(days: 30), Ct)).Info.ExpiresAtUtc.ShouldBe(h.Clock.Now.AddDays(30));
    }

    [Fact]
    public void MaxLifetime_IsAPlatformSetting()
    {
        var descriptor = new AgentOs.Modules.Tenants.Settings.TenantsSettings(openSignupDefault: false).Describe()
            .Single(d => d.Key == ApiTokenFormat.MaxLifetimeKey);
        descriptor.Scope.ShouldBe(SettingScope.Platform);
        descriptor.Kind.ShouldBe(SettingKind.WholeNumber);
        descriptor.DefaultValue.ShouldBe("366");
        descriptor.Min.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RefusesBlankName(string name)
    {
        var h = Build();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Request(name: name), Ct));
    }

    [Fact]
    public async Task Create_MemberCannotCreateServiceAccountsOrAdminScope()
    {
        var h = Build();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Request(actor: Member), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            h.Svc.CreateAsync(Request(kind: ApiTokenKind.Personal, scope: ApiTokenScopes.Admin, actor: Member), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Request(kind: "robot"), Ct));

        var (info, _) = await h.Svc.CreateAsync(Request(kind: ApiTokenKind.Personal, scope: ApiTokenScopes.Run, actor: Member), Ct);
        info.Kind.ShouldBe(ApiTokenKind.Personal);
        info.OwnerUserId.ShouldBe("user-bob");
    }

    [Fact]
    public async Task Create_PersonalTokenNeedsAUser()
    {
        var h = Build();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            h.Svc.CreateAsync(Request(kind: ApiTokenKind.Personal, actor: new ApiTokenActor("ghost", null, true)), Ct));
    }

    [Fact]
    public async Task List_AdminSeesTheTenant_MemberSeesOwnPersonalTokens_NewestFirst()
    {
        var h = Build();
        await h.Svc.CreateAsync(Request(name: "ci"), Ct);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.Svc.CreateAsync(Request(name: "bob-script", kind: ApiTokenKind.Personal, scope: ApiTokenScopes.Read, actor: Member), Ct);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.Svc.CreateAsync(Request(name: "alice-pat", kind: ApiTokenKind.Personal), Ct);
        await h.Svc.CreateAsync(Request(name: "theirs", tenant: "other"), Ct);

        (await h.Svc.ListAsync("acme", Admin, Ct)).Select(t => t.Name).ShouldBe(["alice-pat", "bob-script", "ci"]);
        (await h.Svc.ListAsync("acme", Member, Ct)).Select(t => t.Name).ShouldBe(["bob-script"]);
        (await h.Svc.ListAsync("acme", new ApiTokenActor("anon", null, false), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoke_MemberMayRevokeOnlyOwnTokens()
    {
        var h = Build();
        var (service, _) = await h.Svc.CreateAsync(Request(name: "ci"), Ct);
        var (alices, _) = await h.Svc.CreateAsync(Request(name: "alice-pat", kind: ApiTokenKind.Personal), Ct);
        var (bobs, _) = await h.Svc.CreateAsync(Request(name: "bob-pat", kind: ApiTokenKind.Personal, actor: Member), Ct);

        (await h.Svc.RevokeAsync("acme", service.Id, Member, Ct)).ShouldBeFalse();
        (await h.Svc.RevokeAsync("acme", alices.Id, Member, Ct)).ShouldBeFalse();
        (await h.Svc.RevokeAsync("acme", bobs.Id, Member, Ct)).ShouldBeTrue();
        (await h.Svc.RevokeAsync("acme", alices.Id, Admin, Ct)).ShouldBeTrue();      // an admin may revoke anyone's
    }

    [Fact]
    public async Task RevokeForOwner_RevokesOnlyThatMembersPersonalTokens()
    {
        var h = Build();
        var (_, bobToken) = await h.Svc.CreateAsync(Request(name: "bob-1", kind: ApiTokenKind.Personal, actor: Member), Ct);
        await h.Svc.CreateAsync(Request(name: "bob-2", kind: ApiTokenKind.Personal, actor: Member), Ct);
        var (_, ciToken) = await h.Svc.CreateAsync(Request(name: "ci"), Ct);
        var (_, aliceToken) = await h.Svc.CreateAsync(Request(name: "alice-pat", kind: ApiTokenKind.Personal), Ct);

        (await h.Svc.RevokeForOwnerAsync("acme", "user-bob", "alice", Ct)).ShouldBe(2);
        (await h.Svc.RevokeForOwnerAsync("acme", "user-bob", "alice", Ct)).ShouldBe(0);

        (await h.Svc.ValidateAsync(bobToken, null, Ct)).ShouldBeNull();
        (await h.Svc.ValidateAsync(ciToken, null, Ct)).ShouldNotBeNull();
        (await h.Svc.ValidateAsync(aliceToken, null, Ct)).ShouldNotBeNull();
    }

    // ── audit ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_RecordsCreate_Use_Refusal_Revoke()
    {
        var h = Build();
        var (info, token) = await h.Svc.CreateAsync(Request(), Ct);
        await h.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditActions.ApiTokenCreated && e.TenantId == "acme"
            && e.UserId == "alice" && e.Target!.Contains("service") && e.Target.Contains("read run")), Arg.Any<CancellationToken>());

        await h.Svc.ValidateAsync(token, new ApiTokenUse("10.0.0.7", "curl/8"), Ct);
        await h.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditActions.ApiTokenUsed
            && e.IpAddress == "10.0.0.7" && e.UserAgent == "curl/8"), Arg.Any<CancellationToken>());

        // Within 5 minutes: no second use row; after: one more.
        await h.Svc.ValidateAsync(token, null, Ct);
        h.Clock.Now = h.Clock.Now.AddMinutes(6);
        await h.Svc.ValidateAsync(token, null, Ct);
        await h.Audit.Received(2).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditActions.ApiTokenUsed), Arg.Any<CancellationToken>());

        await h.Svc.RevokeAsync("acme", info.Id, Admin, Ct);
        await h.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditActions.ApiTokenRevoked), Arg.Any<CancellationToken>());

        // A revoked token presented in a loop leaves one refusal row per window, not one per request.
        for (var i = 0; i < 5; i++) { (await h.Svc.ValidateAsync(token, null, Ct)).ShouldBeNull(); }
        await h.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditActions.ApiTokenRefused
            && e.Target!.Contains("revoked")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Audit_UnknownPrefixWritesNothing()
    {
        var h = Build();
        (await h.Svc.ValidateAsync("aos_abcdefghij_" + new string('x', 43), null, Ct)).ShouldBeNull();
        await h.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact]
    public void Throttle_RecordsOncePerWindow()
    {
        var throttle = new ApiTokenAuditThrottle();
        var id = Guid.NewGuid();
        var t0 = DateTimeOffset.UnixEpoch;
        throttle.ShouldRecord(id, t0, TimeSpan.FromMinutes(5)).ShouldBeTrue();
        throttle.ShouldRecord(id, t0.AddMinutes(4), TimeSpan.FromMinutes(5)).ShouldBeFalse();
        throttle.ShouldRecord(Guid.NewGuid(), t0, TimeSpan.FromMinutes(5)).ShouldBeTrue();
        throttle.ShouldRecord(id, t0.AddMinutes(5), TimeSpan.FromMinutes(5)).ShouldBeTrue();
    }

    // ── principal ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Claims_PersonalTokenActsAsItsOwner()
    {
        var id = Guid.NewGuid();
        var claims = ApiTokenAuthentication.ClaimsFor(new ApiTokenIdentity(id, "acme", "laptop", ApiTokenKind.Personal,
            ["read", "run"], "user-bob", "bob", DateTimeOffset.MaxValue));

        claims.Single(c => c.Type == ClaimNames.Subject).Value.ShouldBe("user-bob");
        claims.Single(c => c.Type == ClaimNames.Tenant).Value.ShouldBe("acme");
        claims.Single(c => c.Type == "preferred_username").Value.ShouldBe("bob");
        claims.Single(c => c.Type == System.Security.Claims.ClaimTypes.Role).Value.ShouldBe("member");
        claims.Single(c => c.Type == ApiTokenScopes.ClaimType).Value.ShouldBe("read run");
        claims.Single(c => c.Type == ApiTokenAuthentication.TokenIdClaim).Value.ShouldBe(id.ToString());
    }

    [Fact]
    public void Claims_ServiceAccountIsItsOwnPrincipal_AndNeverPlatformAdmin()
    {
        var id = Guid.NewGuid();
        var claims = ApiTokenAuthentication.ClaimsFor(new ApiTokenIdentity(id, "acme", "ci", ApiTokenKind.Service,
            ["read", "run", "admin"], null, "alice", DateTimeOffset.MaxValue));

        claims.Single(c => c.Type == ClaimNames.Subject).Value.ShouldBe($"apitoken:{id}");
        claims.Single(c => c.Type == "preferred_username").Value.ShouldBe("service:ci");
        claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ShouldBe(["admin"]);
    }

    // GAP-02 limiter: a token principal lands in the same buckets a Keycloak JWT for the same caller would — its tenant,
    // and (personal token) its owner's user bucket, so a token can't buy a second allowance; a service account is its
    // own user.
    [Fact]
    public void RateLimitKeys_TokenPrincipals_PartitionLikeJwtCallers()
    {
        var limits = new AgentOs.ServiceDefaults.RequestRateLimits(Tenant: 600, User: 240, Anonymous: 120);
        static DefaultHttpContext As(System.Collections.Generic.IEnumerable<System.Security.Claims.Claim> claims, string scheme)
            => new() { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, scheme)) };
        var jwt = As([new(ClaimNames.Tenant, "acme"), new(ClaimNames.Subject, "user-bob")], "Bearer");
        var id = Guid.NewGuid();
        var personal = As(ApiTokenAuthentication.ClaimsFor(new ApiTokenIdentity(id, "acme", "laptop", ApiTokenKind.Personal,
            ["read"], "user-bob", "bob", DateTimeOffset.MaxValue)), ApiTokenAuthentication.Scheme);
        var service = As(ApiTokenAuthentication.ClaimsFor(new ApiTokenIdentity(id, "acme", "ci", ApiTokenKind.Service,
            ["read", "run"], null, "alice", DateTimeOffset.MaxValue)), ApiTokenAuthentication.Scheme);

        AgentOs.ServiceDefaults.RequestRateLimitPolicy.TenantKey(personal, limits)
            .ShouldBe(AgentOs.ServiceDefaults.RequestRateLimitPolicy.TenantKey(jwt, limits));
        AgentOs.ServiceDefaults.RequestRateLimitPolicy.UserKey(personal, limits)
            .ShouldBe(AgentOs.ServiceDefaults.RequestRateLimitPolicy.UserKey(jwt, limits));
        AgentOs.ServiceDefaults.RequestRateLimitPolicy.TenantKey(service, limits).ShouldBe("t|600|acme");
        AgentOs.ServiceDefaults.RequestRateLimitPolicy.UserKey(service, limits).ShouldBe($"u|240|acme|apitoken:{id}");
    }

    [Fact]
    public void ForwardSelector_RoutesOnlyApiTokens()
    {
        var withToken = new DefaultHttpContext();
        withToken.Request.Headers.Authorization = "Bearer aos_abcdefghij_secret";
        ApiTokenAuthentication.ForwardSelector(withToken).ShouldBe(ApiTokenAuthentication.Scheme);

        var withJwt = new DefaultHttpContext();
        withJwt.Request.Headers.Authorization = "Bearer eyJhbGciOiJSUzI1NiJ9.a.b";
        ApiTokenAuthentication.ForwardSelector(withJwt).ShouldBeNull();
        ApiTokenAuthentication.ForwardSelector(new DefaultHttpContext()).ShouldBeNull();
    }
}
