// Settings › GitHub (GAP-08 follow-up): the Pipeline auto-PR token is write-only — the page shows only its mask, and a
// blank field keeps it on Save — so "Remove saved token" (with a confirm) is the only way to clear it. Saves a fake
// token, removes it through the dialog, and checks Test then reports there is no token. Leaves no token behind.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class GitHubSettingsTests : IClassFixture<AgentOsPageFixture>
{
    private const string FakePat = "ghp_e2eFAKEe2eFAKEe2eFAKE7412";

    private readonly AgentOsPageFixture _fx;

    public GitHubSettingsTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenAsync()
    {
        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "GitHub" }).ClickAsync();
        await Assertions.Expect(win.Locator(".prefs-h", new() { HasTextString = "GitHub" })).ToBeVisibleAsync();
        return win;
    }

    [Fact]
    public async Task GitHub_SavedToken_IsMasked_CanBeRemovedWithConfirm()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();
        var pat = win.Locator("input[aria-label=\"Personal access token\"]");
        var remove = win.GetByRole(AriaRole.Button, new() { Name = "Remove saved token" });

        try
        {
            await pat.FillAsync(FakePat);
            await pat.PressAsync("Tab");
            await win.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

            // Write-only: the field clears, the placeholder shows only the last four characters.
            await Assertions.Expect(pat).ToHaveValueAsync("");
            await Assertions.Expect(pat).ToHaveAttributeAsync("placeholder", "Saved ••••7412 · type to replace");
            await Assertions.Expect(remove).ToBeVisibleAsync();

            // Cancel keeps the token.
            await remove.ClickAsync();
            var dialog = _fx.Page.Locator(".dialog", new() { HasTextString = "Remove saved token" });
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
            await Assertions.Expect(remove).ToBeVisibleAsync();

            // Confirm removes it.
            await remove.ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
            await Assertions.Expect(win.Locator(".prefs-saved", new() { HasTextString = "The saved token is gone" })).ToBeVisibleAsync();
            await Assertions.Expect(remove).Not.ToBeVisibleAsync();
            await Assertions.Expect(pat).ToHaveAttributeAsync("placeholder", "ghp_...");

            // Test with nothing typed and nothing saved says so in plain language.
            await win.GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true }).ClickAsync();
            await Assertions.Expect(win.Locator(".prefs-saved", new() { HasTextString = "No token saved yet" })).ToBeVisibleAsync();
        }
        finally
        {
            // Zero residue: never leave the fake token saved on a shared stack.
            if (await remove.IsVisibleAsync())
            {
                await remove.ClickAsync();
                await _fx.Page.Locator(".dialog").GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
            }
        }
    }
}
