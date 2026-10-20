// REAL-AUTH E2E — multi-tenant isolation on the full stack, through the real product paths:
//   1. tenant A (`default`, the seeded operator) gets marker rows in four tenant-scoped tables — a connected
//      board (workspaces), a runner (sessions), a tool invocation (evidence) and a run (pipeline);
//   2. control: operator sees every marker in Board / Evidence (so an absence later means isolation, not a
//      broken seed);
//   3. a brand-new tenant B is created through the public /signup page (Keycloak user + tenant row, the real
//      saga) and signs in;
//   4. B sees none of A's markers in Board (boards + runners), Evidence or Insights, and Users lists only B.
// Zero residue: A's markers, every row tenant B created (all TenantId tables + the tenants row) and B's
// Keycloak user are removed in `finally`.
//
//   - Gates: RUN_AGENTOS_E2E_REAL=true (full stack up) AND AGENTOS_PG_CONN (Npgsql conn for the `agentos` db).
//   - Keycloak admin for cleanup: AGENTOS_KC_URL (default https://localhost:8080; lab https://localhost:9080),
//     AGENTOS_KC_ADMIN_USER / AGENTOS_KC_ADMIN_PASSWORD (default admin/admin — the AppHost dev parameters).

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Npgsql;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class TenantIsolationRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private const string TenantA = "default";
    private readonly AgentOsRealAuthFixture _fx;

    public TenantIsolationRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    private static string? PgConn => Environment.GetEnvironmentVariable("AGENTOS_PG_CONN");

    [Fact]
    public async Task SecondTenant_SeesNoneOfTheFirstTenantsData()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }
        if (string.IsNullOrWhiteSpace(PgConn)) { Assert.Skip("AGENTOS_PG_CONN not set — the isolation test seeds tenant A in Postgres."); }

        var id = Guid.NewGuid().ToString("N")[..10];
        var marker = $"e2e-iso-{id}";          // in every seeded row of tenant A
        var tenantB = $"iso-{id}";             // the new tenant's slug (≤32, lowercase/digits/dash)
        var userB = $"iso{id}";
        var passwordB = $"Iso-{id}-Pass9!";
        var seeded = new SeededRows();

        try
        {
            await SeedTenantAAsync(PgConn!, marker, seeded);

            // ── control: tenant A sees its own markers ───────────────────────────────────────────────
            await _fx.LoginAsync();
            await Assertions.Expect(await OpenBoardAsync()).ToContainTextAsync(marker, new() { Timeout = 15_000 });
            await Assertions.Expect(await OpenBoardRunnersAsync()).ToContainTextAsync(marker);
            await Assertions.Expect(await OpenToolCallsAsync()).ToContainTextAsync(marker, new() { Timeout = 15_000 });

            // ── tenant B through the public signup saga ──────────────────────────────────────────────
            await _fx.Context.ClearCookiesAsync();
            await _fx.Page.GotoAsync("/signup");
            await _fx.Page.GetByPlaceholder("acme (optional)").FillAsync(tenantB);
            await _fx.Page.GetByPlaceholder("Acme Corp").FillAsync($"Isolation {id}");
            await _fx.Page.GetByPlaceholder("alice", new() { Exact = true }).FillAsync(userB);
            var passwords = _fx.Page.Locator("input[type=password]");
            await passwords.Nth(0).FillAsync(passwordB);
            await passwords.Nth(1).FillAsync(passwordB);
            await _fx.Page.GetByRole(AriaRole.Button, new() { Name = "Create workspace" }).ClickAsync();
            await _fx.Page.WaitForURLAsync("**/signup/verify-email", new() { Timeout = 30_000 });
            seeded.TenantB = tenantB;

            await _fx.LoginAsync(userB, passwordB);

            // ── isolation: none of A's markers leak into B ───────────────────────────────────────────
            var board = await OpenBoardAsync();
            await Assertions.Expect(board.GetByRole(AriaRole.Heading, new() { Name = "Projects", Exact = true })).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(board).Not.ToContainTextAsync(marker);
            var runners = await OpenBoardRunnersAsync();
            await Assertions.Expect(runners).Not.ToContainTextAsync(marker);

            var evidence = await OpenToolCallsAsync();
            await Assertions.Expect(evidence).Not.ToContainTextAsync(marker);

            var users = await OpenSettingsTabAsync("Users");
            await Assertions.Expect(users).ToContainTextAsync(userB, new() { Timeout = 15_000 });
            await Assertions.Expect(users).Not.ToContainTextAsync("operator");

            await _fx.Page.Locator(".tb-activities").ClickAsync();   // Insights is unpinned (Home holds the dock slot)
            await _fx.Page.Locator(".ko-tile", new() { HasTextString = "Insights" }).First.ClickAsync();
            var insights = _fx.Page.Locator(".appwin.focused");
            await Assertions.Expect(insights.Locator(".appwin-title")).ToHaveTextAsync("Insights");
            await Assertions.Expect(insights).Not.ToContainTextAsync(marker);
        }
        finally
        {
            await CleanupAsync(PgConn!, seeded, userB);
        }
    }

    // ── UI helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<ILocator> OpenBoardAsync()
    {
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");
        await win.Locator(".board-tab", new() { HasTextString = "Projects" }).ClickAsync();
        return win;
    }

    private async Task<ILocator> OpenBoardRunnersAsync()
    {
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".board-tab", new() { HasTextString = "Computers" }).ClickAsync();
        await Assertions.Expect(win.Locator(".data-table").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        return win;
    }

    // Evidence is the "Tool calls" view of Settings → Activity log.
    private async Task<ILocator> OpenToolCallsAsync()
    {
        var win = await OpenSettingsTabAsync("Activity log");
        await win.Locator(".board-tab", new() { HasTextString = "Tool calls" }).ClickAsync();
        return win;
    }

    private async Task<ILocator> OpenSettingsTabAsync(string tab)
    {
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Settings");
        await win.Locator(".prefs-cat", new() { HasTextString = tab }).First.ClickAsync();
        return win;
    }

    // ── seed + cleanup ──────────────────────────────────────────────────────────────────────────────

    private sealed class SeededRows
    {
        public Guid Workspace;
        public Guid Runner;
        public Guid Invocation;
        public Guid Run;
        public string? TenantB;
    }

    /// <summary>One marker row per tenant-scoped surface, all for tenant A. The runner is created already
    /// Revoked with a random hash — it can never authenticate.</summary>
    private static async Task SeedTenantAAsync(string cs, string marker, SeededRows rows)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces.workspaces ("Id","TenantId","Name","Kind","CredentialRef","CreatedAtUtc","Status","ProjectOwner","ProjectNumber","ProjectScope")
            VALUES (gen_random_uuid(), @t, @m, 0, 'e2e-none', now(), 'Connected', @m, 1, '') RETURNING "Id";
            INSERT INTO sessions.runners ("Id","TenantId","OwnerUserId","Label","TokenHash","Status","CreatedAtUtc")
            VALUES (gen_random_uuid(), @t, 'e2e', @m, md5(random()::text), 'Revoked', now()) RETURNING "Id";
            INSERT INTO tools.tool_invocations ("Id","CallId","ToolName","TenantId","Input","Output","IsError","StartedUtc","FinishedUtc")
            VALUES (gen_random_uuid(), @m, @m, @t, '{}', '{}', false, now(), now()) RETURNING "Id";
            INSERT INTO pipeline.pipeline_runs ("Id","TenantId","UserStoryText","Status","TotalCostUsd","TotalTokensIn","TotalTokensOut","IterationCount","CreatedAtUtc","CompletedAtUtc","ResultJson")
            VALUES (gen_random_uuid(), @t, @m, 'Done', 0, 0, 0, 1, now(), now(), '{}'::jsonb) RETURNING "Id";
            """;
        cmd.Parameters.AddWithValue("t", TenantA);
        cmd.Parameters.AddWithValue("m", marker);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>(4);
        do
        {
            while (await reader.ReadAsync()) { ids.Add(reader.GetGuid(0)); }
        }
        while (await reader.NextResultAsync());
        (rows.Workspace, rows.Runner, rows.Invocation, rows.Run) = (ids[0], ids[1], ids[2], ids[3]);
    }

    private static async Task CleanupAsync(string cs, SeededRows rows, string userB)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // Tenant A's markers — exactly the rows this test inserted.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM workspaces.workspaces WHERE "Id" = @w;
                DELETE FROM sessions.runners WHERE "Id" = @r;
                DELETE FROM tools.tool_invocations WHERE "Id" = @i;
                DELETE FROM pipeline.pipeline_runs WHERE "Id" = @p;
                """;
            cmd.Parameters.AddWithValue("w", rows.Workspace);
            cmd.Parameters.AddWithValue("r", rows.Runner);
            cmd.Parameters.AddWithValue("i", rows.Invocation);
            cmd.Parameters.AddWithValue("p", rows.Run);
            await cmd.ExecuteNonQueryAsync();
        }

        // Tenant B: every tenant-scoped row it created (discovered from the catalog), then its registry row.
        if (rows.TenantB is { } tenantB)
        {
            var tables = new List<string>();
            await using (var list = conn.CreateCommand())
            {
                list.CommandText = """
                    SELECT quote_ident(table_schema) || '.' || quote_ident(table_name)
                    FROM information_schema.columns WHERE column_name = 'TenantId' AND table_schema NOT IN ('pg_catalog', 'information_schema')
                    """;
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { tables.Add(reader.GetString(0)); }
            }
            foreach (var table in tables)
            {
                await using var del = conn.CreateCommand();
                del.CommandText = $"DELETE FROM {table} WHERE \"TenantId\" = @t";
                del.Parameters.AddWithValue("t", tenantB);
                await del.ExecuteNonQueryAsync();
            }
            await using var tenant = conn.CreateCommand();
            tenant.CommandText = "DELETE FROM tenants.tenants WHERE \"Id\" = @t";
            tenant.Parameters.AddWithValue("t", tenantB);
            await tenant.ExecuteNonQueryAsync();
        }

        await DeleteKeycloakUserAsync(userB);
    }

    /// <summary>Remove tenant B's Keycloak user through the admin REST API (no-op if signup never created it).</summary>
    private static async Task DeleteKeycloakUserAsync(string username)
    {
        var kc = (Environment.GetEnvironmentVariable("AGENTOS_KC_URL") ?? "https://localhost:8080").TrimEnd('/');
        var adminUser = Environment.GetEnvironmentVariable("AGENTOS_KC_ADMIN_USER") ?? "admin";
        var adminPassword = Environment.GetEnvironmentVariable("AGENTOS_KC_ADMIN_PASSWORD") ?? "admin";
        using var handler = new HttpClientHandler
        {
            // Local dev/lab Keycloak only (loopback) — it serves the ASP.NET dev certificate.
            ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || req.RequestUri?.IsLoopback == true,
        };
        using var http = new HttpClient(handler);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = adminUser,
            ["password"] = adminPassword,
        });
        using var tokenResp = await http.PostAsync($"{kc}/realms/master/protocol/openid-connect/token", form);
        tokenResp.EnsureSuccessStatusCode();
        var token = (await tokenResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var users = await http.GetFromJsonAsync<JsonElement>($"{kc}/admin/realms/agentic/users?username={Uri.EscapeDataString(username)}&exact=true");
        foreach (var user in users.EnumerateArray())
        {
            using var del = await http.DeleteAsync($"{kc}/admin/realms/agentic/users/{user.GetProperty("id").GetString()}");
            del.EnsureSuccessStatusCode();
        }
    }
}
