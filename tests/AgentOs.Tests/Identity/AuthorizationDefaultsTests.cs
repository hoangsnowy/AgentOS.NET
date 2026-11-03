// GAP-13 — deny by default + a pinned JWS algorithm. Unit level: the IdentityModule installs a fallback policy that
// needs an authenticated caller, and the Api's JwtBearer (and the shared pin the Web's OIDC uses) accepts RS256 only.
// The hosts' real endpoint inventory (what is anonymous, what challenges) is covered by HostSecurityTests.

using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Identity;
using AgentOs.Modules.Identity.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Identity;

public sealed class AuthorizationDefaultsTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
    {
        ["Auth:Keycloak:Authority"] = "https://kc.example.com/realms/agentic",
    }).Build();

    [Fact]
    public async Task IdentityModule_FallbackPolicy_RequiresAnAuthenticatedUser()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new IdentityModule().AddServices(services, Config());
        await using var provider = services.BuildServiceProvider();

        var fallback = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull();
        fallback.Requirements.ShouldHaveSingleItem().ShouldBeOfType<DenyAnonymousAuthorizationRequirement>();
    }

    [Fact]
    public async Task IdentityModule_NamedPolicies_StillResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new IdentityModule().AddServices(services, Config());
        await using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in new[] { "Admin", "Member", "PlatformAdmin" })
        {
            (await policies.GetPolicyAsync(name)).ShouldNotBeNull(name);
        }
    }

    [Fact]
    public void JwtBearer_ValidAlgorithms_IsRs256Only()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJwtAuth(Config());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        options.TokenValidationParameters.ValidAlgorithms.ShouldNotBeNull();
        options.TokenValidationParameters.ValidAlgorithms.ToArray().ShouldBe([SecurityAlgorithms.RsaSha256]);
        JwtAuthExtensions.ValidAlgorithms.ShouldBe([SecurityAlgorithms.RsaSha256]);
    }
}
