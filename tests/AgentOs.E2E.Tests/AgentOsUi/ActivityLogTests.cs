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
        var search = _fx.Page.Locator(".ko-search-input");
        await Assertions.Expect(search).ToBeFocusedAsync();   // type only once Activities owns focus
        await search.PressSequentiallyAsync("tool calls");
        await Assertions.Expect(_fx.Page.Locator(".ko-row-title", new() { HasTextString = "Tool calls" }).First).ToBeVisibleAsync();
        await _fx.Page.Locator(".ko-row:has(.ko-row-title:text-is(\"Tool calls\"))").First.ClickAsync();
        await Assertions.Expect(win.Locator(".page-title", new() { HasTextString = "Evidence" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".prefs-cat.active")).ToContainTextAsync("Activity log");
    }

    // Governance: the tool calls a CLI run made on a member's computer are evidence too, behind one filter. Standalone
    // has no paired computer, so this proves the filter and its empty state; the rows themselves are covered by the
    // unit + real-Postgres tests (RunnerToolEvidenceTests, MemberComputerEvidenceRealPostgresTests) and the lab.
    [Fact]
    public async Task ToolCalls_OnAMembersComputerFilter_NarrowsAndExplains()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Activity log" }).ClickAsync();
        await win.Locator(".board-tab", new() { HasTextString = "Tool calls" }).ClickAsync();

        var all = win.GetByTestId("evidence-origin-all");
        var computer = win.GetByTestId("evidence-origin-computer");
        await Assertions.Expect(all).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(computer).ToHaveAttributeAsync("aria-pressed", "false");

        await computer.ClickAsync();
        await Assertions.Expect(computer).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(computer).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bon\b"));
        await Assertions.Expect(win.Locator(".page-sub")).ToContainTextAsync("made on a member's computer");
        await Assertions.Expect(win.Locator(".ins-note", new() { HasTextString = "scoped permissions" })).ToBeVisibleAsync();
        // Nothing ran on a paired computer here: either its empty state, or only member's-computer rows.
        var rows = win.Locator("[data-testid=evidence-table] tbody tr");
        if (await rows.CountAsync() == 0)
        {
            await Assertions.Expect(win.Locator(".empty-state-title", new() { HasTextString = "No tool calls from a member's computer yet" })).ToBeVisibleAsync();
        }
        else
        {
            await Assertions.Expect(win.Locator("[data-testid=evidence-table] tbody tr:not([data-origin=member-computer])")).ToHaveCountAsync(0);
        }

        await all.ClickAsync();
        await Assertions.Expect(all).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(win.Locator(".ins-note", new() { HasTextString = "scoped permissions" })).ToHaveCountAsync(0);
    }
}
