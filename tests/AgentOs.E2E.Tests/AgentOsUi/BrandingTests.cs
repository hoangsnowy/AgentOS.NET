// M0 — branding smoke. The product + desktop shell are "AgentOS.NET"; the retired "Agent Studio" name
// must not surface in the desktop UI. (The agent system prompts intentionally keep "Agentic SDLC" as
// a routing key — that's backend, not the desktop chrome, so it's out of scope here.)

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class BrandingTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public BrandingTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Desktop_Branding_IsAgentOS_AndDropsRetiredName()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();

        Assert.Contains("AgentOS.NET", await _fx.Page.TitleAsync(), StringComparison.Ordinal);
        await Assertions.Expect(_fx.Page.Locator(".topbar")).ToContainTextAsync("AgentOS.NET");

        var body = await _fx.Page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Agent Studio", body, StringComparison.OrdinalIgnoreCase);
    }
}
