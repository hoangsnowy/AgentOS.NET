// System → About: every service the desktop depends on, probed live. Standalone run (DevAutoLogin, no DB):
// the Web is healthy and every external service reads "Not in this run" — never "Down". Members never see
// the platform-only "Infrastructure" details. The real-auth twin (AboutServicesRealAuthTests) checks the full
// stack, where every service must be reachable and report a version.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AboutServicesTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public AboutServicesTests(AgentOsPageFixture fx) => _fx = fx;

    /// <summary>Settings → System → About; returns the focused window.</summary>
    internal static async Task<ILocator> OpenAboutAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "System" }).First.ClickAsync();
        await win.Locator(".prefs-cat", new() { HasTextString = "About" }).ClickAsync();
        await Assertions.Expect(win.Locator(".about-row[data-service=\"web\"]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        return win;
    }

    [Fact]
    public async Task About_Standalone_ShowsEveryService_ExternalOnesNotInThisRun()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        var win = await OpenAboutAsync(_fx.Page);

        await Assertions.Expect(win.Locator(".about-row[data-service=\"web\"] .about-state")).ToHaveTextAsync("Healthy");
        foreach (var id in new[] { "api", "keycloak", "postgres", "smtp" })
        {
            await Assertions.Expect(win.Locator($".about-row[data-service=\"{id}\"] .about-state")).ToHaveTextAsync("Not in this run");
        }
        // Offline fallback is on in Development: the gateway is degraded and says it isn't billed.
        var llm = win.Locator(".about-row[data-service=\"llm\"]");
        await Assertions.Expect(llm.Locator(".about-state")).ToHaveTextAsync("Degraded");
        await Assertions.Expect(llm).ToHaveAttributeAsync("open", "");          // a row needing attention starts open
        await Assertions.Expect(llm.Locator(".about-row-body")).ToContainTextAsync("not billed");

        // The service map is an accessible image naming each service's state.
        await Assertions.Expect(win.Locator(".about-topo svg[role=\"img\"]")).ToHaveAttributeAsync("aria-label",
            new System.Text.RegularExpressions.Regex(@"PostgreSQL \(Not in this run\)"));
        await Assertions.Expect(win.Locator(".about-overall")).ToHaveTextAsync("1 degraded");

        // Refresh re-probes and keeps every card.
        await win.GetByRole(AriaRole.Button, new() { Name = "Refresh" }).ClickAsync();
        await Assertions.Expect(win.Locator(".about-row")).ToHaveCountAsync(6);
    }
}
