// Module entry: cross-cutting tenant context (claims-based) + Admin/Member policies. The auth
// scheme itself is host-specific — the API host calls AddJwtAuth (Keycloak bearer) and the Web
// host wires cookie + OIDC explicitly. No DbContext; tenant state lives in JWT/cookie claims.

using System;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Modularity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.Identity;

public sealed class IdentityModule : IModule
{
    /// <summary>The fallback authorization policy: any authenticated caller.</summary>
    public static AuthorizationPolicy FallbackPolicy { get; } = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddAuthorization(options =>
        {
            // GAP-13: deny by default. An endpoint that names no policy needs a signed-in caller; the public ones
            // (health, OIDC login, sign-up / invitation preview, runner download, the self-authenticating runner hub,
            // static assets) opt out with an explicit AllowAnonymous — a new route can no longer be public by omission.
            options.FallbackPolicy = FallbackPolicy;
            options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
            options.AddPolicy("Member", policy => policy.RequireRole("admin", "member"));
            options.AddPolicy("PlatformAdmin", policy => policy.RequireRole(ClaimNames.PlatformAdminRole));
            // E1.5: approve or reject a step an approval policy holds (an admin may too).
            options.AddPolicy("Approver", policy => policy.RequireRole(ClaimNames.ApproverRole, ClaimNames.AdminRole));
        });
    }
}
