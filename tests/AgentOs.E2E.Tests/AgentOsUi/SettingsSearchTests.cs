// UX phase 0 (docs/plans/ux-simplification.md): Activities lists every app (not only the pinned six), searches
// settings as well as apps, and a settings hit opens the hub on the exact page — here "gemini key" lands on
// Settings → LLM & providers → Model keys, also when Settings is already open on another category.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class SettingsSearchTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public SettingsSearchTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> SearchAsync(string query)
    {
        await _fx.Page.Locator(".tb-activities").ClickAsync();
        var input = _fx.Page.Locator(".ko-search-input");
        await Assertions.Expect(input).ToBeVisibleAsync();
        await input.FillAsync(query);
        return _fx.Page.Locator(".ko-row:has(.ko-row-title:text-is(\"Model keys\"))").First;
    }

    [Fact]
    public async Task ActivitiesSearch_SettingHit_OpensTheExactPage()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        var hit = await SearchAsync("gemini key");
        await Assertions.Expect(hit).ToContainTextAsync("Settings › AI");
        await hit.ClickAsync();

        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await Assertions.Expect(win.Locator(".prefs-h", new() { HasTextString = "Model keys" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".provider-card[data-provider=\"Gemini\"]")).ToBeVisibleAsync();

        // Already open on another category → the link still navigates (consumed on the window-manager change).
        await win.Locator(".prefs-cat", new() { HasTextString = "Audit log" }).ClickAsync();
        await (await SearchAsync("anthropic")).ClickAsync();
        await Assertions.Expect(win.Locator(".prefs-h", new() { HasTextString = "Model keys" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task CtrlK_OpensSearch_EnterRunsTheTopAction_WorkflowKeepsItsOwnCtrlK()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await _fx.Page.Keyboard.PressAsync("Control+k");
        var input = _fx.Page.Locator(".ko-search-input");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await input.PressSequentiallyAsync("add key");
        await Assertions.Expect(_fx.Page.Locator(".ko-row", new() { HasTextString = "Add a model key" })).ToBeVisibleAsync();
        await input.PressAsync("Enter");
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".prefs-h", new() { HasTextString = "Model keys" })).ToBeVisibleAsync();

        // With the Workflow window focused, Ctrl+K is the builder's "Add a node" palette, not the shell search.
        await _fx.Page.Locator(".dock-item[title=\"Workflow\"]").First.ClickAsync();
        await Assertions.Expect(_fx.Page.Locator(".appwin.focused .syn-toolbar")).ToBeVisibleAsync();
        await _fx.Page.Keyboard.PressAsync("Control+k");
        await Assertions.Expect(_fx.Page.Locator(".syn-palette")).ToBeVisibleAsync();
        await Assertions.Expect(input).Not.ToBeVisibleAsync();
        await _fx.Page.Keyboard.PressAsync("Escape");
    }

    [Fact]
    public async Task Activities_ListsEveryApp_NotOnlyThePinnedOnes()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await _fx.Page.Locator(".tb-activities").ClickAsync();
        // Unpinned apps (dev-auth is an admin) used to be reachable only by typing their name.
        foreach (var title in new[] { "Audit log", "API tokens", "Prompts", "Insights" })
        {
            await Assertions.Expect(_fx.Page.Locator(".ko-tile", new() { HasTextString = title }).First).ToBeVisibleAsync();
        }
        await _fx.Page.Keyboard.PressAsync("Escape");
    }
}
