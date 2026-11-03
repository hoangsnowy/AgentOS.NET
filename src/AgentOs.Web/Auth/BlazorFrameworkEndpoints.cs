// GAP-13 — the Web's fallback authorization policy (RequireAuthenticatedUser) also reaches the endpoints Blazor maps
// for its circuits under /_blazor. The sign-in and sign-up pages are interactive Server components, so an anonymous
// visitor must be able to negotiate and open a circuit; the PAGES stay protected by their own [Authorize] /
// [AllowAnonymous] (endpoint metadata for the document request, AuthorizeRouteView inside the circuit).
//
// GAP-02 — the circuit's housekeeping calls (/_blazor/initializers on every page load, /_blazor/disconnect as the page
// unloads) are not counted by the global request limiter. Opening a circuit (/_blazor/negotiate) and the document still
// are, which is what bounds a flood of new circuits. Measured: the UI E2E suite sends ~370 counted requests a minute as one
// user, ~180 of them housekeeping; with those counted, the 240/min per-user bucket ran to 27 tokens locally and dry on CI.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Web.Auth;

/// <summary>Endpoint conventions for Blazor's own circuit endpoints (and nothing else).</summary>
internal static class BlazorFrameworkEndpoints
{
    /// <summary>The path Blazor Server maps its hub and helpers under.</summary>
    internal const string Prefix = "/_blazor";

    /// <summary>True for <c>/_blazor</c> and anything below it (not for a page route that merely starts with the text).</summary>
    internal static bool IsCircuitEndpoint(EndpointBuilder builder) =>
        builder is RouteEndpointBuilder { RoutePattern.RawText: { } raw }
        && (raw.Equals(Prefix, System.StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith(Prefix + "/", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>True for the circuit's housekeeping calls the limiter doesn't count (<c>/initializers</c>, <c>/disconnect</c>).</summary>
    internal static bool IsHousekeeping(EndpointBuilder builder) =>
        builder is RouteEndpointBuilder { RoutePattern.RawText: { } raw }
        && (raw.StartsWith(Prefix + "/initializers", System.StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith(Prefix + "/disconnect", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The convention: circuit endpoints are <see cref="AllowAnonymousAttribute"/>; the housekeeping ones are also
    /// <see cref="DisableRateLimitingAttribute"/>. Pages are left alone.</summary>
    internal static void Apply(EndpointBuilder builder)
    {
        if (IsCircuitEndpoint(builder))
        {
            builder.Metadata.Add(new AllowAnonymousAttribute());
        }
        if (IsHousekeeping(builder))
        {
            builder.Metadata.Add(new DisableRateLimitingAttribute());
        }
    }
}
