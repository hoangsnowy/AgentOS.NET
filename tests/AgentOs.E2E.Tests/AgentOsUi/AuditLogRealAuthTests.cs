// REAL-AUTH E2E — plan C.3 on the full stack: a governance write made in the desktop shows up in Settings →
// Audit log with who / what / value. Saving Tool policy unchanged re-writes the same allowlist + enforce flag
// (no state change to restore) and must produce `setting.changed` rows for `tools/enforce` and
// `tools/allowlist`, attributed to the signed-in operator. The CSV export link is served same-origin, and "Verify chain"
// reports the tenant's hash chain intact (GAP-11b).
// Residue: none in the tenant's settings (the save writes the values it read). The audit rows it causes STAY — the trail
// is append-only and hash-chained, so deleting them would (rightly) break the operator tenant's chain.

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AuditLogRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public AuditLogRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    [Fact]
    public async Task PolicySave_ShowsUpInTheAuditLog()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync();
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Tool policy" }).ClickAsync();
        // Wait for the page to switch: the Settings landing page (Model keys) has Save buttons of its own.
        await Assertions.Expect(win.Locator(".page-title", new() { HasTextString = "Tool Policy" })).ToBeVisibleAsync();
        await win.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("Policy");

        await win.Locator(".prefs-cat", new() { HasTextString = "Activity log" }).ClickAsync();
        await win.Locator(".audit-filter", new() { HasTextString = "Settings" }).ClickAsync();
        var table = win.Locator(".audit-table");
        await Assertions.Expect(table).ToContainTextAsync("tools/enforce", new() { Timeout = 15_000 });
        await Assertions.Expect(table).ToContainTextAsync("tools/allowlist");
        await Assertions.Expect(table.Locator("tr", new() { HasTextString = "tools/enforce" }).First).ToContainTextAsync("operator");

        // Export is a same-origin, cookie-authed CSV.
        var response = await _fx.Page.APIRequest.GetAsync("/audit/export?scope=tenant");
        Assert.Equal(200, response.Status);
        Assert.Contains("timestamp_utc,tenant,user,action,detail", await response.TextAsync(), StringComparison.Ordinal);

        // GAP-11b: the trail is tamper-evident — the verifier walks the tenant's chain and finds it intact.
        await win.GetByRole(AriaRole.Button, new() { Name = "Verify chain" }).ClickAsync();
        await Assertions.Expect(win.Locator("[data-testid=audit-chain]")).ToContainTextAsync("Chain intact", new() { Timeout = 15_000 });
    }
}
