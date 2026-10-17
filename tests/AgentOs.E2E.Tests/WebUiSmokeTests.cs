// E2E: boots the Aspire app, opens the Web in a real browser (Playwright), and asserts the
// Blazor framework actually loads + the Studio renders. This is the test that WOULD have caught
// the publish-only bugs (blazor.web.js 404, prerender crash) that unit tests missed.
// Requires Docker (Aspire) + `playwright install`. Skipped unless RUN_E2E=true.
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests;

public sealed class WebUiSmokeTests
{
    [Fact]
    public async Task Web_LoadsBlazorFramework_AndRendersStudio()
    {
        if (Environment.GetEnvironmentVariable("RUN_E2E") != "true")
        {
            Assert.Skip("E2E: set RUN_E2E=true (needs Docker + `playwright install`) to run.");
        }

        var ct = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AgentOs_AppHost>(ct);
        await using var app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);

        await app.ResourceNotifications
            .WaitForResourceHealthyAsync("web", ct)
            .WaitAsync(TimeSpan.FromMinutes(3), ct);

        var webUrl = app.GetEndpoint("web").ToString();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var notFound = new List<string>();
        page.Response += (_, r) =>
        {
            if (r.Status == 404)
            {
                notFound.Add(r.Url);
            }
        };

        await page.GotoAsync(webUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The framework script must load — the exact bug we hit (404 in published/container mode).
        Assert.DoesNotContain(notFound, u => u.Contains("blazor.web.js", StringComparison.OrdinalIgnoreCase));

        // The AgentOS.NET desktop shell renders (would fail if the prerender crash returned a broken page).
        await Assertions.Expect(page.Locator(".syn-root")).ToBeVisibleAsync();
    }
}
