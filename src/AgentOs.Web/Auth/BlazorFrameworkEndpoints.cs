// GAP-13 — the Web's fallback authorization policy (RequireAuthenticatedUser) also reaches the endpoints Blazor maps
// for its circuits under /_blazor. The sign-in and sign-up pages are interactive Server components, so an anonymous
// visitor must be able to negotiate and open a circuit; the PAGES stay protected by their own [Authorize] /
// [AllowAnonymous] (endpoint metadata for the document request, AuthorizeRouteView inside the circuit).

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Web.Auth;

/// <summary>An endpoint convention that marks only Blazor's circuit endpoints <see cref="AllowAnonymousAttribute"/>.</summary>
internal static class BlazorFrameworkEndpoints
{
    /// <summary>The path Blazor Server maps its hub and helpers under.</summary>
    internal const string Prefix = "/_blazor";

    /// <summary>True for <c>/_blazor</c> and anything below it (not for a page route that merely starts with the text).</summary>
    internal static bool IsCircuitEndpoint(EndpointBuilder builder) =>
        builder is RouteEndpointBuilder { RoutePattern.RawText: { } raw }
        && (raw.Equals(Prefix, System.StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith(Prefix + "/", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The convention: add <see cref="AllowAnonymousAttribute"/> to the circuit endpoints, leave pages alone.</summary>
    internal static void AllowAnonymous(EndpointBuilder builder)
    {
        if (IsCircuitEndpoint(builder))
        {
            builder.Metadata.Add(new AllowAnonymousAttribute());
        }
    }
}
