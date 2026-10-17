// Sign-in hygiene: per-instance cookie names, and a failed OIDC callback ("Correlation failed") restarts the
// login once, then shows a plain explanation page — never a developer exception page, never an open redirect.

using System;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class AuthCookiesTests
{
    private static DefaultHttpContext Http(string? cookie = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider(),
        };
        if (cookie is not null) { http.Request.Headers.Cookie = cookie; }
        return http;
    }

    private static RemoteFailureContext Failure(HttpContext http, string message, string? redirectUri = "/board")
        => new(http, new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(), new AuthenticationFailureException(message))
        {
            Properties = new AuthenticationProperties { RedirectUri = redirectUri },
        };

    [Theory]
    [InlineData(null, "agentic")]
    [InlineData("", "agentic")]
    [InlineData("agentic-lab", "agentic-lab")]
    [InlineData("tenant.prod-1", "tenant.prod-1")]
    public void Prefix_AcceptsValidValues(string? configured, string expected)
        => AuthCookies.Prefix(configured).ShouldBe(expected);

    [Theory]
    [InlineData("Agentic")]          // uppercase
    [InlineData("-lab")]             // must start alphanumeric
    [InlineData("lab;path=/")]       // cookie-attribute injection
    [InlineData("a234567890123456789012345678901234")]   // > 32 chars
    public void Prefix_RejectsInvalidValues(string configured)
        => Should.Throw<InvalidOperationException>(() => AuthCookies.Prefix(configured));

    [Fact]
    public async Task FirstCallbackFailure_RestartsSignIn_AndSetsRetryMarker()
    {
        var http = Http();
        var ctx = Failure(http, "Correlation failed.");

        await AuthCookies.HandleRemoteFailureAsync(ctx, "agentic-lab");

        ctx.Result.ShouldNotBeNull();
        ctx.Result!.Handled.ShouldBeTrue();
        http.Response.Headers.Location.ToString().ShouldBe("/account/login?returnUrl=%2Fboard");
        http.Response.Headers.SetCookie.ToString().ShouldContain("agentic-lab.oidc.retry=1");
    }

    [Fact]
    public async Task RepeatedCallbackFailure_ShowsTheExplanationPage_AndClearsTheMarker()
    {
        var http = Http("agentic-lab.oidc.retry=1");
        var ctx = Failure(http, "Correlation failed.");

        await AuthCookies.HandleRemoteFailureAsync(ctx, "agentic-lab");

        http.Response.Headers.Location.ToString().ShouldStartWith("/account/signin-failed?reason=expired");
        http.Response.Headers.SetCookie.ToString().ShouldContain("agentic-lab.oidc.retry=;");
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    public async Task Failure_NeverRedirectsOffSite(string redirectUri)
    {
        var http = Http();
        await AuthCookies.HandleRemoteFailureAsync(Failure(http, "boom", redirectUri), "agentic");
        http.Response.Headers.Location.ToString().ShouldBe("/account/login?returnUrl=%2F");
    }

    [Fact]
    public void FailedPage_IsPlainHtml_WithALocalRetryLink()
    {
        var result = AuthCookies.FailedPage("expired", "//evil.example");
        var content = result.ShouldBeAssignableTo<IContentTypeHttpResult>();
        content!.ContentType.ShouldNotBeNull();
        content.ContentType!.ShouldStartWith("text/html");
        var html = ((Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult)result).ResponseContent!;
        html.ShouldContain("Couldn't sign you in");
        html.ShouldContain("href=\"/account/login?returnUrl=%2F\"");
        html.ShouldNotContain("evil");
        html.Split('\n').ShouldNotContain(l => l.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }
}
