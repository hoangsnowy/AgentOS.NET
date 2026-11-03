// GAP-12 — the security-headers middleware shared by the Api and the Web (ServiceDefaults). Unit level: which headers,
// when HSTS applies, how a policy is picked per path, and that the policies themselves keep their promises
// (no inline script / eval on the desktop, nothing framed anywhere). The hosts' real responses are covered by
// HostSecurityTests.

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AgentOs.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.ServiceDefaults;

public sealed class SecurityHeadersTests
{
    private static SecurityHeadersMiddleware Middleware(string environment, SecurityHeadersOptions? options = null) =>
        new(_ => Task.CompletedTask, options ?? new SecurityHeadersOptions(), new HostingEnvironment { EnvironmentName = environment });

    private static DefaultHttpContext Request(string path = "/", string scheme = "https", string host = "agentos.example.com")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        return http;
    }

    [Fact]
    public void Apply_AnyResponse_SetsTheBaselineHeaders()
    {
        var http = Request();

        Middleware(Environments.Production).Apply(http);

        var headers = http.Response.Headers;
        headers.XContentTypeOptions.ToString().ShouldBe("nosniff");
        headers.XFrameOptions.ToString().ShouldBe("DENY");
        headers["Referrer-Policy"].ToString().ShouldBe("same-origin");
        headers["Permissions-Policy"].ToString().ShouldBe(SecurityHeadersMiddleware.PermissionsPolicy);
        headers.ContentSecurityPolicy.ToString().ShouldBe(ContentSecurityPolicies.Api);
    }

    [Theory]
    [InlineData("Production", "https", "agentos.example.com", true)]
    [InlineData("Staging", "https", "agentos.example.com", true)]
    [InlineData("Development", "https", "agentos.example.com", false)] // dev never pins https
    [InlineData("Production", "http", "agentos.example.com", false)]   // RFC 6797: only over a secure transport
    [InlineData("Production", "https", "localhost", false)]            // would pin every local app on the box
    [InlineData("Production", "https", "lab.dev.localhost", false)]
    [InlineData("Production", "https", "127.0.0.1", false)]
    public void Apply_Hsts_OnlyOutsideDevelopment_OverHttps_ToARealHost(string environment, string scheme, string host, bool expected)
    {
        var http = Request(scheme: scheme, host: host);

        Middleware(environment).Apply(http);

        if (expected)
        {
            http.Response.Headers.StrictTransportSecurity.ToString().ShouldBe("max-age=31536000; includeSubDomains");
        }
        else
        {
            http.Response.Headers.ContainsKey("Strict-Transport-Security").ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("/scalar", ContentSecurityPolicies.ApiDocs)]
    [InlineData("/scalar/v1", ContentSecurityPolicies.ApiDocs)]
    [InlineData("/SCALAR/v1", ContentSecurityPolicies.ApiDocs)]
    [InlineData("/scalarx", ContentSecurityPolicies.Api)]   // segment match, not a text prefix
    [InlineData("/runs", ContentSecurityPolicies.Api)]
    public void PolicyFor_PathOverride_MatchesWholeSegments(string path, string expected)
    {
        var options = new SecurityHeadersOptions();
        options.PathPolicies.Add(new KeyValuePair<PathString, string>("/scalar", ContentSecurityPolicies.ApiDocs));

        options.PolicyFor(path).ShouldBe(expected);
    }

    [Fact]
    public void Apply_EndpointSetItsOwnPolicy_IsLeftAlone()
    {
        var http = Request();
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

        Middleware(Environments.Production).Apply(http);

        http.Response.Headers.ContentSecurityPolicy.ToString().ShouldBe("default-src 'none'; sandbox");
    }

    [Fact]
    public void WebDesktop_Policy_AllowsNoInlineScriptNoEvalAndNoFraming()
    {
        var csp = ContentSecurityPolicies.WebDesktop(["https://kc.example.com"]);

        var directives = Directives(csp);
        directives["script-src"].ShouldBe("'self'");
        directives["frame-ancestors"].ShouldBe("'none'");
        directives["object-src"].ShouldBe("'none'");
        directives["base-uri"].ShouldBe("'self'");
        directives["connect-src"].ShouldBe("'self'");
        directives["form-action"].ShouldBe("'self' https://kc.example.com");
        directives["font-src"].ShouldContain(ContentSecurityPolicies.WebFontOrigin);
        csp.ShouldNotContain("unsafe-eval");
        csp.ShouldNotContain("report-only", Case.Insensitive);
    }

    [Fact]
    public void WebDesktop_Development_AddsOnlyTheLocalBrowserRefreshSocket()
    {
        Directives(ContentSecurityPolicies.WebDesktop(development: true))["connect-src"]
            .ShouldBe("'self' ws://localhost:* wss://localhost:*");
    }

    [Fact]
    public void WebDesktop_DuplicateOrigins_AreListedOnce()
    {
        Directives(ContentSecurityPolicies.WebDesktop(["https://kc.example.com", "https://KC.example.com", "'self'"]))["form-action"]
            .ShouldBe("'self' https://kc.example.com");
    }

    [Fact]
    public void Api_Policy_LoadsNothingAndCannotBeFramed()
    {
        var directives = Directives(ContentSecurityPolicies.Api);
        directives["default-src"].ShouldBe("'none'");
        directives["frame-ancestors"].ShouldBe("'none'");
        directives["form-action"].ShouldBe("'none'");
    }

    [Theory]
    [InlineData("https://kc.example.com/realms/agentic", "https://kc.example.com")]
    [InlineData("http://localhost:8080/realms/agentic", "http://localhost:8080")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/realms/agentic", null)]
    [InlineData(null, null)]
    public void OriginOf_OnlyAbsoluteHttpUrls(string? url, string? expected) =>
        ContentSecurityPolicies.OriginOf(url).ShouldBe(expected);

    [Fact]
    public async Task Middleware_ResponseRewrittenByTheExceptionHandler_StillCarriesTheHeaders()
    {
        // The exception handler clears the response headers before writing the error page; the headers are applied as
        // the response starts, so the error page must still have them.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseAgentOsSecurityHeaders();
        app.UseExceptionHandler(e => e.Run(ctx => ctx.Response.WriteAsync("error page")));
        app.MapGet("/boom", (HttpContext _) => throw new HttpRequestException("boom"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new System.Uri("/boom", System.UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe([ContentSecurityPolicies.Api]);
    }

    internal static Dictionary<string, string> Directives(string csp)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var part in csp.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            var space = part.IndexOf(' ', System.StringComparison.Ordinal);
            map[space < 0 ? part : part[..space]] = space < 0 ? string.Empty : part[(space + 1)..];
        }
        return map;
    }
}
