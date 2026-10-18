// REAL-AUTH E2E — role isolation, fully automated. The seeded `member` user (role=member) logs in via
// real Keycloak and must NOT see the admin-only surfaces. After the 5-pillar consolidation the admin
// gate lives in the Settings hub (SettingsHub.VisibleCats filters AdminOnly categories), NOT the dock —
// so the meaningful assertion is that the admin .prefs-cat rows are absent for a member while a
// non-admin one (System) is present. This replaces the manual "log in as member and eyeball" check.
// Gated by RUN_AGENTOS_E2E_REAL=true with the full Aspire stack running and the realm imported fresh
// (the `member`/`member` user comes from infra/keycloak/agentic-realm.json).

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class RoleIsolationRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public RoleIsolationRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // A member sees normal apps but not the admin-only ones.
    [Fact]
    public async Task Member_RealAuth_AdminAppsHidden()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync("member", "member");

        // Desktop renders (no circuit crash — guards the IssueWorkAgent eager-ctor regression too).
        await Assertions.Expect(_fx.Page.Locator(".dock")).ToBeVisibleAsync();

        // Board is visible to every member (smoke check — the pillar apps are not role-gated).
        await Assertions.Expect(_fx.Page.Locator(".dock-item[title=\"Board\"]").First).ToBeVisibleAsync();

        // The real role gate: open the Settings hub and assert the AdminOnly categories are absent
        // for a member, while a non-admin one (System) renders. (Dock-level absence is no longer a
        // role signal — every admin app is unpinned for all users now.)
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var settings = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(settings.Locator(".prefs-cat", new() { HasTextString = "Appearance" })).ToBeVisibleAsync();

        // Platform (platform-admin only) and Agent profiles (tenant admin) must be hidden too.
        foreach (var adminCat in new[] { "Users", "Evidence", "Model keys", "Routing", "Agent profiles", "Prompts", "Tool policy", "MCP servers", "Platform" })
        {
            await Assertions.Expect(settings.Locator(".prefs-cat", new() { HasTextString = adminCat })).ToHaveCountAsync(0);
        }
    }
}
