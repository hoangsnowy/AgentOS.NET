// GAP-12 (docs/security/threat-model.md §15) — transport + browser hardening headers for BOTH hosts. One middleware,
// placed right after the forwarded headers (so Request.IsHttps reflects the ingress), stamps every response:
//   • Strict-Transport-Security outside Development (https requests to a non-loopback host only — RFC 6797 §7.2);
//   • X-Content-Type-Options: nosniff, X-Frame-Options: DENY (legacy twin of frame-ancestors), Referrer-Policy,
//     Permissions-Policy;
//   • a Content-Security-Policy chosen per host (and per path prefix, for the Api's dev-only docs UI).
// The headers are applied in Response.OnStarting, so a response the exception handler rewrote (it clears headers)
// still carries them; a CSP an endpoint set itself is left alone.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentOs.ServiceDefaults;

/// <summary>Options for <see cref="SecurityHeadersMiddleware"/>.</summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>The policy for every path no <see cref="PathPolicies"/> entry matches.</summary>
    public string ContentSecurityPolicy { get; set; } = ContentSecurityPolicies.Api;

    /// <summary>Per path-prefix overrides (segment match, first hit wins), e.g. the Api's <c>/scalar</c> docs UI.</summary>
    public IList<KeyValuePair<PathString, string>> PathPolicies { get; } = [];

    /// <summary>HSTS max-age. One year — the floor for HSTS preload lists.</summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>The CSP for <paramref name="path"/>.</summary>
    public string PolicyFor(PathString path)
    {
        foreach (var (prefix, policy) in PathPolicies)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return policy;
            }
        }
        return ContentSecurityPolicy;
    }
}

/// <summary>The Content-Security-Policy strings the hosts use (each source is justified in the PR / threat model).</summary>
public static class ContentSecurityPolicies
{
    /// <summary>JSON API: nothing may load, nothing may frame it.</summary>
    public const string Api = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The Api's interactive docs (Scalar, mapped outside Production only). Scalar's page boots from an inline
    /// configuration module script and injects inline styles, so it needs <c>'unsafe-inline'</c>, and it loads Inter /
    /// JetBrains Mono from <c>fonts.scalar.com</c> (its <c>DisableDefaultFonts()</c> is not emitted by 2.18) — all scoped
    /// to these paths only. No <c>'unsafe-eval'</c>: the one <c>new Function</c> in scalar.js is zod's JIT probe,
    /// which falls back when CSP blocks it.
    /// </summary>
    public const string ApiDocs =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
        + "font-src 'self' data: https://fonts.scalar.com; img-src 'self' data: blob:; connect-src 'self'; worker-src 'self' blob:; "
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>The webfont host the desktop's App.razor links (Inter, CSS + font files).</summary>
    public const string WebFontOrigin = "https://rsms.me";

    /// <summary>
    /// The Blazor Server desktop. <c>script-src 'self'</c> (no inline script, no eval — Blazor Server needs neither);
    /// <c>style-src 'unsafe-inline'</c> because Blazor renders <c>style="…"</c> attributes through the DOM (CSS only,
    /// no script); <c>connect-src 'self'</c> covers the circuit's same-origin <c>wss:</c> socket (CSP Level 3).
    /// </summary>
    /// <param name="formActionOrigins">Extra form targets — the OIDC authority's origin, since a form post that ends in a
    /// redirect to the IdP is checked against form-action too.</param>
    /// <param name="development">Development adds the <c>dotnet watch</c> browser-refresh socket on localhost.</param>
    public static string WebDesktop(IEnumerable<string>? formActionOrigins = null, bool development = false)
    {
        var formAction = string.Join(' ', (formActionOrigins ?? []).Prepend("'self'").Distinct(StringComparer.OrdinalIgnoreCase));
        var connect = development ? "'self' ws://localhost:* wss://localhost:*" : "'self'";
        return "default-src 'self'; "
            + "script-src 'self'; "
            + $"style-src 'self' 'unsafe-inline' {WebFontOrigin}; "
            + $"font-src 'self' data: {WebFontOrigin}; "
            + "img-src 'self' data: blob:; "
            + $"connect-src {connect}; "
            + "frame-src 'none'; object-src 'none'; base-uri 'self'; "
            + $"form-action {formAction}; "
            + "frame-ancestors 'none'";
    }

    /// <summary>The scheme + authority of <paramref name="url"/> (for form-action), or null when it isn't absolute http(s).</summary>
    public static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}

/// <summary>Stamps the security headers on every response (see the file header).</summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>Referrer-Policy: nothing leaves the origin (invitation / pairing links carry tokens in the query).</summary>
    public const string ReferrerPolicy = "same-origin";

    /// <summary>Browser features the product never uses.</summary>
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;
    private readonly string? _hsts;

    /// <summary>Creates the middleware.</summary>
    public SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _next = next;
        _options = options;
        _hsts = environment.IsDevelopment()
            ? null
            : FormattableString.Invariant($"max-age={(long)options.HstsMaxAge.TotalSeconds}; includeSubDomains");
    }

    /// <summary>Registers the header callback, then runs the rest of the pipeline.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            var (self, http) = ((SecurityHeadersMiddleware, HttpContext))state;
            self.Apply(http);
            return Task.CompletedTask;
        }, (this, context));
        return _next(context);
    }

    /// <summary>Writes the headers onto <paramref name="context"/>'s response (called as the headers go out).</summary>
    internal void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = ReferrerPolicy;
        headers["Permissions-Policy"] = PermissionsPolicy;
        if (!headers.ContainsKey("Content-Security-Policy"))
        {
            headers.ContentSecurityPolicy = _options.PolicyFor(context.Request.Path);
        }
        if (_hsts is not null && context.Request.IsHttps && !IsLoopback(context.Request.Host.Host))
        {
            headers.StrictTransportSecurity = _hsts;
        }
    }

    // Same exclusion as ASP.NET Core's HstsMiddleware: pinning https on localhost breaks every other local app.
    internal static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "[::1]" or "::1";
}

/// <summary>Wiring for <see cref="SecurityHeadersMiddleware"/>.</summary>
public static class SecurityHeadersExtensions
{
    /// <summary>Adds the security-headers middleware. Call right after <c>UseAgentOsForwardedHeaders</c>.</summary>
    public static WebApplication UseAgentOsSecurityHeaders(this WebApplication app, Action<SecurityHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new SecurityHeadersOptions();
        configure?.Invoke(options);
        app.UseMiddleware<SecurityHeadersMiddleware>(options);
        return app;
    }
}
