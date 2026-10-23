// Open any app the way a person does when it isn't in the dock: Ctrl+K search, pick the app. The dock holds only the
// MVP apps (Home · Board · Insights · Settings); everything else — e.g. the Workflow studio — is one search away.

using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AgentOs.E2E.Tests.AgentOsUi;

internal static class DesktopNavigation
{
    /// <summary>Open <paramref name="appName"/> through the shell search and wait for its window to have focus.</summary>
    public static async Task OpenAppAsync(this IPage page, string appName)
    {
        await page.Keyboard.PressAsync("Control+k");
        var input = page.Locator(".ko-search-input");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await input.PressSequentiallyAsync(appName);
        await page.Locator(".ko-row", new() { HasTextString = appName }).First.ClickAsync();
        await Assertions.Expect(page.Locator(".appwin.focused .appwin-title")).ToHaveTextAsync(appName);
    }
}
