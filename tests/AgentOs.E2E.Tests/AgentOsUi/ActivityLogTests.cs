// UX phase 1: Evidence and Audit log are one rail entry — Settings → Governance → Activity log — with two views.
// The old "evidence" deep link (Ctrl+K "tool calls") opens the Tool calls view with the Activity log entry lit.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class ActivityLogTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public ActivityLogTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task ActivityLog_OneEntry_TwoViews_EvidenceLinkOpensToolCalls()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");

        await Assertions.Expect(win.Locator(".prefs-cat", new() { HasTextString = "Evidence" })).ToHaveCountAsync(0);
        await win.Locator(".prefs-cat", new() { HasTextString = "Activity log" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title", new() { HasTextString = "Audit log" })).ToBeVisibleAsync();
        await win.Locator(".board-tab", new() { HasTextString = "Tool calls" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title", new() { HasTextString = "Evidence" })).ToBeVisibleAsync();

        // Search hit for the tool-call trail → Tool calls view, rail entry still "Activity log".
        await win.Locator(".prefs-cat", new() { HasTextString = "Model keys" }).ClickAsync();
        await _fx.Page.Keyboard.PressAsync("Control+k");
        await _fx.Page.Locator(".ko-search-input").PressSequentiallyAsync("tool calls");
        await _fx.Page.Locator(".ko-row:has(.ko-row-title:text-is(\"Tool calls\"))").First.ClickAsync();
        await Assertions.Expect(win.Locator(".page-title", new() { HasTextString = "Evidence" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".prefs-cat.active")).ToContainTextAsync("Activity log");
    }
}
