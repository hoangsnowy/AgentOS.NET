// E1.3 integration — the REAL Api host booted in-process (WebApplicationFactory, Development, no database) with the
// token store swapped for the real ApiTokenService on EF InMemory. Proves, through the actual middleware pipeline:
//   • Bearer aos_… authenticates next to the Keycloak JWT scheme and maps to the token's tenant + scopes;
//   • the scope gates the HTTP method (read ⇒ GET only, run ⇒ writes) and the admin scope the Admin policy;
//   • revoked / expired / forged tokens get 401;
//   • every module route answers under /v1, and the unversioned twin still works with Deprecation / Sunset / Link;
//   • the SignalR hub is not versioned; /v1 endpoint names are unique, legacy twins carry none;
//   • /openapi/v1.json documents the /v1 routes (not the legacy ones) and matches the committed docs/api/openapi-v1.json
//     (regenerate with AGENTOS_UPDATE_OPENAPI=1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.ApiTokens;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.SharedKernel.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Api;

/// <summary>The Api with an in-memory token store.</summary>
public sealed class ApiHostFixture : WebApplicationFactory<AgentOs.Api.ApiDocumentV1>
{
    private readonly string _db = "api-tokens-" + Guid.NewGuid().ToString("N");

    /// <summary>The clock the token service reads (tests move it to expire tokens).</summary>
    public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Audit rows the token service wrote.</summary>
    public IAuditLog Audit { get; } = Substitute.For<IAuditLog>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        // Hosts read the environment from configuration too (AddJwtAuth's dev-Keycloak fallback, ModuleEnvironment).
        builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
        builder.UseSetting("Persistence:RequireDatabase", "false");
        builder.UseSetting("Persistence:RowLevelSecurity", "false");
        builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", string.Empty);
        builder.ConfigureTestServices(services =>
        {
            var options = new DbContextOptionsBuilder<TenantsDbContext>().UseInMemoryDatabase(_db).Options;
            services.RemoveAll<IApiTokenService>();
            services.AddScoped<IApiTokenService>(_ => new ApiTokenService(new TenantsDbContext(options), Audit, Clock, throttle: new ApiTokenAuditThrottle()));
        });
    }

    /// <summary>Create a token straight through the service (as the desktop would).</summary>
    public async Task<string> CreateTokenAsync(string scope, string kind = ApiTokenKind.Service, string tenant = "acme", int days = 30)
    {
        await using var scope_ = Services.CreateAsyncScope();
        var svc = scope_.ServiceProvider.GetRequiredService<IApiTokenService>();
        var (_, token) = await svc.CreateAsync(new ApiTokenRequest(tenant, $"t-{Guid.NewGuid():N}"[..12], kind, [scope], days,
            new ApiTokenActor("alice", "user-alice", IsAdmin: true)));
        return token;
    }

    /// <summary>Revoke the token whose plaintext is <paramref name="token"/>.</summary>
    public async Task RevokeAsync(string token, string tenant = "acme")
    {
        await using var scope = Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IApiTokenService>();
        var identity = await svc.ValidateAsync(token) ?? throw new InvalidOperationException("token not valid");
        (await svc.RevokeAsync(tenant, identity.Id, new ApiTokenActor("alice", "user-alice", true))).ShouldBeTrue();
    }

    /// <summary>A client sending <c>Authorization: Bearer <paramref name="token"/></c>.</summary>
    public HttpClient ClientWith(string? token)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null) { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return client;
    }
}

/// <summary>A settable clock.</summary>
public sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>Now.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ApiV1IntegrationTests(ApiHostFixture api) : IClassFixture<ApiHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ── token auth ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Token_AuthenticatesAsItsTenant_WithItsScopes()
    {
        using var client = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Run));

        using var response = await client.GetAsync("/v1/tenants/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("tenantId").GetString().ShouldBe("acme");
        body.RootElement.GetProperty("isAuthenticated").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("authMethod").GetString().ShouldBe("api_token");
        body.RootElement.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).ShouldBe(["read", "run"]);
        body.RootElement.GetProperty("userName").GetString()!.ShouldStartWith("service:");
    }

    [Fact]
    public async Task PersonalToken_ActsAsItsOwner()
    {
        using var client = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Read, ApiTokenKind.Personal));

        using var response = await client.GetAsync("/v1/tenants/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("userId").GetString().ShouldBe("user-alice");
        body.RootElement.GetProperty("userName").GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task ReadScope_AllowsGet_RefusesWrites()
    {
        using var client = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Read));

        using var get = await client.GetAsync("/v1/tenants/me", Ct);
        using var post = await client.PostAsync("/v1/requirement", Json("{}"), Ct);

        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await post.Content.ReadAsStringAsync(Ct)).ShouldContain("'run' scope");
    }

    [Fact]
    public async Task RunScope_ReachesTheEndpoint()
    {
        using var client = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Run));

        // An empty story is a 400 validation problem from the endpoint itself — i.e. auth + scope let it through.
        using var post = await client.PostAsync("/v1/requirement", Json("{}"), Ct);

        post.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdminScope_IsNeededForAdminRoutes()
    {
        using var run = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Run));
        using var admin = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Admin));

        using var asRun = await run.GetAsync("/v1/mcp/servers", Ct);
        using var asAdmin = await admin.GetAsync("/v1/mcp/servers", Ct);

        asRun.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        asAdmin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokedToken_Gets401()
    {
        var token = await api.CreateTokenAsync(ApiTokenScopes.Read);
        using var client = api.ClientWith(token);
        (await client.GetAsync("/v1/tenants/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await api.RevokeAsync(token);

        using var after = await client.GetAsync("/v1/tenants/me", Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        after.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_token");
    }

    [Fact]
    public async Task ExpiredToken_Gets401()
    {
        var token = await api.CreateTokenAsync(ApiTokenScopes.Read, tenant: "expiry", days: 1);
        var start = api.Clock.Now;
        try
        {
            api.Clock.Now = start.AddDays(2);
            using var client = api.ClientWith(token);
            (await client.GetAsync("/v1/tenants/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            api.Clock.Now = start;
        }
    }

    [Theory]
    [InlineData("aos_abcdefghij_unknown-unknown-unknown-unknown-unknown")]
    [InlineData("aos_garbage")]
    public async Task UnknownOrMalformedToken_Gets401(string token)
    {
        using var client = api.ClientWith(token);
        (await client.GetAsync("/v1/tenants/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoCredentials_Gets401_AndAnonymousRoutesStayOpen()
    {
        using var client = api.ClientWith(null);
        (await client.GetAsync("/v1/tenants/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/version", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── versioning + deprecation ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyRoute_StillWorks_WithDeprecationHeaders()
    {
        using var client = api.ClientWith(await api.CreateTokenAsync(ApiTokenScopes.Read));

        using var legacy = await client.GetAsync("/tenants/me", Ct);
        using var v1 = await client.GetAsync("/v1/tenants/me", Ct);

        legacy.StatusCode.ShouldBe(HttpStatusCode.OK);
        legacy.Headers.GetValues("Deprecation").Single().ShouldBe("@" + ApiVersions.UnversionedDeprecatedAt.ToUnixTimeSeconds());
        legacy.Headers.GetValues("Sunset").Single().ShouldBe("Mon, 03 May 2027 00:00:00 GMT");
        var link = string.Join(", ", legacy.Headers.GetValues("Link"));
        link.ShouldContain("</v1/tenants/me>; rel=\"successor-version\"");
        link.ShouldContain("rel=\"deprecation\"");

        v1.StatusCode.ShouldBe(HttpStatusCode.OK);
        v1.Headers.Contains("Deprecation").ShouldBeFalse();
        v1.Headers.Contains("Sunset").ShouldBeFalse();
    }

    [Fact]
    public async Task LegacyRoute_AnnouncesDeprecation_EvenWhenUnauthorized()
    {
        using var client = api.ClientWith(null);
        using var legacy = await client.GetAsync("/runs", Ct);
        legacy.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        legacy.Headers.Contains("Deprecation").ShouldBeTrue();
    }

    [Fact]
    public async Task OperationalRoutes_AreNotDeprecated()
    {
        using var client = api.ClientWith(null);
        using var version = await client.GetAsync("/version", Ct);
        version.Headers.Contains("Deprecation").ShouldBeFalse();
    }

    [Fact]
    public void Endpoints_V1NamesAreUnique_LegacyTwinsUnnamed_HubUnversioned()
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var v1 = endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true).ToArray();
        var legacy = endpoints.Where(e => e.Metadata.GetMetadata<ApiDeprecationMetadata>() is not null).ToArray();

        v1.Length.ShouldBeGreaterThan(30);
        legacy.Length.ShouldBe(v1.Length);                                            // one deprecated twin per /v1 route
        v1.ShouldAllBe(e => e.Metadata.GetMetadata<ApiDeprecationMetadata>() == null);
        legacy.ShouldAllBe(e => e.Metadata.GetMetadata<IEndpointNameMetadata>() == null);
        var names = endpoints.Select(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName).OfType<string>().ToArray();
        names.Length.ShouldBe(names.Distinct(StringComparer.Ordinal).Count());

        var hub = endpoints.Where(e => e.RoutePattern.RawText?.Contains("hubs/remote-agent", StringComparison.Ordinal) == true).ToArray();
        hub.ShouldNotBeEmpty();
        hub.ShouldAllBe(e => !e.RoutePattern.RawText!.StartsWith("/v1", StringComparison.Ordinal));
        hub.ShouldAllBe(e => e.Metadata.GetMetadata<ApiDeprecationMetadata>() == null);
    }

    // ── OpenAPI ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenApi_V1DocumentsTheVersionedRoutes_AndMatchesTheCommittedCopy()
    {
        using var client = api.ClientWith(null);
        var json = await client.GetStringAsync("/openapi/v1.json", Ct);
        var doc = JsonNode.Parse(json)!.AsObject();

        doc["info"]!["version"]!.GetValue<string>().ShouldBe("v1");
        doc["info"]!["title"]!.GetValue<string>().ShouldBe("AgentOS.NET API");
        doc["components"]!["securitySchemes"]!["Bearer"]!["scheme"]!.GetValue<string>().ShouldBe("bearer");
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToArray();
        paths.ShouldContain("/v1/pipeline");
        paths.ShouldContain("/v1/tenants/me");
        paths.ShouldContain("/v1/runs");
        paths.ShouldNotContain("/pipeline");                                           // legacy twins are not documented
        paths.ShouldNotContain("/tenants/me");
        paths.Where(p => !p.StartsWith("/v1/", StringComparison.Ordinal)).ShouldAllBe(p => OperationalPaths.Contains(p));

        var committed = Path.Join(RepoRoot(), "docs", "api", "openapi-v1.json");
        var pretty = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("AGENTOS_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committed)!);
            await File.WriteAllTextAsync(committed, pretty, Ct);
        }
        File.Exists(committed).ShouldBeTrue("docs/api/openapi-v1.json is missing — run the tests with AGENTOS_UPDATE_OPENAPI=1 and commit it.");
        (await File.ReadAllTextAsync(committed, Ct)).ReplaceLineEndings("\n").ShouldBe(pretty,
            "The /v1 contract changed — regenerate docs/api/openapi-v1.json (AGENTOS_UPDATE_OPENAPI=1) and review the diff: "
            + "removing or changing a /v1 operation is a breaking change (docs/API.md#deprecation-policy).");
    }

    private static readonly HashSet<string> OperationalPaths = new(StringComparer.Ordinal) { "/", "/version", "/health/llm" };

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }
}
