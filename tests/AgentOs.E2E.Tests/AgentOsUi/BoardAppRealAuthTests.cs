// AgentOS UI — REAL-AUTH end-to-end against the full Aspire stack (real Keycloak login, real Postgres).
// Gated by RUN_AGENTOS_E2E_REAL=true with the AppHost running. See AgentOsRealAuthFixture.
//
// Login_RealAuth_RendersDesktop proves the real OIDC login harness (works on any AppHost build).
// Board_RealAuth_RegisterRunner_AppearsInList additionally exercises the Board app + real persistence
// (needs an AppHost build that includes the Board desktop app).

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class BoardAppRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public BoardAppRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // The real Keycloak OIDC round-trip lands an authenticated user on the desktop.
    [Fact]
    public async Task Login_RealAuth_RendersDesktop()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync();

        await Assertions.Expect(_fx.Page.Locator(".dock")).ToBeVisibleAsync();
        await Assertions.Expect(_fx.Page.Locator(".topbar")).ToBeVisibleAsync();
        // We are back on the app origin, not the Keycloak login page.
        Assert.StartsWith(_fx.BaseUrl, _fx.Page.Url, StringComparison.Ordinal);
    }

    // Register a runner through the real stack and confirm it persists (appears in the list) — real
    // Postgres, real auth, real tenant scoping.
    [Fact]
    public async Task Board_RealAuth_RegisterRunner_AppearsInList()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync();
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");

        // Runner registration lives on the Runners tab (Board opens on Boards).
        await win.Locator(".board-tab", new() { HasTextString = "Runners" }).ClickAsync();
        var label = "e2e-runner-" + Guid.NewGuid().ToString("N")[..8];
        await win.GetByPlaceholder("Hoang's laptop").FillAsync(label);
        await win.GetByRole(AriaRole.Button, new() { Name = "Register runner" }).ClickAsync();

        await Assertions.Expect(win.Locator(".cred-readout textarea")).ToContainTextAsync("REMOTE_AGENT_TOKEN");
        // Persisted to Postgres + listed under the operator's tenant.
        await Assertions.Expect(win.Locator(".data-table")).ToContainTextAsync(label);
    }
}
