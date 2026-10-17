// Batch 1 — the Web production crash screen. UseExceptionHandler("/Error") used to target a page
// that did not exist (500 + blank body in Production); ErrorPage now renders branded HTML (or RFC
// 7807 JSON for API-ish callers) with the request id for support triage.
using System.Text;
using AgentOs.Web;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public class ErrorPageTests
{
    private static async Task<(int Status, string? ContentType, string Body)> InvokeAsync(string? accept = null)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-abc-123";
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }
        using var body = new MemoryStream();
        context.Response.Body = body;

        await ErrorPage.HandleAsync(context);

        return (context.Response.StatusCode, context.Response.ContentType,
            Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task HandleAsync_BrowserRequest_RendersBrandedHtmlWithRequestId()
    {
        var (status, contentType, body) = await InvokeAsync("text/html,application/xhtml+xml");

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        contentType.ShouldNotBeNull();
        contentType.ShouldStartWith("text/html");
        body.ShouldContain("AgentOS.NET");
        body.ShouldContain("trace-abc-123");
        body.ShouldContain("Back to the desktop");
    }

    [Fact]
    public async Task HandleAsync_NoAcceptHeader_DefaultsToHtml()
    {
        var (status, contentType, _) = await InvokeAsync();

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        contentType.ShouldNotBeNull();
        contentType.ShouldStartWith("text/html");
    }

    [Fact]
    public async Task HandleAsync_JsonAccept_ReturnsProblemDetails()
    {
        var (status, contentType, body) = await InvokeAsync("application/json");

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        contentType.ShouldBe("application/problem+json");
        body.ShouldContain("\"status\":500");
        body.ShouldContain("trace-abc-123");
    }
}
