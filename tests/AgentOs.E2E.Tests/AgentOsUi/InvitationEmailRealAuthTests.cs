// REAL-AUTH E2E — the invitation-email path end-to-end, fully automated (no human checking an inbox).
// operator logs in via real Keycloak → Users app → create an invite for a unique address → the app's
// MailKit sender delivers to MailHog → we assert the message landed via the MailHog API. Then GAP-11: the invitation is
// listed as pending, the admin revokes it, and its link says it can't be used.
// Gated by RUN_AGENTOS_E2E_REAL=true with the full Aspire stack running (Web + Keycloak + MailHog).

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class InvitationEmailRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public InvitationEmailRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // Mint an invite with an email address through the Users app and assert the message reaches MailHog.
    [Fact]
    public async Task Invite_RealAuth_EmailLandsInMailHog()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await MailHog.ClearAsync();
        await _fx.LoginAsync(); // operator / admin

        // Open the Users surface (now a tab in the Settings hub) from the dock.
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Users" }).ClickAsync();
        await Assertions.Expect(win.GetByPlaceholder("bob@acme.test")).ToBeVisibleAsync();

        // Mint an invite for a unique address so the MailHog assertion can't collide with other runs.
        var email = $"e2e-invite-{Guid.NewGuid():N}@agentic.local";
        await win.GetByPlaceholder("bob@acme.test").FillAsync(email);
        await win.GetByRole(AriaRole.Button, new() { Name = "Create invitation" }).ClickAsync();

        // UI confirms the invite URL was created.
        var link = win.Locator(".cred-readout textarea");
        await Assertions.Expect(link).ToContainTextAsync("/signup?invite=");
        var url = await link.InputValueAsync();

        // The real assertion: the MailKit sender delivered the invite to MailHog for that address.
        var delivered = await MailHog.WaitForMessageAsync(TimeSpan.FromSeconds(20), email);
        Assert.True(delivered, $"No invitation email for {email} arrived in MailHog within 20s.");

        // GAP-11: listed as pending; the admin revokes it; the link then says it can't be used.
        var row = win.Locator("tr", new() { HasTextString = email });
        await Assertions.Expect(row.Locator(".badgepill")).ToHaveTextAsync("pending");
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        await _fx.Page.Locator(".dialog").GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        await Assertions.Expect(row.Locator(".badgepill")).ToHaveTextAsync("revoked", new() { Timeout = 15_000 });

        var page = await _fx.Page.Context.NewPageAsync();
        await page.GotoAsync(url);
        await Assertions.Expect(page.Locator("[data-testid=invite-unusable]")).ToContainTextAsync("withdrawn by an administrator");
        await page.CloseAsync();
    }
}
