// Authorization surface of the tenant endpoints. The tenant `admin` role is per tenant, so anything that spans
// tenants — listing every tenant, provisioning a new one — must require the platform-admin policy. A tenant
// admin seeing the platform's tenant list was a cross-tenant leak (found in the 2026-10 enterprise review).

using System.Linq;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class TenantEndpointAuthorizationTests
{
    private static RouteEndpoint[] Endpoints()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(Substitute.For<ITenantsRepository>());
        builder.Services.AddSingleton(Substitute.For<IKeycloakAdminClient>());
        builder.Services.AddSingleton(Substitute.For<IAuditLog>());
        builder.Services.AddSingleton(Substitute.For<IEmailSender>());
        builder.Services.AddSingleton(Substitute.For<ITenantContext>());
        builder.Services.AddSingleton(Substitute.For<ITenantSignupService>());
        var app = builder.Build();
        app.MapTenantEndpoints();
        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()];
    }

    private static string?[] Policies(RouteEndpoint e) => [.. e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)];

    [Theory]
    [InlineData("TenantsList")]
    [InlineData("TenantsCreate")]
    public void CrossTenantEndpoints_RequirePlatformAdmin(string name)
    {
        var endpoint = Endpoints().Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        Policies(endpoint).ShouldContain("PlatformAdmin");
        Policies(endpoint).ShouldNotContain("Admin");
    }

    [Fact]
    public void EveryTenantScopedAdminRoute_CarriesTheTenantInItsPath()
    {
        // Routes a TENANT admin may call must be scoped by {tenantId} (checked against the caller's claim in the
        // handler); an Admin-policy route without it would act across tenants.
        var offenders = Endpoints()
            .Where(e => Policies(e).Contains("Admin"))
            .Where(e => !e.RoutePattern.RawText!.Contains("{tenantId}", System.StringComparison.Ordinal))
            .Select(e => e.RoutePattern.RawText)
            .ToArray();
        offenders.ShouldBeEmpty();
    }
}
