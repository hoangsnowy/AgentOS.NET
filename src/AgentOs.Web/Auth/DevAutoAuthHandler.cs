// Development-only auto-login. When Auth:DevAutoLogin is set (Development only — see Program.cs, which
// hard-throws if it is ever true outside Development), this scheme authenticates every request as a
// fixed "developer" principal so the AgentOS desktop runs with a single `dotnet run --project
// src/AgentOs.Web`, no Keycloak / Postgres required. The full Aspire stack turns it OFF (the AppHost
// injects Auth__DevAutoLogin=false) so it always uses real Keycloak OIDC. NEVER enabled in production.

using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Web.Auth;

/// <summary>Auto-authenticates every request as a fixed dev principal. Development only.</summary>
public sealed class DevAutoAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevAuto";

    public DevAutoAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>Cookie that lets a dev preview the member-only view. Set via <c>GET /dev/view-as</c>.</summary>
    public const string ViewAsCookie = "dev-view-as";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Mirrors the claims a real Keycloak token carries (tenant + sub + username + flattened roles)
        // so ITenantContext, the desktop AuthorizeView, and the tenant-scoped UIs all behave normally.
        // Roles are normally both admin+member; the dev-view-as cookie narrows them to preview how the
        // desktop looks for a member (or an admin) without standing up a second Keycloak user.
        var claims = new List<Claim>
        {
            new("sub", "dev-user"),
            new(ClaimTypes.NameIdentifier, "dev-user"),
            new("preferred_username", "developer"),
            new(ClaimTypes.Name, "developer"),
            new("tenant", "default"),
        };
        foreach (var role in RolesFor(Request.Cookies.TryGetValue(ViewAsCookie, out var v) ? v : null))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>Map the view-as selection to the role set the dev principal carries. Default = everything,
    /// including platform-admin (the standalone dev box is its own platform).</summary>
    private static string[] RolesFor(string? viewAs) => viewAs switch
    {
        "admin" => ["admin"],
        "member" => ["member"],
        _ => ["admin", "member", "platform-admin"],
    };
}
