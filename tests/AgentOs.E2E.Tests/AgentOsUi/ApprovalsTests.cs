// Approval policies (E1.5) in the standalone desktop (DevAutoLogin = an admin, who may approve; no database). Board ›
// Tasks shows the Approvals panel — the standalone run seeds clearly-labelled sample requests into the in-memory store,
// as it has no tasks: a merge and a plan waiting for an approver, and a pull request already rejected with its reason.
// Waiting requests come first; Approve is a real, keyboard-reachable button; Reject needs a reason before it can be
// sent; a decision shows who made it and when. Re-runnable: a decision is final, so a second run against the same app
// finds #21 approved and asserts that. Set AGENTOS_E2E_SCREENSHOTS to a folder to capture the panel in light and dark
// (never committed).

using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

[Collection(TicketFlowGroup.Name)]
public sealed class ApprovalsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public ApprovalsTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task ShotAsync(ILocator target, string name)
    {
        var dir = Environment.GetEnvironmentVariable("AGENTOS_E2E_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { "light", "dark" })
        {
            await _fx.Page.EvaluateAsync("t => document.documentElement.setAttribute('data-theme', t)", theme);
            await target.ScreenshotAsync(new() { Path = Path.Join(dir, $"approvals-{name}-{theme}.png") });
            await _fx.Page.ScreenshotAsync(new() { Path = Path.Join(dir, $"approvals-{name}-desktop-{theme}.png") });
        }
        await _fx.Page.EvaluateAsync("() => document.documentElement.removeAttribute('data-theme')");
    }

    [Fact]
    public async Task BoardTasks_Approvals_WaitingFirst_ApproveRecordsWho_RejectNeedsAReason()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");
        await win.Locator(".board-tab", new() { HasTextString = "Tasks" }).ClickAsync();

        var panel = win.Locator("[data-testid=approvals]");
        await Assertions.Expect(panel).ToBeVisibleAsync();
        var cards = panel.Locator("[data-testid=approval-card]");
        await Assertions.Expect(cards).ToHaveCountAsync(3);

        // The rejected pull request: who rejected it and why, and no buttons.
        var rejected = cards.Filter(new() { HasTextString = "#23" });
        await Assertions.Expect(rejected).ToContainTextAsync("Rejected");
        await Assertions.Expect(rejected).ToContainTextAsync("Rejected by sample approver");
        await Assertions.Expect(rejected).ToContainTextAsync("Not before the data migration is reviewed");
        await Assertions.Expect(rejected.Locator("[data-testid=approval-approve]")).ToHaveCountAsync(0);

        // The plan: Reject is refused until there is a reason.
        var plan = cards.Filter(new() { HasTextString = "#22" });
        await Assertions.Expect(plan).ToContainTextAsync("Plan");
        await Assertions.Expect(plan.GetByRole(AriaRole.Link, new() { Name = "Open the ticket" })).ToHaveAttributeAsync("target", "_blank");
        var reject = plan.GetByRole(AriaRole.Button, new() { Name = "Reject the plan of #22" });
        await reject.ClickAsync();
        await Assertions.Expect(reject).ToHaveAttributeAsync("aria-expanded", "true");
        var confirm = plan.Locator("[data-testid=approval-reject-confirm]");
        await Assertions.Expect(confirm).ToBeDisabledAsync();
        await plan.GetByLabel("Why do you reject it?").FillAsync("Needs the export format agreed first.");
        await Assertions.Expect(confirm).ToBeEnabledAsync();
        await ShotAsync(panel, "reject");
        await reject.ClickAsync();   // changed my mind: close the reason without sending it
        await Assertions.Expect(confirm).ToHaveCountAsync(0);

        // The merge: Approve, from the keyboard; the card then says who approved it.
        var merge = cards.Filter(new() { HasTextString = "#21" });
        await Assertions.Expect(merge).ToContainTextAsync("Merge");
        await Assertions.Expect(merge.GetByRole(AriaRole.Link, new() { Name = "Open the pull request" })).ToHaveAttributeAsync("href", "https://github.com/octo-org/shop/pull/42");
        var approve = merge.GetByRole(AriaRole.Button, new() { Name = "Approve the merge of #21" });
        if (await approve.CountAsync() > 0)
        {
            await Assertions.Expect(merge).ToContainTextAsync("Waiting for approval");
            await ShotAsync(panel, "waiting");
            await approve.FocusAsync();
            await Assertions.Expect(approve).ToBeFocusedAsync();
            await approve.PressAsync("Enter");
        }
        await Assertions.Expect(merge).ToContainTextAsync("Approved by");
        await Assertions.Expect(merge.Locator("[data-testid=approval-approve]")).ToHaveCountAsync(0);
        await Assertions.Expect(merge.Locator("time")).ToHaveCountAsync(2);
        await ShotAsync(panel, "decided");
    }

    [Fact]
    public async Task SettingsAutopilot_ShowsTheApprovalsPanel()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = "Autopilot" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Autopilot");
        foreach (var label in new[]
                 {
                     "Approve the plan", "Approve opening the pull request", "Approve the change", "Approve the merge",
                     "Open pull requests as drafts", "Per project",
                 })
        {
            await Assertions.Expect(win.Locator(".prefs-label strong").GetByText(label, new() { Exact = true })).ToBeVisibleAsync();
        }
        await Assertions.Expect(win.GetByText("Repository safety.")).ToBeVisibleAsync();
        await ShotAsync(win, "settings");
    }
}
