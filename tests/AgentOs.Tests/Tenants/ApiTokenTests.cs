// E1.3 — tenant API tokens: format + hashing, and the full lifecycle (create → validate → revoke / expire) on EF.
// Only the SHA-256 is stored; a token with the right prefix but a wrong secret is refused; tokens never carry
// platform-admin; "Bearer aos_…" is forwarded to the token scheme, a Keycloak JWT is not.

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.ApiTokens;
using AgentOs.Modules.Tenants.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class ApiTokenTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ApiTokenService Svc, TenantsDbContext Db, Clock Clock) Build()
    {
        var db = new TenantsDbContext(new DbContextOptionsBuilder<TenantsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        return (new ApiTokenService(db, Substitute.For<IAuditLog>(), clock), db, clock);
    }

    [Fact]
    public void Format_NewToken_HasSchemePrefixAndSecret()
    {
        var (token, prefix) = ApiTokenFormat.New();
        token.ShouldStartWith("aos_" + prefix + "_");
        prefix.Length.ShouldBe(10);
        ApiTokenFormat.PrefixOf(token).ShouldBe(prefix);
        ApiTokenFormat.Hash(token).Length.ShouldBe(64);
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
    public async Task Lifecycle_Create_Validate_Revoke()
    {
        var (svc, db, _) = Build();
        var (info, token) = await svc.CreateAsync("acme", "ci-deploy", "member", 30, "alice", TestContext.Current.CancellationToken);

        (await db.ApiTokens.SingleAsync(TestContext.Current.CancellationToken)).TokenHash.ShouldBe(ApiTokenFormat.Hash(token));
        (await db.ApiTokens.SingleAsync(TestContext.Current.CancellationToken)).TokenHash.ShouldNotContain(token);   // never stored in clear

        var identity = await svc.ValidateAsync(token, TestContext.Current.CancellationToken);
        identity.ShouldBe(new ApiTokenIdentity(info.Id, "acme", "ci-deploy", "member"));

        (await svc.RevokeAsync("other", info.Id, "mallory", TestContext.Current.CancellationToken)).ShouldBeFalse();   // another tenant can't
        (await svc.RevokeAsync("acme", info.Id, "alice", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await svc.ValidateAsync(token, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Validate_RefusesWrongSecret_AndExpired_AndRecordsLastUse()
    {
        var (svc, db, clock) = Build();
        var (_, token) = await svc.CreateAsync("acme", "script", "admin", 1, "alice", TestContext.Current.CancellationToken);
        var forged = token[..(token.LastIndexOf('_') + 1)] + "forged-secret-forged-secret-forged-secret";

        (await svc.ValidateAsync(forged, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await svc.ValidateAsync(token, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await db.ApiTokens.SingleAsync(TestContext.Current.CancellationToken)).LastUsedAtUtc.ShouldBe(clock.Now);

        clock.Now = clock.Now.AddDays(2);
        (await svc.ValidateAsync(token, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData("platform-admin", 30)]
    [InlineData("member", 0)]
    [InlineData("member", 400)]
    public async Task Create_RefusesPlatformRoles_AndBadExpiry(string role, int days)
    {
        var (svc, _, _) = Build();
        await Should.ThrowAsync<InvalidOperationException>(() => svc.CreateAsync("acme", "x", role, days, "alice", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_IsPerTenant_NewestFirst_WithoutSecrets()
    {
        var (svc, _, clock) = Build();
        await svc.CreateAsync("acme", "first", "member", 30, "alice", TestContext.Current.CancellationToken);
        clock.Now = clock.Now.AddMinutes(1);
        await svc.CreateAsync("acme", "second", "member", 30, "alice", TestContext.Current.CancellationToken);
        await svc.CreateAsync("other", "theirs", "member", 30, "bob", TestContext.Current.CancellationToken);

        (await svc.ListAsync("acme", TestContext.Current.CancellationToken)).Select(t => t.Name).ShouldBe(["second", "first"]);
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
    }
}
