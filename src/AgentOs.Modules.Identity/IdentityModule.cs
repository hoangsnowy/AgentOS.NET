// Module entry: cross-cutting tenant context (claims-based) + Admin/Member policies. The auth
// scheme itself is host-specific — the API host calls AddJwtAuth (Keycloak bearer) and the Web
// host wires cookie + OIDC explicitly. No DbContext; tenant state lives in JWT/cookie claims.

using System;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Modularity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Identity;

public sealed class IdentityModule : IModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
            options.AddPolicy("Member", policy => policy.RequireRole("admin", "member"));
            options.AddPolicy("PlatformAdmin", policy => policy.RequireRole(ClaimNames.PlatformAdminRole));
        });
    }
}
