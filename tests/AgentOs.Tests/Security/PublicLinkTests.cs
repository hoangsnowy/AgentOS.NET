// GAP-05 — links the server hands out (invitation URL, the runner's hub URL) come from Hosting:PublicBaseUrl, never from
// the Host header of the request that minted them. GAP-19 — the VS Code approve form is antiforgery-protected.
// Real endpoints (TenantEndpoints, PairingEndpoints) on a TestServer; the request's Host is an attacker's.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Sessions.Endpoints;
using AgentOs.Modules.Sessions.Pairing;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Hosting;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Security;

public sealed partial class PublicLinkTests
{
    private const string PublicUrl = "https://agentos.example.com";
    private const string AttackerHost = "evil.example";

    // ---- PublicOrigin ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://agentos.example.com/", "https://agentos.example.com")]
    [InlineData(" https://agentos.example.com:8443/desk/ ", "https://agentos.example.com:8443/desk")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:5199", "http://127.0.0.1:5199")]
    public void Normalize_AcceptsHttpsAndLoopbackHttp(string value, string expected) =>
        PublicOrigin.Normalize(value).ShouldBe(expected);

    [Theory]
    [InlineData("http://agentos.example.com")]
    [InlineData("ftp://agentos.example.com")]
    [InlineData("agentos.example.com")]
    [InlineData("https://user:pw@agentos.example.com")]
    [InlineData("https://agentos.example.com/?x=1")]
    [InlineData("https://agentos.example.com/#top")]
    public void Normalize_RefusesAnythingElse(string value) =>
        Should.Throw<InvalidOperationException>(() => PublicOrigin.Normalize(value));

    [Fact]
    public void BaseUrl_Configured_WinsOverTheRequest()
    {
        new PublicOrigin(PublicUrl, allowRequestFallback: true).BaseUrl("https://" + AttackerHost).ShouldBe(PublicUrl);
    }

    [Fact]
    public void BaseUrl_Unconfigured_UsesTheRequestInDevelopmentOnly()
    {
        new PublicOrigin(null, allowRequestFallback: true).BaseUrl("https://localhost:5180/").ShouldBe("https://localhost:5180");
        Should.Throw<InvalidOperationException>(() => new PublicOrigin(null, allowRequestFallback: false).BaseUrl("https://" + AttackerHost));
    }

    [Fact]
    public void Link_JoinsThePath()
    {
        new PublicOrigin(PublicUrl + "/desk/", false).Link("/signup?invite=a", null).ShouldBe(PublicUrl + "/desk/signup?invite=a");
    }

    // ---- Invitation URL ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Invitation_UrlPointsAtThePublicBaseUrl_NotTheRequestHost()
    {
        await using var app = await StartAsync(services =>
        {
            var signup = Substitute.For<ITenantSignupService>();
            signup.CreateInvitation("acme", "member", null, Arg.Any<TimeSpan>())
                .Returns(new InvitationToken("tok/1", DateTimeOffset.UtcNow.AddHours(1)));
            services.AddSingleton(signup);
            services.AddSingleton(Substitute.For<IAuditLog>());
            services.AddSingleton(Substitute.For<IEmailSender>());
            services.AddSingleton(Substitute.For<ITenantsRepository>());
            services.AddSingleton(Substitute.For<IKeycloakAdminClient>());
            services.AddSingleton(Tenant("acme"));
        }, app => app.MapTenantEndpoints());
        using var client = Client(app, "admin");

        using var response = await client.PostAsJsonAsync("/tenants/acme/invitations", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(TestContext.Current.CancellationToken);
        body!["url"].ToString().ShouldBe(PublicUrl + "/signup?invite=tok%2F1");
    }

    // ---- VS Code pairing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PairApprove_WithItsAntiforgeryToken_HandsOutTheConfiguredHubUrl()
    {
        var codes = new CapturingCodes();
        await using var app = await StartPairingAsync(codes);
        using var client = Client(app, "member");

        var (cookie, field, token) = await ApprovePageAsync(client);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/pair/vscode")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["callback"] = "vscode://agentos.connect/cb", ["state"] = "s1", ["label"] = "laptop", [field] = token,
            }),
        };
        post.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(post, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        codes.Stashed.ShouldNotBeNull();
        codes.Stashed!.HubUrl.ShouldBe(PublicUrl + "/hubs/remote-agent");
    }

    [Fact]
    public async Task PairApprove_WithoutTheAntiforgeryToken_IsRejected_AndProvisionsNothing()
    {
        var codes = new CapturingCodes();
        var provisioning = new CountingProvisioning();
        await using var app = await StartPairingAsync(codes, provisioning);
        using var client = Client(app, "member");

        // A cross-site form: the victim's cookies ride along, but the page's token can't be read cross-origin.
        var (cookie, _, _) = await ApprovePageAsync(client);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/pair/vscode")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["callback"] = "vscode://agentos.connect/cb" }),
        };
        post.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(post, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        provisioning.Calls.ShouldBe(0);
        codes.Stashed.ShouldBeNull();
    }

    private static async Task<(string Cookie, string Field, string Token)> ApprovePageAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/pair/vscode?callback=vscode%3A%2F%2Fagentos.connect%2Fcb&state=s1", TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var match = HiddenToken().Match(html);
        match.Success.ShouldBeTrue("the approve form carries an antiforgery token");
        var cookie = page.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().Split(';')[0];
        return (cookie, WebUtility.HtmlDecode(match.Groups["name"].Value), WebUtility.HtmlDecode(match.Groups["value"].Value));
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>__RequestVerificationToken[^\"]*)\" value=\"(?<value>[^\"]+)\"")]
    private static partial Regex HiddenToken();

    private static Task<WebApplication> StartPairingAsync(CapturingCodes codes, CountingProvisioning? provisioning = null) =>
        StartAsync(services =>
        {
            services.AddSingleton<IPairingCodeStore>(codes);
            services.AddSingleton<IRunnerProvisioningService>(provisioning ?? new CountingProvisioning());
            services.AddSingleton(Tenant("acme"));
        }, app => app.MapPairingEndpoints());

    private static async Task<WebApplication> StartAsync(Action<IServiceCollection> services, Action<WebApplication> map)
    {
        // Development, so no host filter stands in the way: the link must come from the configured URL regardless.
        var builder = TestHosting.Builder("Development");
        builder.Services.AddSingleton(new PublicOrigin(PublicUrl, allowRequestFallback: true));
        builder.Services.AddTestAuth();
        builder.Services.AddAntiforgery();
        builder.Services.AddRateLimiter(o => o.AddPolicy(PairingEndpoints.ExchangeRateLimitPolicy,
            _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("all")));
        services(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient Client(WebApplication app, string roles)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://" + AttackerHost);
        client.DefaultRequestHeaders.Add(TestHosting.UserHeader, "alice");
        client.DefaultRequestHeaders.Add(TestHosting.RolesHeader, roles);
        client.DefaultRequestHeaders.Add(TestHosting.TenantHeader, "acme");
        return client;
    }

    private static ITenantContext Tenant(string id)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(id);
        tenant.UserId.Returns("alice");
        tenant.UserName.Returns("alice");
        return tenant;
    }

    private sealed class CapturingCodes : IPairingCodeStore
    {
        public PairingPayload? Stashed { get; private set; }

        public string Stash(PairingPayload payload)
        {
            Stashed = payload;
            return "CODE";
        }

        public PairingPayload? Redeem(string code) => null;
    }

    private sealed class CountingProvisioning : IRunnerProvisioningService
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ProvisionedRunner> ProvisionAsync(string tenantId, string userId, string label, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProvisionedRunner(Guid.NewGuid(), label, "runner-token", "Pending"));
        }
    }
}
