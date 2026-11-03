extern alias ApiHost;

// GAP-02 / GAP-12 / GAP-13 against the REAL hosts (WebApplicationFactory over the Web's and the Api's Program):
//   • anonymous → challenge (Web: 302 to the IdP; Api: 401) on protected endpoints, explicit anonymous ones still answer;
//   • the endpoint inventory: every anonymous route is on a reviewed list, so a new public route fails this test;
//   • the security headers on real responses;
//   • the global limiter: 429 past the limit, while health, static assets and the circuit's transport are exempt.
// The Web runs with real cookie + OIDC (DevAutoLogin off) against a static IdP configuration, so no Keycloak is needed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AgentOs.Modules.Identity.Auth;
using AgentOs.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Security;

/// <summary>The real hosts set process-wide statics (RLS tenant accessor, plugin catalog) — run these one at a time.</summary>
[CollectionDefinition(nameof(HostSecuritySerial), DisableParallelization = true)]
public sealed class HostSecuritySerial;

/// <summary>The Web host with real cookie + OIDC auth and a static (offline) IdP configuration.</summary>
public sealed class WebHostFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string Authority = "https://idp.test/realms/agentic";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:DevAutoLogin", "false");
        builder.UseSetting("Auth:Keycloak:Authority", Authority);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                o.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth",
                    TokenEndpoint = Authority + "/protocol/openid-connect/token",
                    EndSessionEndpoint = Authority + "/protocol/openid-connect/logout",
                }));
    }
}

/// <summary>The Api host, stateless (no database), JWT bearer against an authority it never has to reach.</summary>
public sealed class ApiHostFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("Persistence:RequireDatabase", "false");
        builder.UseSetting("Auth:Keycloak:Authority", "https://idp.test/realms/agentic");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }
}

[Collection(nameof(HostSecuritySerial))]
public sealed class HostSecurityTests
{
    private static HttpClient Client<T>(WebApplicationFactory<T> factory) where T : class =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path) =>
        await client.SendAsync(new HttpRequestMessage(method, Rel(path)), TestContext.Current.CancellationToken);

    /// <summary>Route patterns of every endpoint that opts out of authorization (static files excluded — see below).</summary>
    private static string[] AnonymousRoutes(IServiceProvider services) =>
    [
        .. services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(raw => !IsStaticFile(raw))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    // MapStaticAssets maps one endpoint per file (and per fingerprinted / compressed variant): a file name has an extension.
    private static bool IsStaticFile(string raw) => raw.Contains('.', StringComparison.Ordinal) && !raw.Contains('{', StringComparison.Ordinal);

    // ── Web: GAP-13 ──

    [Theory]
    [InlineData("/")]
    [InlineData("/cost/export")]
    [InlineData("/tenant/export")]
    [InlineData("/audit/export")]
    [InlineData("/pair/vscode")]
    [InlineData("/workspaces")]
    public async Task Web_AnonymousOnAProtectedRoute_IsSentToTheIdentityProvider(string path)
    {
        await using var factory = new WebHostFactory();
        using var client = Client(factory);

        using var response = await SendAsync(client, HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, path);
        response.Headers.Location!.ToString().ShouldStartWith(WebHostFactory.Authority + "/protocol/openid-connect/auth");
    }

    [Fact]
    public async Task Web_ExplicitAnonymousRoutes_StillAnswer()
    {
        await using var factory = new WebHostFactory();
        using var client = Client(factory);

        var expected = new (HttpMethod Method, string Path, HttpStatusCode Status)[]
        {
            (HttpMethod.Get, "/health", HttpStatusCode.OK),
            (HttpMethod.Get, "/alive", HttpStatusCode.OK),
            (HttpMethod.Get, "/version", HttpStatusCode.OK),
            (HttpMethod.Get, "/login", HttpStatusCode.OK),
            (HttpMethod.Get, "/signup", HttpStatusCode.OK),
            (HttpMethod.Get, "/signup/verify-email", HttpStatusCode.OK),
            (HttpMethod.Get, "/account/signin-failed", HttpStatusCode.OK),
            (HttpMethod.Get, "/account/logout", HttpStatusCode.Redirect), // sign-out works without a session
            (HttpMethod.Get, "/runner/version?rid=linux-x64", HttpStatusCode.NotFound), // public; no binary in a test run
            (HttpMethod.Get, "/_framework/blazor.web.js", HttpStatusCode.OK),
            (HttpMethod.Get, "/app.css", HttpStatusCode.OK),
            (HttpMethod.Get, "/page-boot.js", HttpStatusCode.OK),
            (HttpMethod.Get, "/_blazor/initializers", HttpStatusCode.OK),
            (HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", HttpStatusCode.OK), // the anonymous sign-up circuit
            (HttpMethod.Post, "/hubs/remote-agent/negotiate?negotiateVersion=1", HttpStatusCode.OK), // runner hub authenticates itself
        };
        foreach (var (method, path, status) in expected)
        {
            using var response = await SendAsync(client, method, path);
            response.StatusCode.ShouldBe(status, $"{method} {path}");
        }
    }

    [Fact]
    public async Task Web_AccountLogin_IsAnonymousAndChallenges()
    {
        await using var factory = new WebHostFactory();
        using var client = Client(factory);

        using var response = await SendAsync(client, HttpMethod.Get, "/account/login?returnUrl=%2F");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldStartWith(WebHostFactory.Authority);
    }

    [Fact]
    public async Task Web_AnonymousEndpointInventory_IsTheReviewedList()
    {
        await using var factory = new WebHostFactory();
        _ = factory.Server; // build the host

        AnonymousRoutes(factory.Services).ShouldBe(
        [
            "/_blazor",
            "/_blazor/disconnect/",
            "/_blazor/initializers/",
            "/_blazor/negotiate",
            "/account/login",
            "/account/logout",
            "/account/signin-failed",
            "/alive",
            "/health",
            "/hubs/remote-agent",
            "/hubs/remote-agent/negotiate",
            "/login",
            "/runner/download",
            "/runner/pair/exchange",
            "/runner/version",
            "/signup",
            "/signup/verify-email",
            "/version",
        ]);
    }

    [Fact]
    public async Task Web_StaticAssets_AreAllAnonymousAndUnthrottled()
    {
        await using var factory = new WebHostFactory();
        _ = factory.Server;

        var assets = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => IsStaticFile(e.RoutePattern.RawText ?? string.Empty))
            .ToArray();

        assets.Length.ShouldBeGreaterThan(10);
        assets.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() != null);
        assets.ShouldAllBe(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.DisableRateLimitingAttribute>() != null);
    }

    [Fact]
    public async Task Web_OidcIdTokens_ArePinnedToRs256()
    {
        await using var factory = new WebHostFactory();
        _ = factory.Server;

        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidc.TokenValidationParameters.ValidAlgorithms!.ToArray().ShouldBe([SecurityAlgorithms.RsaSha256]);
    }

    // ── Web: GAP-12 ──

    [Theory]
    [InlineData("/login")]
    [InlineData("/health")]
    [InlineData("/")] // the challenge redirect too
    public async Task Web_Responses_CarryTheSecurityHeaders(string path)
    {
        await using var factory = new WebHostFactory();
        using var client = Client(factory);

        using var response = await SendAsync(client, HttpMethod.Get, path);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["same-origin"]);
        var csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        csp.ShouldBe(ContentSecurityPolicies.WebDesktop(["https://idp.test"], development: true));
        response.Headers.Contains("Content-Security-Policy-Report-Only").ShouldBeFalse();
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse(); // Development
    }

    [Fact]
    public async Task Web_LoginPage_HasNoInlineScriptOrHandler()
    {
        // script-src 'self' — an inline <script> block or on*="" attribute in the document would be blocked.
        await using var factory = new WebHostFactory();
        using var client = Client(factory);

        var html = await client.GetStringAsync(Rel("/login"), TestContext.Current.CancellationToken);

        System.Text.RegularExpressions.Regex.IsMatch(html, @"<script(?![^>]*\bsrc=)[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .ShouldBeFalse("inline <script> block");
        System.Text.RegularExpressions.Regex.IsMatch(html, @"\son[a-z]+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .ShouldBeFalse("inline event handler attribute");
    }

    // ── Web: GAP-02 ──

    [Fact]
    public async Task Web_Limiter_OpeningCircuitsPastTheLimit_Is429_LiveTrafficHealthAndAssetsAreNot()
    {
        await using var factory = new WebHostFactory(new Dictionary<string, string?>
        {
            [RequestRateLimitSettings.AnonymousPerMinute] = "5",
        });
        using var client = Client(factory);

        var negotiate = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var r = await SendAsync(client, HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
            negotiate.Add(r.StatusCode);
        }
        negotiate.Take(5).ShouldAllBe(c => c == HttpStatusCode.OK);
        negotiate[5].ShouldBe(HttpStatusCode.TooManyRequests);

        // Past the limit, the exempt traffic still flows: a circuit's transport requests (here with an unknown id, so
        // SignalR answers 404 — the point is that it is not 429), health probes and static files.
        foreach (var path in new[] { "/_blazor?id=already-negotiated", "/health", "/alive", "/version", "/app.css", "/_framework/blazor.web.js" })
        {
            for (var i = 0; i < 10; i++)
            {
                using var r = await SendAsync(client, HttpMethod.Get, path);
                r.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, path);
            }
        }

        // …and an ordinary anonymous page is refused with a Retry-After.
        using var page = await SendAsync(client, HttpMethod.Get, "/login");
        page.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        page.Headers.RetryAfter.ShouldNotBeNull();
    }

    // ── Api ──

    [Theory]
    [InlineData("/health/llm")]
    [InlineData("/tenants/me")]
    [InlineData("/runs")]
    [InlineData("/mcp/servers")]
    [InlineData("/settings/Llm")]
    public async Task Api_AnonymousOnAProtectedRoute_Is401(string path)
    {
        await using var factory = new ApiHostFactory();
        using var client = Client(factory);

        using var response = await SendAsync(client, HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
    }

    [Fact]
    public async Task Api_ExplicitAnonymousRoutes_StillAnswer()
    {
        await using var factory = new ApiHostFactory();
        using var client = Client(factory);

        var expected = new (HttpMethod Method, string Path, HttpStatusCode Status)[]
        {
            (HttpMethod.Get, "/", HttpStatusCode.OK),
            (HttpMethod.Get, "/version", HttpStatusCode.OK),
            (HttpMethod.Get, "/health", HttpStatusCode.OK),
            (HttpMethod.Get, "/alive", HttpStatusCode.OK),
            (HttpMethod.Get, "/openapi/v1.json", HttpStatusCode.OK), // dev docs (not mapped in Production)
            (HttpMethod.Get, "/scalar/v1", HttpStatusCode.OK),
            (HttpMethod.Get, "/tenants/invitations/preview?token=not-a-token", HttpStatusCode.NotFound), // public preview
            (HttpMethod.Post, "/hubs/remote-agent/negotiate?negotiateVersion=1", HttpStatusCode.OK),
        };
        foreach (var (method, path, status) in expected)
        {
            using var response = await SendAsync(client, method, path);
            response.StatusCode.ShouldBe(status, $"{method} {path}");
        }
    }

    [Fact]
    public async Task Api_AnonymousEndpointInventory_IsTheReviewedList()
    {
        await using var factory = new ApiHostFactory();
        _ = factory.Server;

        AnonymousRoutes(factory.Services).ShouldBe(
        [
            "/",
            "/alive",
            "/health",
            "/hubs/remote-agent",
            "/hubs/remote-agent/negotiate",
            "/openapi/{documentName}.json",
            "/scalar/{documentName?}", // the docs page + its embedded assets
            "/tenants/invitations/preview",
            "/tenants/register",
            "/version",
        ]);
    }

    [Fact]
    public async Task Api_JwtBearer_IsPinnedToRs256()
    {
        await using var factory = new ApiHostFactory();
        _ = factory.Server;

        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        jwt.TokenValidationParameters.ValidAlgorithms!.ToArray().ShouldBe(JwtAuthExtensions.ValidAlgorithms.ToArray());
    }

    [Fact]
    public async Task Api_Responses_CarryTheStrictPolicy_DocsTheirOwn()
    {
        await using var factory = new ApiHostFactory();
        using var client = Client(factory);

        using var health = await SendAsync(client, HttpMethod.Get, "/health");
        health.Headers.GetValues("Content-Security-Policy").ShouldBe([ContentSecurityPolicies.Api]);
        health.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        health.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        health.Headers.GetValues("Referrer-Policy").ShouldBe(["same-origin"]);

        using var unauthorized = await SendAsync(client, HttpMethod.Get, "/runs");
        unauthorized.Headers.GetValues("Content-Security-Policy").ShouldBe([ContentSecurityPolicies.Api]);

        using var docs = await SendAsync(client, HttpMethod.Get, "/scalar/v1");
        docs.Headers.GetValues("Content-Security-Policy").ShouldBe([ContentSecurityPolicies.ApiDocs]);
    }

    [Fact]
    public async Task Api_Limiter_AnonymousPastTheLimit_Is429_HealthIsNot()
    {
        await using var factory = new ApiHostFactory(new Dictionary<string, string?>
        {
            [RequestRateLimitSettings.AnonymousPerMinute] = "5",
        });
        using var client = Client(factory);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var r = await SendAsync(client, HttpMethod.Get, "/tenants/invitations/preview?token=x");
            codes.Add(r.StatusCode);
        }
        codes.Take(5).ShouldAllBe(c => c == HttpStatusCode.NotFound);
        codes[5].ShouldBe(HttpStatusCode.TooManyRequests);

        foreach (var path in new[] { "/health", "/alive", "/version", "/" })
        {
            using var r = await SendAsync(client, HttpMethod.Get, path);
            r.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }
    }
}
