// GAP-17 — TenantDataService.PurgeAsync authorizes itself: only a platform admin may purge, never their own tenant, and
// a refusal happens before any database, Keycloak or audit call (the desktop's check is no longer the only one). Also a
// regression guard: no Web code falls back to the shared "default" tenant when the tenant claim is missing.

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Data;
using AgentOs.Modules.Tenants.Keycloak;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed partial class TenantPurgeAuthorizationTests
{
    private static ClaimsPrincipal User(string? tenant, params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "alice")).ToList();
        if (tenant is not null) { claims.Add(new Claim("tenant", tenant)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static (TenantDataService Service, IKeycloakAdminClient Keycloak, IAuditLog Audit) Service(string? connectionString = null)
    {
        var keycloak = Substitute.For<IKeycloakAdminClient>();
        var audit = Substitute.For<IAuditLog>();
        return (new TenantDataService(connectionString, keycloak, audit, TimeProvider.System, NullLogger<TenantDataService>.Instance), keycloak, audit);
    }

    private static ClaimsPrincipal Caller(string who) => who switch
    {
        "anonymous" => new ClaimsPrincipal(new ClaimsIdentity()),
        "member" => User("default", "member"),
        "tenant admin" => User("default", "admin"),
        "platform admin of the target tenant" => User("acme", "admin", "platform-admin"),
        _ => throw new ArgumentOutOfRangeException(nameof(who)),
    };

    [Theory]
    [InlineData("anonymous")]
    [InlineData("member")]
    [InlineData("tenant admin")]
    [InlineData("platform admin of the target tenant")]
    public async Task PurgeAsync_NotAllowed_ThrowsBeforeTouchingAnything(string who)
    {
        var caller = Caller(who);
        // A connection string that would fail if anything tried to open it — the refusal must come first.
        var (service, keycloak, audit) = Service("Host=127.0.0.1;Port=1;Timeout=1");

        await Should.ThrowAsync<TenantPurgeDeniedException>(() => service.PurgeAsync("acme", caller), who);

        keycloak.ReceivedCalls().ShouldBeEmpty();
        audit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void AuthorizePurge_PlatformAdminOfAnotherTenant_IsAllowed_AndNamedInTheAudit()
    {
        TenantDataService.AuthorizePurge("acme", User("default", "platform-admin")).ShouldBe("alice");
    }

    [Fact]
    public async Task PurgeAsync_PlatformAdmin_PassesTheCheck_ThenNeedsADatabase()
    {
        var (service, _, _) = Service(connectionString: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => service.PurgeAsync("acme", User("default", "platform-admin")));

        ex.ShouldNotBeOfType<TenantPurgeDeniedException>();
        ex.Message.ShouldContain("No database");
    }

    [Fact]
    public void WebSources_NeverFallBackToTheDefaultTenant()
    {
        var web = Path.Join(RepoRoot(), "src", "AgentOs.Web");
        var offenders = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => DefaultTenantFallback().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(web, f))
            .ToArray();

        offenders.ShouldBeEmpty("a missing tenant claim must fail closed (GAP-17) — show TenantClaim.MissingMessage or return 403");
    }

    [GeneratedRegex("""GetTenantId\(\)\s*\?\?\s*"default"|GetTenantId\(\)\s*\?\?\s*ITenantContext\.DefaultTenantId""")]
    private static partial Regex DefaultTenantFallback();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new InvalidOperationException("Repo root (AgentOs.slnx) not found.");
    }
}
