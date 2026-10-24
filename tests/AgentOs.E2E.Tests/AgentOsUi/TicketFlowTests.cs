// Workflow › Ticket flow (ADR-0014) on the standalone Web (DevAutoLogin = an admin; ticket flows kept in memory): the
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

    // Select a step (a second click would deselect it) and wait until the inspector shows it.
    private static async Task SelectAsync(ILocator win, string step)
    {
        var card = Card(win, step);
        if (await card.GetAttributeAsync("aria-pressed") != "true") { await card.ClickAsync(); }
        await Assertions.Expect(card).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(win.Locator(".syn-dock .dh-cat")).ToHaveTextAsync(char.ToUpperInvariant(step[0]) + step[1..]);
    }

    private static async Task SetRunsAsync(ILocator win, string step, bool on)
    {
        await SelectAsync(win, step);
        var toggle = win.Locator("[data-testid=ticket-flow-runs]");
        if ((await toggle.GetAttributeAsync("aria-checked") == "true") != on) { await PressAsync(toggle); }
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", on ? "true" : "false");
    }

    // A toast can sit over the toolbar's top-right buttons: press them with a DOM click once they are enabled.
    private static async Task PressAsync(ILocator button)
    {
        await Assertions.Expect(button).ToBeEnabledAsync();
        await button.EvaluateAsync("el => el.click()");
    }

    // Zero residue: every step on and saved (the built-in flow's content).
    private static async Task EnsureDefaultAsync(ILocator win)
    {
        var discard = win.GetByRole(AriaRole.Button, new() { Name = "Discard" });
        if (await discard.IsEnabledAsync()) { await PressAsync(discard); }
        var changed = false;
        foreach (var step in new[] { "review", "test" })
        {
            var css = await Card(win, step).GetAttributeAsync("class");
            if (css is null || !css.Contains("run-skipped", StringComparison.Ordinal)) { continue; }
            await SetRunsAsync(win, step, on: true);
            changed = true;
        }
        if (changed)
        {
            await PressAsync(win.Locator("[data-testid=ticket-flow-save]"));
            await Assertions.Expect(win.GetByRole(AriaRole.Button, new() { Name = "Discard" })).ToBeDisabledAsync();
        }
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
        await EnsureDefaultAsync(win); // a run cut short earlier may have left a step off
        try
        {
            // Render: the five steps in order, the way back to Code, and the table alternative.
            await Assertions.Expect(win.Locator(".tf-node")).ToHaveCountAsync(5);
            await Assertions.Expect(win.Locator(".tf-chain")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Code → Checks → Review → Test → Merge"));
            await Assertions.Expect(win.Locator(".tf-back")).ToContainTextAsync("Fail → back to Code");
            await ShotAsync("graph");
            await PressAsync(win.Locator("[data-testid=ticket-flow-table-view]"));
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-table] tbody tr")).ToHaveCountAsync(5);
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-table]")).ToContainTextAsync("Back to Code (a fix round)");
            await ShotAsync("table");
            await PressAsync(win.GetByRole(AriaRole.Button, new() { Name = "Graph" }));

            // Required steps can't be turned off.
            await SelectAsync(win, "checks");
            await Assertions.Expect(win.Locator(".syn-dock")).ToContainTextAsync("Always runs, for every ticket");

            // Toggle a step and save: a new version.
            await SetRunsAsync(win, "review", on: false);
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-skipped"));
            await PressAsync(win.Locator("[data-testid=ticket-flow-save]"));
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("Saved v");
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-source]")).ToContainTextAsync("the default flow for all projects");
            var saved = await win.Locator(".tf-versions li").First.GetAttributeAsync("data-version");

            // An unsafe flow: the problem shows inline, and Save is refused with the reason.
            await SetRunsAsync(win, "test", on: false);
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-problems]")).ToContainTextAsync("Turn on Review or Test");
            await ShotAsync("refused");
            await PressAsync(win.Locator("[data-testid=ticket-flow-save]"));
            await Assertions.Expect(win.Locator("[data-testid=ticket-flow-refused]")).ToContainTextAsync("Turn on Review or Test");
            await Assertions.Expect(win.Locator(".tf-versions li").First).ToHaveAttributeAsync("data-version", saved!);
            await PressAsync(win.GetByRole(AriaRole.Button, new() { Name = "Discard" }));

            // Save Review back on, then revert to the version without it.
            await SetRunsAsync(win, "review", on: true);
            await PressAsync(win.Locator("[data-testid=ticket-flow-save]"));
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-idle"));
            await PressAsync(win.GetByRole(AriaRole.Button, new() { Name = $"Revert to version {saved}" }));
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync($"Reverted to v{saved}");
            await Assertions.Expect(Card(win, "review")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("run-skipped"));
            await Assertions.Expect(win.Locator(".tf-versions li").First).ToContainTextAsync($"Reverted to v{saved}");
        }
        finally
        {
            await EnsureDefaultAsync(win);
        }
        await Assertions.Expect(win.Locator(".tf-chain")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Code → Checks → Review → Test → Merge"));
    }
}
