// Settings → Agent profiles (Phase C.4): an admin picks an agent's model in the desktop, saves, and the value
// survives a page reload (persisted in the settings registry — in-process on the standalone Web, Postgres on
// the full stack), shows its source as "This tenant", and Reset returns it to the inherited value. The QA
// agent is switched to claude-haiku-5-5, which rejects sampling params, so the temperature cell must turn
// into the "Not used by this model" hint.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

/// <summary>The shared flow, driven against whichever fixture (dev-auth standalone or real-auth full stack).</summary>
internal static class AgentProfilesFlow
{
    private const string NewModel = "claude-haiku-5-5";

    public static async Task<ILocator> OpenAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "Agent profiles" }).ClickAsync();
        await Assertions.Expect(win.Locator(".agent-profiles")).ToBeVisibleAsync();
        return win;
    }

    public static async Task RunAsync(IPage page, System.Func<Task> reopenDesktop)
    {
        var win = await OpenAsync(page);
        var qaRow = win.Locator(".agent-profiles tbody tr", new() { HasTextString = "Qa" }).First;
        var model = qaRow.Locator("select[aria-label=\"Qa model\"]");

        await model.SelectOptionAsync(NewModel);   // a real dropdown (ModelPicker), not a filtered datalist
        await qaRow.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Assertions.Expect(qaRow.Locator(".badgepill").First).ToContainTextAsync("This tenant");
        await Assertions.Expect(qaRow).ToContainTextAsync("Not used by this model");
        await Assertions.Expect(qaRow).ToContainTextAsync("$0.1 in");

        // Survives a reload — the value comes back from the settings registry, not component state.
        await page.ReloadAsync();
        await reopenDesktop();
        win = await OpenAsync(page);
        qaRow = win.Locator(".agent-profiles tbody tr", new() { HasTextString = "Qa" }).First;
        await Assertions.Expect(qaRow.Locator("select[aria-label=\"Qa model\"]")).ToHaveValueAsync(NewModel);

        // Reset returns to the inherited value and clears the tenant badge (also leaves the env clean).
        await qaRow.GetByRole(AriaRole.Button, new() { Name = "Reset" }).ClickAsync();
        await Assertions.Expect(qaRow.Locator("select[aria-label=\"Qa model\"]")).Not.ToHaveValueAsync(NewModel);
        await Assertions.Expect(qaRow).Not.ToContainTextAsync("This tenant");
    }
}

public sealed class AgentProfilesTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public AgentProfilesTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task AgentProfiles_ChangeQaModel_PersistsAcrossReload_AndResets()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await AgentProfilesFlow.RunAsync(_fx.Page, _fx.GotoDesktopAsync);
    }
}

public sealed class AgentProfilesRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public AgentProfilesRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // Full stack: the value is written to Postgres (config.app_config, encrypted) under the operator's tenant.
    [Fact]
    public async Task AgentProfiles_RealAuth_ChangeQaModel_PersistsInPostgres_AndResets()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync();
        await AgentProfilesFlow.RunAsync(_fx.Page, () => _fx.Page.Locator(".dock").WaitForAsync());
    }
}
