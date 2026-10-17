// Settings → Platform (Phase C.5b): a platform admin edits a platform-wide setting from descriptors — the
// self-hosted Git host allowlist — and it is written at platform scope (badge "Platform"), survives a
// reload, and Reset clears it. The dev-auth principal carries platform-admin, so the category is visible.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class PlatformSettingsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public PlatformSettingsTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenAsync()
    {
        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "Platform" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Platform");
        return win;
    }

    [Fact]
    public async Task Platform_AllowedHosts_SavesAtPlatformScope_PersistsAndResets()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();
        var row = win.Locator(".prefs-row", new() { HasTextString = "Allowed self-hosted Git hosts" });
        var input = row.Locator("input[aria-label=\"Allowed self-hosted Git hosts\"]");

        await input.FillAsync("github.mycorp.com");
        await input.PressAsync("Tab");
        await row.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(row.Locator(".badgepill").First).ToContainTextAsync("Platform");

        await _fx.Page.ReloadAsync();
        win = await OpenAsync();
        row = win.Locator(".prefs-row", new() { HasTextString = "Allowed self-hosted Git hosts" });
        await Assertions.Expect(row.Locator("input[aria-label=\"Allowed self-hosted Git hosts\"]")).ToHaveValueAsync("github.mycorp.com");

        await row.GetByRole(AriaRole.Button, new() { Name = "Reset" }).ClickAsync();
        await Assertions.Expect(row.Locator(".badgepill").First).Not.ToContainTextAsync("Platform");
    }

    [Fact]
    public async Task Platform_InvalidHost_IsRejectedWithAReason()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();
        var row = win.Locator(".prefs-row", new() { HasTextString = "Allowed self-hosted Git hosts" });
        var input = row.Locator("input[aria-label=\"Allowed self-hosted Git hosts\"]");

        await input.FillAsync("not a host!");
        await input.PressAsync("Tab");
        await row.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("is not a host name");
        await Assertions.Expect(row.Locator(".badgepill").First).Not.ToContainTextAsync("Platform");
    }
}

public sealed class PlatformSettingsRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public PlatformSettingsRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // The seeded operator carries the platform-admin realm role (agentic-realm.json; ensured on existing
    // realms by the postdeploy hook), so Keycloak's token unlocks the Platform category end to end.
    [Fact]
    public async Task Platform_RealAuth_OperatorSeesPlatformCategory()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync();
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Platform" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Platform");
        await Assertions.Expect(win.Locator(".prefs-row", new() { HasTextString = "Fail-closed tool policy" })).ToBeVisibleAsync();
    }
}
