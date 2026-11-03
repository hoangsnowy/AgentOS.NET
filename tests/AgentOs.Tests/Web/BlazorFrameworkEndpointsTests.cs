// GAP-13 — only Blazor's own circuit endpoints become anonymous; a page whose route merely starts with the same text
// (or any other page) keeps the fallback / [Authorize].

using AgentOs.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class BlazorFrameworkEndpointsTests
{
    private static RouteEndpointBuilder Route(string pattern) =>
        new(_ => System.Threading.Tasks.Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0);

    [Theory]
    [InlineData("/_blazor", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/_blazor/initializers/", true)]
    [InlineData("/_blazor/disconnect/", true)]
    [InlineData("/_blazorish", false)]
    [InlineData("/", false)]
    [InlineData("/settings", false)]
    public void AllowAnonymous_OnlyTheCircuitEndpoints(string pattern, bool anonymous)
    {
        var builder = Route(pattern);

        BlazorFrameworkEndpoints.Apply(builder);

        (builder.Metadata.OfType<IAllowAnonymous>().Any()).ShouldBe(anonymous);
    }

    [Theory]
    [InlineData("/_blazor/initializers/", true)]
    [InlineData("/_blazor/disconnect/", true)]
    [InlineData("/_blazor/negotiate", false)] // opening a circuit is what the limiter bounds
    [InlineData("/_blazor", false)]           // transport on an open connection is exempted by the limiter itself
    [InlineData("/settings", false)]
    public void Apply_OnlyHousekeepingSkipsTheLimiter(string pattern, bool unlimited)
    {
        var builder = Route(pattern);

        BlazorFrameworkEndpoints.Apply(builder);

        (builder.Metadata.OfType<DisableRateLimitingAttribute>().Any()).ShouldBe(unlimited);
    }
}
