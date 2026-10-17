// REAL-AUTH E2E — plan C.3 on the full stack: a governance write made in the desktop shows up in Settings →
// Audit log with who / what / value. Saving Tool policy unchanged re-writes the same allowlist + enforce flag
// (no state change to restore) and must produce `setting.changed` rows for `tools/enforce` and
// `tools/allowlist`, attributed to the signed-in operator. The CSV export link is served same-origin.
// Residue: the audit rows this test caused are removed afterwards when AGENTOS_PG_CONN is set.

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Npgsql;
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

        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        try
        {
            await _fx.LoginAsync();
            await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
            var win = _fx.Page.Locator(".appwin.focused");
            await win.Locator(".prefs-cat", new() { HasTextString = "Tool policy" }).ClickAsync();
            await win.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Assertions.Expect(_fx.Page.Locator(".toast").Last).ToContainTextAsync("Policy");

            await win.Locator(".prefs-cat", new() { HasTextString = "Audit log" }).ClickAsync();
            await win.Locator(".audit-filter", new() { HasTextString = "Settings" }).ClickAsync();
            var table = win.Locator(".audit-table");
            await Assertions.Expect(table).ToContainTextAsync("tools/enforce", new() { Timeout = 15_000 });
            await Assertions.Expect(table).ToContainTextAsync("tools/allowlist");
            await Assertions.Expect(table.Locator("tr", new() { HasTextString = "tools/enforce" }).First).ToContainTextAsync("operator");

            // Export is a same-origin, cookie-authed CSV.
            var response = await _fx.Page.APIRequest.GetAsync("/audit/export?scope=tenant");
            Assert.Equal(200, response.Status);
            Assert.Contains("timestamp_utc,tenant,user,action,detail", await response.TextAsync(), StringComparison.Ordinal);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("AGENTOS_PG_CONN") is { Length: > 0 } cs)
            {
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM tenants.audit_events
                    WHERE "Action" = 'setting.changed' AND "TimestampUtc" >= @since
                      AND ("Target" LIKE 'tools/enforce:%' OR "Target" LIKE 'tools/allowlist:%')
                    """;
                cmd.Parameters.AddWithValue("since", started);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
