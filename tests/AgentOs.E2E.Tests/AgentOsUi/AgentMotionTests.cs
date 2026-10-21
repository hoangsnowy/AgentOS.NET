// System → Appearance → "Animations": a viewer can switch the agent characters' and icons' motion off (enterprise
// desktops, demos on a projector, motion sensitivity). The choice lands on <html data-agent-motion>, which the
// stylesheet uses to stop every mascot animation, and it survives a reload. The OS reduce-motion setting is
// honoured independently by a media query.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AgentMotionTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public AgentMotionTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenAppearanceAsync()
    {
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "Appearance" }).ClickAsync();
        // The stored preferences load after the first render; clicking before that raced them (flaky on CI).
        await Assertions.Expect(win.Locator(".prefs-row[data-restored=\"true\"]")).ToBeVisibleAsync();
        return win.Locator("label.toggle[title=\"Animations\"]");
    }

    [Fact]
    public async Task AnimatedAgents_TurnedOff_StopsMotion_AndPersists()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        var html = _fx.Page.Locator("html");
        var toggle = await OpenAppearanceAsync();
        await Assertions.Expect(toggle).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bon\b"));

        await toggle.ClickAsync();
        await Assertions.Expect(html).ToHaveAttributeAsync("data-agent-motion", "off");

        // Survives a reload — restored before Blazor mounts (theme.js), so no flash of motion.
        await _fx.Page.ReloadAsync();
        await Assertions.Expect(html).ToHaveAttributeAsync("data-agent-motion", "off");

        // Turn it back on (also leaves the shared browser profile clean for other tests).
        toggle = await OpenAppearanceAsync();
        // Wait for the restored (off) state before clicking — a click on the not-yet-restored toggle is a no-op flip.
        await Assertions.Expect(toggle).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"on"));
        await toggle.ClickAsync();
        await Assertions.Expect(html).ToHaveAttributeAsync("data-agent-motion", "on");
    }
}
