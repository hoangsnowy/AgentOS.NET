// Settings → AI → Autopilot (ADR-0011): Autopilot is configured from the desktop. The page renders every Autopilot:*
// descriptor (who decides, how a ticket runs, the bounds, the checks wait) plus the fixed rules in plain words; a bound
// saves at tenant scope and resets; the range is enforced; and Ctrl+K search finds the page by what people type.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AutopilotSettingsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public AutopilotSettingsTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenAsync()
    {
        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "Autopilot" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Autopilot");
        return win;
    }

    [Fact]
    public async Task Autopilot_ShowsEverySetting_AndTheRealMergeRule()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();

        foreach (var label in new[]
                 {
                     "Decided by", "Each ticket runs as", "Tasks at once", "Acceptance tests at once", "Automatic fix rounds",
                     "Orchestrator retries", "Wait for checks (minutes)", "Look at checks every (seconds)",
                     "Coding run time limit (minutes)", "Review run time limit (minutes)", "Testing run time limit (minutes)",
                 })
        {
            await Assertions.Expect(win.Locator(".prefs-label strong").GetByText(label, new() { Exact = true })).ToBeVisibleAsync();
        }
        await Assertions.Expect(win.Locator("input[aria-label=\"Tasks at once\"]")).ToHaveValueAsync("2");
        await Assertions.Expect(win.Locator("input[aria-label=\"Automatic fix rounds\"]")).ToHaveValueAsync("3");
        await Assertions.Expect(win.Locator(".autopilot-rules")).ToContainTextAsync("the Reviewer agent approved it and the Testing agent passed it");
        await Assertions.Expect(win.Locator(".autopilot-rules")).ToContainTextAsync("plan hits its limit");
    }

    [Fact]
    public async Task Autopilot_ABound_SavesAtTenantScope_RejectsOutOfRange_AndResets()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();
        var row = win.Locator(".prefs-row:has(input[aria-label=\"Tasks at once\"])");
        var input = row.Locator("input[aria-label=\"Tasks at once\"]");
        try
        {
            await input.FillAsync("99");
            await input.PressAsync("Tab");
            await row.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("6"); // the range's upper bound, in the reason
            await Assertions.Expect(input).ToHaveValueAsync("2");

            await input.FillAsync("3");
            await input.PressAsync("Tab");
            await row.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Assertions.Expect(row.Locator(".badgepill").First).ToContainTextAsync("This tenant");
            await Assertions.Expect(input).ToHaveValueAsync("3");
        }
        finally
        {
            // Zero residue: the tenant value goes back to the default.
            await row.GetByRole(AriaRole.Button, new() { Name = "Reset" }).ClickAsync();
            await Assertions.Expect(row.Locator(".badgepill").First).Not.ToContainTextAsync("This tenant");
        }
        await Assertions.Expect(input).ToHaveValueAsync("2");
    }

    [Fact]
    public async Task Autopilot_TryTheApp_ShowsTheTestingHoldAndTheIdleStop()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenAsync();
        var panel = win.Locator(".panel", new() { HasTextString = "Test while trying the app" });

        await Assertions.Expect(panel.Locator(".prefs-label strong").GetByText("Test while trying the app", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(panel).ToContainTextAsync("Testing can run while you try the app when the app keeps its data separately for each copy.");
        // Nothing listed by default: every project still holds testing while its app is tried.
        await Assertions.Expect(panel.Locator("input[aria-label=\"Test while trying the app\"]")).ToHaveValueAsync("");
        await Assertions.Expect(panel.Locator("input[aria-label=\"Stop an unused app after (minutes)\"]")).ToHaveValueAsync("120");
        await Assertions.Expect(panel).ToContainTextAsync("keeps running when that computer's AgentOS.NET connection restarts");
        await win.Locator(".page-title").ScrollIntoViewIfNeededAsync();
        await panel.ScrollIntoViewIfNeededAsync();
        await _fx.Page.ScreenshotAsync(new() { Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "agentos-autopilot-try-the-app.png") });
    }

    [Fact]
    public async Task CtrlK_FixRounds_OpensSettingsAutopilot()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await _fx.Page.Keyboard.PressAsync("Control+k");
        var input = _fx.Page.Locator(".ko-search-input");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await input.PressSequentiallyAsync("fix rounds");
        var hit = _fx.Page.Locator(".ko-row", new() { HasTextString = "Settings › AI" }).First;
        await Assertions.Expect(hit).ToBeVisibleAsync();
        await hit.ClickAsync();

        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Autopilot");
    }
}
