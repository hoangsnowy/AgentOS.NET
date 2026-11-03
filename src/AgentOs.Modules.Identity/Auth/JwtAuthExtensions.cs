// JWT bearer auth — Keycloak OIDC resource server. Drives AddJwtBearer + Admin/Member policies.

using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace AgentOs.Modules.Identity.Auth;

/// <summary>DI extensions for Keycloak JWT bearer auth.</summary>
public static class JwtAuthExtensions
{
    /// <summary>The only JWS algorithm accepted for access tokens (and the Web's ID tokens): the realm's RS256.</summary>
    public static IReadOnlyList<string> ValidAlgorithms { get; } = [SecurityAlgorithms.RsaSha256];

    /// <summary>Add JWT bearer authentication (Keycloak) + Admin/Member policies.</summary>
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var kc = config.GetSection("Auth:Keycloak");
        var environment = config["ASPNETCORE_ENVIRONMENT"] ?? config["DOTNET_ENVIRONMENT"];
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        // The dev-Keycloak fallback is Development-only: a production host with no configured
        // Authority must fail at startup, not silently try to validate tokens against localhost.
        var authority = kc["Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            authority = isDevelopment
                ? "http://localhost:8080/realms/agentic"
                : throw new InvalidOperationException(
                    "Auth:Keycloak:Authority is required outside the Development environment.");
        }
        var audience = kc["Audience"] ?? "agentic-api";
        // Default secure (true) — except for an http://localhost authority IN DEVELOPMENT, where
        // requiring HTTPS metadata makes the JwtBearer options-factory THROW on the first request and
        // turns every route (including /health and /alive) into a 500 on a standalone dev run. The
        // exception is environment-gated so a reverse-proxied production deploy that points its
        // authority at loopback still fails loud rather than silently skipping metadata validation.
        // Explicit config wins.
        var requireHttps = bool.TryParse(kc["RequireHttpsMetadata"], out var rh)
            ? rh
            : !(isDevelopment && IsLoopbackHttp(authority));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                // Behind ACA the public authority is not hairpin-reachable from inside the
                // environment; MetadataAddress (internal http) points jwks/discovery at the
                // back-channel while Authority/ValidIssuer stay the public https URL.
                if (kc["MetadataAddress"] is { Length: > 0 } metadataAddress)
                {
                    options.MetadataAddress = metadataAddress;
                }
                options.Audience = audience;
                options.RequireHttpsMetadata = requireHttps;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    // GAP-13: the realm signs with RS256; pinning it rules out algorithm confusion (HS256 keyed with
                    // the public key, or "none") whatever the JWKS or a forged header advertises.
                    ValidAlgorithms = ValidAlgorithms,
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = "preferred_username",
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        // Issuer/audience/lifetime/signature alone don't pin the token KIND: Keycloak
                        // signs refresh + logout tokens with the same realm key + audience, so a leaked
                        // one would otherwise be accepted here as an API access token. Reject anything
                        // whose payload `typ` says it isn't an access token before trusting its roles.
                        if (IsNonAccessToken(ctx.Principal, out var tokenType))
                        {
                            ctx.Fail($"Rejected non-access token (typ='{tokenType}').");
                            return Task.CompletedTask;
                        }
                        FlattenRealmRoles(ctx.Principal);
                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    /// <summary>True when <paramref name="authority"/> is plain http on a loopback host (the dev
    /// Keycloak) — the only case where the HTTPS-metadata requirement defaults off.</summary>
    internal static bool IsLoopbackHttp(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    /// <summary>
    /// True when the validated principal is NOT a Keycloak access token, and so must be rejected.
    /// Keycloak stamps the token kind in the PAYLOAD <c>typ</c> claim — <c>"Bearer"</c> for access
    /// tokens, <c>"Refresh"</c> / <c>"Logout"</c> / <c>"ID"</c> for the rest — while the JWT *header*
    /// typ is <c>"JWT"</c> for every kind, so header-keyed
    /// <see cref="TokenValidationParameters.ValidTypes"/> can't tell them apart. A token with no
    /// <c>typ</c> claim is treated as an access token, so non-Keycloak callers are never locked out;
    /// only a present-and-not-<c>"Bearer"</c> claim fails. Comparison is case-insensitive so a
    /// legitimately-cased bearer token is never rejected.
    /// </summary>
    internal static bool IsNonAccessToken(ClaimsPrincipal? principal, out string? tokenType)
    {
        tokenType = principal?.FindFirst("typ")?.Value;
        return !string.IsNullOrEmpty(tokenType)
            && !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Flatten Keycloak's nested <c>realm_access.roles</c> JSON into individual role claims.</summary>
    public static void FlattenRealmRoles(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in roles.EnumerateArray()
                    .Select(r => r.GetString())
                    .Where(name => !string.IsNullOrEmpty(name)))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, name!));
                }
            }
        }
        catch (JsonException ex)
        {
            // Malformed realm_access claim — the user simply gets no role claims.
            _ = ex.Message;
        }
    }
}
