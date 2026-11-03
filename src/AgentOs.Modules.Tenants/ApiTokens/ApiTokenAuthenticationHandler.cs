// Authenticates `Authorization: Bearer aos_…` (E1.3). The host's primary bearer scheme (Keycloak JWT on the Api)
// forwards here for tokens with the aos_ prefix (ApiTokenAuthentication.ForwardSelector), so one Authorization header
// serves both humans (Keycloak JWT) and machines (API token). The principal carries the same claims a JWT would —
// tenant, sub, name, role — plus the token's scopes, which ApiTokenScopeMiddleware enforces per HTTP method.
//
// Registration is self-contained (AddApiTokenAuthentication + UseApiTokenScopes): it adds a scheme and chains the
// primary scheme's forward selector without replacing the default scheme, authorization policies or a fallback policy.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>Scheme name, claim names, the forward selector and the host wiring.</summary>
public static class ApiTokenAuthentication
{
    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "ApiToken";

    /// <summary>Claim marking a principal authenticated by an API token.</summary>
    public const string AuthMethodClaim = "auth_method";

    /// <summary>Value of <see cref="AuthMethodClaim"/> for API tokens.</summary>
    public const string AuthMethod = "api_token";

    /// <summary>Claim carrying the token id.</summary>
    public const string TokenIdClaim = "token_id";

    /// <summary>Claim carrying the token kind (personal | service).</summary>
    public const string TokenKindClaim = "token_kind";

    private const string BearerPrefix = "Bearer " + ApiTokenFormat.Scheme;

    /// <summary>For the primary scheme's ForwardDefaultSelector: route aos_ bearer tokens to this scheme.</summary>
    public static string? ForwardSelector(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.Ordinal) ? Scheme : null;
    }

    /// <summary>True when <paramref name="user"/> was authenticated by an API token.</summary>
    public static bool IsApiToken(this ClaimsPrincipal? user)
        => string.Equals(user?.FindFirst(AuthMethodClaim)?.Value, AuthMethod, StringComparison.Ordinal);

    /// <summary>
    /// Register the API-token scheme and forward <c>Bearer aos_…</c> to it from <paramref name="forwardFromScheme"/> (the
    /// host's primary bearer scheme, e.g. JwtBearer). Any forward selector already on that scheme still runs for other
    /// headers. Adds no default scheme and no authorization policy.
    /// </summary>
    public static IServiceCollection AddApiTokenAuthentication<TPrimaryOptions>(this IServiceCollection services, string forwardFromScheme)
        where TPrimaryOptions : AuthenticationSchemeOptions
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(forwardFromScheme);
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(Scheme, null);
        services.PostConfigure<TPrimaryOptions>(forwardFromScheme, o =>
        {
            var previous = o.ForwardDefaultSelector;
            o.ForwardDefaultSelector = ctx => ForwardSelector(ctx) ?? previous?.Invoke(ctx);
        });
        return services;
    }

    /// <summary>Enforce API-token scopes (place after <c>UseAuthentication</c>, before <c>UseAuthorization</c>).</summary>
    public static IApplicationBuilder UseApiTokenScopes(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ApiTokenScopeMiddleware>();
    }

    /// <summary>The claims for a validated token.</summary>
    public static IReadOnlyList<Claim> ClaimsFor(ApiTokenIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var personal = identity.Kind == ApiTokenKind.Personal && !string.IsNullOrWhiteSpace(identity.OwnerUserId);
        // A personal token acts as its owner (their user id, so runs and runners are attributed to them); a service
        // account is its own principal.
        var subject = personal ? identity.OwnerUserId! : $"apitoken:{identity.Id}";
        var name = personal ? identity.CreatedBy : $"service:{identity.Name}";
        return
        [
            new Claim(ClaimNames.Tenant, identity.TenantId),
            new Claim(ClaimNames.Subject, subject),
            new Claim("preferred_username", name),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, identity.Role),
            new Claim(ApiTokenScopes.ClaimType, ApiTokenScopes.Format(identity.Scopes)),
            new Claim(AuthMethodClaim, AuthMethod),
            new Claim(TokenIdClaim, identity.Id.ToString("D", CultureInfo.InvariantCulture)),
            new Claim(TokenKindClaim, identity.Kind),
        ];
    }
}

/// <summary>Validates API tokens through <see cref="IApiTokenService"/>.</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ApiTokenAuthentication.ForwardSelector(Context) is null)
        {
            return AuthenticateResult.NoResult();
        }
        var header = Request.Headers.Authorization.ToString();
        var service = Context.RequestServices.GetRequiredService<IApiTokenService>();
        var use = new ApiTokenUse(Context.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
        var identity = await service.ValidateAsync(header["Bearer ".Length..].Trim(), use, Context.RequestAborted).ConfigureAwait(false);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked or expired API token.");
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(ApiTokenAuthentication.ClaimsFor(identity),
            ApiTokenAuthentication.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiTokenAuthentication.Scheme));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }
}

/// <summary>
/// Maps the HTTP method to the scope an API-token caller needs — safe methods (GET/HEAD/OPTIONS/TRACE) need
/// <c>read</c>, everything else <c>run</c> — and answers 403 (problem+json) when the token lacks it. Admin-only routes
/// are additionally gated by the role claim (admin scope ⇒ admin role). Keycloak (JWT) callers pass through.
/// </summary>
public sealed class ApiTokenScopeMiddleware(RequestDelegate next)
{
    /// <summary>Run the check.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.User.IsApiToken()) { return next(context); }
        var required = RequiredScope(context.Request.Method);
        if (ApiTokenScopes.Allows(context.User.FindFirst(ApiTokenScopes.ClaimType)?.Value, required)) { return next(context); }
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Insufficient token scope",
            detail: $"This API token lacks the '{required}' scope needed for {context.Request.Method} requests.")
            .ExecuteAsync(context);
    }

    /// <summary>The scope a request with <paramref name="method"/> needs.</summary>
    public static string RequiredScope(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)
            ? ApiTokenScopes.Read
            : ApiTokenScopes.Run;
}
