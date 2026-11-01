// The Design gate (ADR-0016) in the standalone desktop (DevAutoLogin = an admin; no database). Board › Tasks shows the
// Design checks — the standalone run seeds one clearly-labelled sample into the in-memory stores, as it has no tasks —
// with the screenshot grid (phone / desktop × light / dark, served by the tenant-scoped REST route as real images),
// the Designer agent's verdict and findings, and the owner's "Approve design" / "Ask for changes", which override it.
// Workflow › Ticket flow shows the Design step off, limited to area:ui, and refuses Design without Test.
// Re-runnable: every decision sets the state it asserts. Set AGENTOS_E2E_SCREENSHOTS to a folder to capture the panel in
// light and dark (never committed).

using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

[Collection(TicketFlowGroup.Name)]
public sealed class DesignGateTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public DesignGateTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task ShotAsync(ILocator target, string name)
    {
        var dir = Environment.GetEnvironmentVariable("AGENTOS_E2E_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { "light", "dark" })
        {
            await _fx.Page.EvaluateAsync("t => document.documentElement.setAttribute('data-theme', t)", theme);
            await target.ScreenshotAsync(new() { Path = Path.Join(dir, $"design-{name}-{theme}.png") });
            await _fx.Page.ScreenshotAsync(new() { Path = Path.Join(dir, $"design-{name}-desktop-{theme}.png") });
        }
        await _fx.Page.EvaluateAsync("() => document.documentElement.removeAttribute('data-theme')");
    }

    [Fact]
    public async Task BoardTasks_DesignPanel_ShowsTheGrid_TheVerdict_AndTheOwnerOverrides()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");
        await win.Locator(".board-tab", new() { HasTextString = "Tasks" }).ClickAsync();

        var checks = win.Locator("[data-testid=design-checks]");
        await Assertions.Expect(checks).ToBeVisibleAsync();
        await Assertions.Expect(checks).ToContainTextAsync("#7");

        // Keyboard: the Open control is a real button.
        var open = checks.GetByRole(AriaRole.Button, new() { Name = "Open the design check of #7" });
        await open.FocusAsync();
        await Assertions.Expect(open).ToBeFocusedAsync();
        await open.PressAsync("Enter");
        await Assertions.Expect(checks.GetByRole(AriaRole.Button, new() { Name = "Hide the design check of #7" })).ToHaveAttributeAsync("aria-expanded", "true");
        open = checks.GetByRole(AriaRole.Button, new() { Name = "Hide the design check of #7" });

        var panel = win.Locator("[data-testid=design-panel]");
        await Assertions.Expect(panel).ToBeVisibleAsync();
        await Assertions.Expect(panel.Locator("[data-testid=design-verdict]")).ToContainTextAsync("Designer agent: changes requested");
        await Assertions.Expect(panel.Locator("[data-testid=design-verdict]")).ToContainTextAsync("Sample design check");

        // The grid: one screen × phone/desktop × light/dark, each a real image from the tenant-scoped route.
        var grid = panel.Locator("[data-testid=design-grid]");
        await Assertions.Expect(grid.Locator("thead th")).ToHaveTextAsync(["Screen", "Phone · light", "Phone · dark", "Desktop · light", "Desktop · dark"]);
        await Assertions.Expect(grid.Locator("tbody th")).ToHaveTextAsync("Checkout");
        var images = grid.Locator("img");
        await Assertions.Expect(images).ToHaveCountAsync(4);
        await Assertions.Expect(images.Nth(1)).ToHaveAttributeAsync("alt", "Checkout · phone · dark");
        foreach (var i in new[] { 0, 1, 2, 3 })
        {
            var img = images.Nth(i);
            await img.ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(img).ToHaveJSPropertyAsync("complete", true);
            Assert.True(await img.EvaluateAsync<int>("el => el.naturalWidth") > 0, $"screenshot {i} didn't load");
        }
        var desktopWidth = await images.Nth(2).EvaluateAsync<int>("el => el.naturalWidth");
        Assert.Equal(1280, desktopWidth);
        var src = await images.Nth(0).GetAttributeAsync("src");
        Assert.StartsWith("/tasks/", src);
        var response = await _fx.Page.APIRequest.GetAsync(_fx.BaseUrl.TrimEnd('/') + src);
        Assert.Equal(200, response.Status);
        Assert.Equal("image/png", response.Headers["content-type"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);

        // The findings, as a table.
        var findings = panel.Locator("[data-testid=design-findings]");
        await Assertions.Expect(findings.Locator("tbody tr")).ToHaveCountAsync(2);
        await Assertions.Expect(findings).ToContainTextAsync("Use the accent button");
        await ShotAsync(panel, "panel");

        // Check again is there for the owner (the sample has no task to run it on: it says so, nothing changes).
        await panel.GetByRole(AriaRole.Button, new() { Name = "Run the design check of #7 again" }).ClickAsync();
        await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("Couldn't check #7 again");
        await Assertions.Expect(panel.Locator("[data-testid=design-verdict]")).ToContainTextAsync("Designer agent: changes requested");

        // Ask for changes: needs a note, then it is recorded — the owner's decision wins over the agent.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Ask for design changes on #7" }).ClickAsync();
        var send = panel.GetByRole(AriaRole.Button, new() { Name = "Send to the AI" });
        await Assertions.Expect(send).ToBeDisabledAsync();
        await panel.Locator("textarea").FillAsync("Stack the summary under the form on the phone.");
        await Assertions.Expect(send).ToBeEnabledAsync();
        await send.ClickAsync();
        await Assertions.Expect(panel.Locator("[data-testid=design-decision]")).ToContainTextAsync("Changes asked");
        await Assertions.Expect(panel.Locator("[data-testid=design-decision]")).ToContainTextAsync("Stack the summary under the form on the phone.");
        await Assertions.Expect(checks.Locator(".design-checks-row", new() { HasTextString = "#7" })).ToContainTextAsync("Changes requested");

        // Approve design: the gate passes, whatever the agent said.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Approve the design of #7" }).ClickAsync();
        await Assertions.Expect(panel.Locator("[data-testid=design-decision]")).ToContainTextAsync("Design approved");
        await Assertions.Expect(panel.Locator(".design-panel-title")).ToContainTextAsync("Passed");
        await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("design approved");
        await ShotAsync(panel, "approved");

        // Hide it again.
        await open.ClickAsync();
        await Assertions.Expect(panel).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TicketFlow_DesignStep_IsOff_ForUiTickets_AndNeedsTest()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.OpenAppAsync("Workflow");
        var win = _fx.Page.Locator(".appwin.focused");
        await win.GetByRole(AriaRole.Tab, new() { Name = "Ticket flow" }).ClickAsync();
        await Assertions.Expect(win.Locator("[data-testid=ticket-flow]")).ToBeVisibleAsync();

        var design = win.Locator(".tf-node[data-step=\"design\"]");
        await Assertions.Expect(design).ToBeVisibleAsync();
        await Assertions.Expect(design).ToContainTextAsync("Off");
        await Assertions.Expect(design).ToContainTextAsync("DESIGNER");
        await design.ClickAsync();
        await Assertions.Expect(win.Locator(".syn-dock .dh-cat")).ToHaveTextAsync("Design");
        await Assertions.Expect(win.Locator(".syn-dock input[placeholder=\"type:feature\"]")).ToHaveValueAsync("area:ui");

        // On, with Test off: refused with the reason, nothing saved.
        var runs = win.Locator("[data-testid=ticket-flow-runs]");
        await runs.EvaluateAsync("el => el.click()");
        await Assertions.Expect(runs).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(win.Locator(".tf-chain")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Design \\(only area:ui\\)"));
        await win.Locator(".tf-node[data-step=\"test\"]").ClickAsync();
        await Assertions.Expect(win.Locator(".syn-dock .dh-cat")).ToHaveTextAsync("Test");
        await win.Locator("[data-testid=ticket-flow-runs]").EvaluateAsync("el => el.click()");
        await Assertions.Expect(win.Locator("[data-testid=ticket-flow-runs]")).ToHaveAttributeAsync("aria-checked", "false");
        await Assertions.Expect(win.Locator("[data-testid=ticket-flow-problems]")).ToContainTextAsync("Design checks the Testing agent's screenshots");

        // Discard: back to the flow as saved (zero residue).
        var discard = win.GetByRole(AriaRole.Button, new() { Name = "Discard" });
        await discard.EvaluateAsync("el => el.click()");
        await Assertions.Expect(design).ToContainTextAsync("Off");
        await Assertions.Expect(discard).ToBeDisabledAsync();
    }
}
