// E1.3 — the versioned REST contract. Every module route is served twice by the Api:
//   /v1/<route>   the contract: named endpoints, in the published OpenAPI document "v1"
//   /<route>      the original unversioned path — still works, but is deprecated: it answers with
//                 Deprecation (RFC 9745), Sunset (RFC 8594) and Link rel="successor-version" / rel="deprecation"
//                 headers, carries no endpoint names (so names stay unique) and is left out of the OpenAPI document.
// Protocol endpoints (SignalR hubs, MCP) and operational routes (/health, /alive, /version, /) are not versioned —
// they have their own wire versioning or are probed by infrastructure. Policy: docs/API.md#deprecation-policy.

using System;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.SharedKernel.Http;

/// <summary>API version names, prefixes and the deprecation dates of the unversioned routes.</summary>
public static class ApiVersions
{
    /// <summary>The current major version (also the OpenAPI document name).</summary>
    public const string V1 = "v1";

    /// <summary>Route prefix of <see cref="V1"/>.</summary>
    public const string V1Prefix = "/" + V1;

    /// <summary>Endpoint group name of the deprecated unversioned routes (never documented).</summary>
    public const string Unversioned = "unversioned";

    /// <summary>Where the deprecation policy is published (the <c>Link rel="deprecation"</c> target).</summary>
    public const string PolicyUrl = "https://github.com/hoangsnowy/AgentOS.NET/blob/main/docs/API.md#deprecation-policy";

    /// <summary>When the unversioned routes were deprecated (the day <c>/v1</c> shipped).</summary>
    public static DateTimeOffset UnversionedDeprecatedAt { get; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>When the unversioned routes may be removed (the policy's minimum six months after deprecation).</summary>
    public static DateTimeOffset UnversionedSunsetAt { get; } = new(2027, 5, 3, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Endpoint metadata: this route is deprecated; <see cref="ApiVersioning.UseApiDeprecationHeaders"/> announces it.</summary>
/// <param name="DeprecatedAt">When it was deprecated (RFC 9745 <c>Deprecation</c>).</param>
/// <param name="SunsetAt">When it may stop working (RFC 8594 <c>Sunset</c>), if decided.</param>
/// <param name="SuccessorPrefix">Prefix that turns this path into its successor (e.g. <c>/v1</c>), if any.</param>
/// <param name="PolicyUrl">The deprecation policy (<c>Link rel="deprecation"</c>).</param>
public sealed record ApiDeprecationMetadata(DateTimeOffset DeprecatedAt, DateTimeOffset? SunsetAt, string? SuccessorPrefix, string? PolicyUrl)
{
    /// <summary>The metadata every unversioned module route carries.</summary>
    public static ApiDeprecationMetadata Unversioned { get; } =
        new(ApiVersions.UnversionedDeprecatedAt, ApiVersions.UnversionedSunsetAt, ApiVersions.V1Prefix, ApiVersions.PolicyUrl);

    /// <summary>The <c>Deprecation</c> header value: an RFC 9651 date (<c>@</c> + Unix seconds).</summary>
    public string DeprecationHeader => "@" + DeprecatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>The <c>Sunset</c> header value (IMF-fixdate), or null.</summary>
    public string? SunsetHeader => SunsetAt?.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    /// <summary>Write the headers for a request to <paramref name="path"/> (escaped form).</summary>
    public void Apply(IHeaderDictionary headers, string path)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers["Deprecation"] = DeprecationHeader;
        if (SunsetHeader is { } sunset) { headers["Sunset"] = sunset; }
        var links = new System.Collections.Generic.List<string>(2);
        if (SuccessorPrefix is { Length: > 0 }) { links.Add($"<{SuccessorPrefix}{path}>; rel=\"successor-version\""); }
        if (PolicyUrl is { Length: > 0 }) { links.Add($"<{PolicyUrl}>; rel=\"deprecation\"; type=\"text/html\""); }
        if (links.Count > 0) { headers.Append("Link", string.Join(", ", links)); }
    }
}

/// <summary>Map routes under a version prefix (plus the deprecated unversioned path) and announce deprecations.</summary>
public static class ApiVersioning
{
    /// <summary>
    /// Map <paramref name="map"/>'s routes twice: under <c>/v1</c> (group name <c>v1</c>, documented) and at the original
    /// path (group <c>unversioned</c>, deprecated, undocumented, endpoint names stripped so they stay unique).
    /// </summary>
    public static IEndpointRouteBuilder MapVersioned(this IEndpointRouteBuilder endpoints, Action<IEndpointRouteBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(map);

        var v1 = endpoints.MapGroup(ApiVersions.V1Prefix);
        v1.WithGroupName(ApiVersions.V1);
        map(v1);

        var legacy = endpoints.MapGroup(string.Empty);
        legacy.WithGroupName(ApiVersions.Unversioned)
              .WithMetadata(ApiDeprecationMetadata.Unversioned)
              .ExcludeFromDescription();
        // After the endpoints' own conventions: the /v1 twin owns the endpoint and route names. (Finally is a default
        // interface member, so it is reached through the interface.)
        IEndpointConventionBuilder legacyConventions = legacy;
        legacyConventions.Finally(static builder =>
        {
            for (var i = builder.Metadata.Count - 1; i >= 0; i--)
            {
                if (builder.Metadata[i] is IEndpointNameMetadata or IRouteNameMetadata) { builder.Metadata.RemoveAt(i); }
            }
        });
        map(legacy);
        return endpoints;
    }

    /// <summary>Add <c>Deprecation</c> / <c>Sunset</c> / <c>Link</c> headers to responses of deprecated endpoints.</summary>
    public static IApplicationBuilder UseApiDeprecationHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<ApiDeprecationMetadata>() is { } deprecation)
            {
                deprecation.Apply(context.Response.Headers, context.Request.Path.ToUriComponent());
            }
            return next(context);
        });
    }
}
