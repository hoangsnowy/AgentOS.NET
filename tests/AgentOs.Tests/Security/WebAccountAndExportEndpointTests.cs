// GAP-19 — sign-out is a POST with an antiforgery token: a GET (an <img> or link on another site) never signs anyone out,
// and a POST without a valid token neither. GAP-17 — the export routes refuse a sign-in without a tenant claim instead of
// serving the shared "default" tenant. The Web host's real endpoint classes on a TestServer.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Data;
using AgentOs.Web.Auth;
using AgentOs.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Security;

public sealed partial class WebAccountAndExportEndpointTests
{
    // ---- Sign-out ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignOut_Get_ShowsAConfirmationForm_AndSignsNobodyOut()
    {
        var (app, recorder) = await StartAccountAsync();
        await using var _ = app;
        using var client = Client(app, roles: "member", tenant: "acme");

        using var response = await client.GetAsync(AccountEndpoints.LogoutPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("method=\"post\" action=\"/account/logout\"");
        HiddenToken().IsMatch(html).ShouldBeTrue();
        recorder.Schemes.ShouldBeEmpty();
    }

    [Fact]
    public async Task SignOut_Get_Anonymous_GoesHome()
    {
        var (app, _) = await StartAccountAsync();
        await using var __ = app;
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(AccountEndpoints.LogoutPath, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Fact]
    public async Task SignOut_PostWithoutToken_SignsNobodyOut_AndAsksToConfirm()
    {
        var (app, recorder) = await StartAccountAsync();
        await using var _ = app;
        using var client = Client(app, roles: "member", tenant: "acme");

        // A cross-site form post: the session cookie rides along, the token doesn't.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>());
        using var response = await client.PostAsync(AccountEndpoints.LogoutPath, form, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(AccountEndpoints.LogoutPath);
        recorder.Schemes.ShouldBeEmpty();
    }

    [Fact]
    public async Task SignOut_PostWithTheToken_SignsOutOfCookieAndOidc()
    {
        var (app, recorder) = await StartAccountAsync();
        await using var _ = app;
        using var client = Client(app, roles: "member", tenant: "acme");

        using var post = await TokenPostAsync(client);
        using var response = await client.SendAsync(post, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        recorder.Schemes.ShouldBe(["Cookies", "OpenIdConnect"], ignoreOrder: true);
    }

    [Fact]
    public async Task SignOut_PostWithTheToken_InDevAutoLogin_GoesHome()
    {
        var (app, recorder) = await StartAccountAsync(devAutoLogin: true);
        await using var _ = app;
        using var client = Client(app, roles: "member", tenant: "acme");

        using var post = await TokenPostAsync(client);
        using var response = await client.SendAsync(post, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
        recorder.Schemes.ShouldBeEmpty();
    }

    private static async Task<HttpRequestMessage> TokenPostAsync(HttpClient client)
    {
        using var page = await client.GetAsync(AccountEndpoints.LogoutPath, TestContext.Current.CancellationToken);
        var match = HiddenToken().Match(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        match.Success.ShouldBeTrue();
        var post = new HttpRequestMessage(HttpMethod.Post, AccountEndpoints.LogoutPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [WebUtility.HtmlDecode(match.Groups["name"].Value)] = WebUtility.HtmlDecode(match.Groups["value"].Value),
            }),
        };
        post.Headers.Add("Cookie", page.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().Split(';')[0]);
        return post;
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>__RequestVerificationToken[^\"]*)\" value=\"(?<value>[^\"]+)\"")]
    private static partial Regex HiddenToken();

    private static async Task<(WebApplication App, SignOutRecorder Recorder)> StartAccountAsync(bool devAutoLogin = false)
    {
        var builder = TestHosting.Builder("Development");
        var recorder = builder.Services.AddTestAuth();
        builder.Services.AddAntiforgery();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapAccountEndpoints(devAutoLogin);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, recorder);
    }

    // ---- Exports: no tenant claim → 403 -------------------------------------------------------------------------

    [Theory]
    [InlineData("/cost/export")]
    [InlineData("/tenant/export")]
    [InlineData("/audit/export")]
    public async Task Export_AdminWithoutTenantClaim_IsForbidden_AndReadsNothing(string path)
    {
        var (app, runs, data, audit) = await StartExportsAsync();
        await using var _ = app;
        using var client = Client(app, roles: "admin", tenant: null);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        runs.ReceivedCalls().ShouldBeEmpty();
        data.ReceivedCalls().ShouldBeEmpty();
        audit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AuditExport_AdminWithTenantClaim_ReadsOnlyThatTenant()
    {
        var (app, _, _, audit) = await StartExportsAsync();
        await using var __ = app;
        audit.ListAsync("acme", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AuditEntry>>([]));
        using var client = Client(app, roles: "admin", tenant: "acme");

        using var response = await client.GetAsync("/audit/export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await audit.Received(1).ListAsync("acme", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await audit.DidNotReceive().ListAsync("default", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditExport_PlatformScope_NeedsNoTenantClaimButPlatformAdmin()
    {
        var (app, _, _, audit) = await StartExportsAsync();
        await using var __ = app;
        audit.ListAsync(AuditScopes.Platform, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AuditEntry>>([]));
        using var client = Client(app, roles: "admin,platform-admin", tenant: null);

        using var response = await client.GetAsync("/audit/export?scope=platform", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<(WebApplication, IPipelineRunRepository, ITenantDataService, IAuditLog)> StartExportsAsync()
    {
        var runs = Substitute.For<IPipelineRunRepository>();
        var data = Substitute.For<ITenantDataService>();
        var audit = Substitute.For<IAuditLog>();
        var builder = TestHosting.Builder("Development");
        builder.Services.AddTestAuth();
        builder.Services.AddSingleton(runs).AddSingleton(data).AddSingleton(audit);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapExportEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, runs, data, audit);
    }

    private static HttpClient Client(WebApplication app, string roles, string? tenant)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TestHosting.UserHeader, "alice");
        client.DefaultRequestHeaders.Add(TestHosting.RolesHeader, roles);
        if (tenant is not null) { client.DefaultRequestHeaders.Add(TestHosting.TenantHeader, tenant); }
        return client;
    }
}
