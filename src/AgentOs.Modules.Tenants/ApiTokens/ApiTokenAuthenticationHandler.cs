// Authenticates `Authorization: Bearer aos_…` (E1.3). The Api's JWT bearer scheme forwards here for tokens with the
// aos_ scheme (ApiTokenAuthentication.ForwardSelector), so one Authorization header serves both humans (Keycloak
// JWT) and machines (API token). The principal carries the same claims a JWT would: tenant, sub, name, role.

using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>Scheme name + the JWT forward selector.</summary>
public static class ApiTokenAuthentication
{
    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "ApiToken";

    /// <summary>For JwtBearerOptions.ForwardDefaultSelector: route aos_ bearer tokens to this scheme.</summary>
    public static string? ForwardSelector(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer " + ApiTokenFormat.Scheme, StringComparison.Ordinal) ? Scheme : null;
    }
}

/// <summary>Validates API tokens through <see cref="IApiTokenService"/>.</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + ApiTokenFormat.Scheme, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }
        var service = Context.RequestServices.GetRequiredService<IApiTokenService>();
        var identity = await service.ValidateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted).ConfigureAwait(false);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked or expired API token.");
        }
        var claims = new[]
        {
            new Claim(ClaimNames.Tenant, identity.TenantId),
            new Claim(ClaimNames.Subject, $"apitoken:{identity.Id}"),
            new Claim("preferred_username", $"token:{identity.Name}"),
            new Claim(ClaimTypes.Name, $"token:{identity.Name}"),
            new Claim(ClaimTypes.Role, identity.Role),
            new Claim("auth_method", "api_token"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokenAuthentication.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiTokenAuthentication.Scheme));
    }
}
