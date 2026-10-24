// Workflow › Ticket flow (ADR-0016) on the standalone Web (DevAutoLogin = an admin; ticket flows kept in memory): the
// view renders the flow Autopilot runs as step cards and as a table, a step can be turned off and saved as a new
// version, a flow that breaks a safety rule is refused with the reason, and an earlier version can be brought back.
// Zero residue: the test ends with the built-in flow's content saved again. Set AGENTOS_E2E_SCREENSHOTS to a folder to
// capture the view in light and dark (never committed).

using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class TicketFlowTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public TicketFlowTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenAsync()
    {
        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.OpenAppAsync("Workflow");
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".syn-select").First).ToBeVisibleAsync();
        await win.GetByRole(AriaRole.Tab, new() { Name = "Ticket flow" }).ClickAsync();
        await Assertions.Expect(win.Locator("[data-testid=ticket-flow]")).ToBeVisibleAsync();
        return win;
    }

    private static ILocator Card(ILocator win, string step) => win.Locator($".tf-node[data-step=\"{step}\"]");

    private static async Task SetRunsAsync(ILocator win, string step, bool on)
    {
        await Card(win, step).ClickAsync();
        var toggle = win.Locator("[data-testid=ticket-flow-runs]");
        if ((await toggle.GetAttributeAsync("aria-checked") == "true") != on) { await toggle.ClickAsync(); }
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", on ? "true" : "false");
    }

    private async Task ShotAsync(string name)
    {
        var dir = Environment.GetEnvironmentVariable("AGENTOS_E2E_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { "light", "dark" })
        {
            await _fx.Page.EvaluateAsync("t => document.documentElement.setAttribute('data-theme', t)", theme);
            await _fx.Page.ScreenshotAsync(new() { Path = Path.Join(dir, $"ticket-flow-{name}-{theme}.png") });
        }
    }

    [Fact]
    public async Task TicketFlow_Renders_SavesAStepOff_RefusesAnUnsafeFlow_AndReverts()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        var win = await OpenAsync();
        try
        {
            // Render: the five steps in order, the way back to Code, and the table alternative.
            await Assertions.Expect(win.Locator(".tf-node")).ToHaveCountAsync(5);
            await Assertions.Expect(win.Locator(".tf-chain")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Code → Checks → Review → Test → Merge"));
            await Assertions.Expect(win.Locator(".tf-back")).ToContainTextAsync("Fail → back to Code");
            await ShotAsync("graph");
            await win.Locator("[data-testid=ticket-flow-table-view]").ClickAsync();
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-table] tbody tr")).ToHaveCountAsync(5);
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-table]")).ToContainTextAsync("Back to Code (a fix round)");
            await ShotAsync("table");
            await win.GetByRole(AriaRole.Button, new() { Name = "Graph" }).ClickAsync();

            // Required steps can't be turned off.
            await Card(win, "checks").ClickAsync();
            await Assertions.Expect(win.Locator(".syn-dock")).ToContainTextAsync("Always runs, for every ticket");

            // Toggle a step and save: a new version.
            await SetRunsAsync(win, "review", on: false);
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-skipped"));
            await win.Locator("[data-testid=ticket-flow-save]").ClickAsync();
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("Saved v");
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-source]")).ToContainTextAsync("the default flow for all projects");
            var saved = await win.Locator(".tf-versions li").First.GetAttributeAsync("data-version");

            // An unsafe flow: the problem shows inline, and Save is refused with the reason.
            await SetRunsAsync(win, "test", on: false);
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-problems]")).ToContainTextAsync("Turn on Review or Test");
            await ShotAsync("refused");
            await win.Locator("[data-testid=ticket-flow-save]").ClickAsync();
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-refused]")).ToContainTextAsync("Turn on Review or Test");
            await Assertions.Expect(win.Locator(".tf-versions li").First).ToHaveAttributeAsync("data-version", saved!);
            await win.GetByRole(AriaRole.Button, new() { Name = "Discard" }).ClickAsync();

            // Save Review back on, then revert to the version without it.
            await SetRunsAsync(win, "review", on: true);
            await win.Locator("[data-testid=ticket-flow-save]").ClickAsync();
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-idle"));
            await win.GetByRole(AriaRole.Button, new() { Name = $"Revert to version {saved}" }).ClickAsync();
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync($"Reverted to v{saved}");
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-skipped"));
            await Assertions.Expect(win.Locator(".tf-versions li").First).ToContainTextAsync($"Reverted to v{saved}");
        }
        finally
        {
            // Zero residue: every step back on and saved (the built-in flow's content).
            var discard = win.GetByRole(AriaRole.Button, new() { Name = "Discard" });
            if (await discard.IsEnabledAsync()) { await discard.ClickAsync(); }
            if (await Card(win, "review").GetAttributeAsync("class") is { } css && css.Contains("run-skipped", StringComparison.Ordinal))
            {
                await SetRunsAsync(win, "review", on: true);
                await win.Locator("[data-testid=ticket-flow-save]").ClickAsync();
            }
        }
        await Assertions.Expect(win.Locator(".tf-chain")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Code → Checks → Review → Test → Merge"));
    }
}
