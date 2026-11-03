// GAP-11 / 11b in the desktop, on the standalone Web (invitations live in the process; no audit store):
//   • Settings › Users: an admin creates an invitation — the link is shown with the address it works for, and the
//     invitation is listed as pending; its link opens the join form with that address fixed (not editable);
//   • revoked from the list (confirmed in a dialog) it shows "revoked", and its link says it was withdrawn;
//   • Settings › Activity log: "Verify chain" answers in words (standalone records nothing, so nothing to verify).
// Using an invitation once (and the refused second use) needs the tenant registry — covered by the unit and
// real-Postgres tests (TenantSignupServiceTests, AuditAndInvitationsRealPostgresTests) and the full-stack test plan.

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class InvitationsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public InvitationsTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Invitation_IsListed_EmailBound_AndRevocable()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        var win = await OpenSettingsPageAsync("Users");

        var email = $"e2e-{Guid.NewGuid():N}@acme.test";
        await win.GetByPlaceholder("bob@acme.test").FillAsync(email);
        await win.GetByRole(AriaRole.Button, new() { Name = "Create invitation" }).ClickAsync();
        var link = win.GetByLabel("Invitation link");
        await Assertions.Expect(link).ToContainTextAsync("/signup?invite=");
        await Assertions.Expect(win.Locator(".cred-readout-hint")).ToContainTextAsync(email);
        var url = await link.InputValueAsync();

        var row = win.Locator("tr", new() { HasTextString = email });
        await Assertions.Expect(row.Locator(".badgepill")).ToHaveTextAsync("pending");

        // The link opens the join form for exactly that address.
        var visitor = await _fx.Context.NewPageAsync();
        await visitor.GotoAsync(url);
        var address = visitor.Locator("input[type=email]");
        await Assertions.Expect(address).ToHaveValueAsync(email);
        await Assertions.Expect(address).ToHaveAttributeAsync("readonly", string.Empty);

        // Revoked from the list → the same link says it was withdrawn.
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        var dialog = _fx.Page.Locator(".dialog");
        await Assertions.Expect(dialog).ToContainTextAsync(email);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        await Assertions.Expect(row.Locator(".badgepill")).ToHaveTextAsync("revoked");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new() { Name = "Revoke" })).ToHaveCountAsync(0);

        await visitor.GotoAsync(url);
        await Assertions.Expect(visitor.GetByTestId("invite-unusable")).ToContainTextAsync("withdrawn by an administrator");
        await visitor.CloseAsync();
    }

    [Fact]
    public async Task InvitationLink_Tampered_SaysItIsNotValid()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.Page.GotoAsync("/signup?invite=not-a-real-invitation");
        await Assertions.Expect(_fx.Page.GetByTestId("invite-unusable")).ToContainTextAsync("isn't valid");
    }

    [Fact]
    public async Task ActivityLog_VerifyChain_AnswersInWords()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        var win = await OpenSettingsPageAsync("Activity log");

        await win.GetByRole(AriaRole.Button, new() { Name = "Verify chain" }).ClickAsync();
        var result = win.GetByTestId("audit-chain");
        await Assertions.Expect(result).ToBeVisibleAsync();
        await Assertions.Expect(result).ToHaveAttributeAsync("role", "status");
        await Assertions.Expect(result).ToContainTextAsync("Nothing to verify yet");
    }

    private async Task<ILocator> OpenSettingsPageAsync(string page)
    {
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = page }).ClickAsync();
        return win;
    }
}
