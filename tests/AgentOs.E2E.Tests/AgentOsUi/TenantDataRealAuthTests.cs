// REAL-AUTH E2E — E1.4 on the full stack: a tenant created through /signup is exported (every row it owns,
// secrets excluded) and then PURGED from Settings → Data by the platform admin. Afterwards no TenantId table holds
// a row of it, its registry row is gone, its Keycloak user is gone, and the purge is in the platform audit trail.
//   - Gates: RUN_AGENTOS_E2E_REAL=true + AGENTOS_PG_CONN (+ AGENTOS_KC_URL / admin creds as TenantIsolationRealAuthTests).

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

public sealed class TenantDataRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public TenantDataRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    private static string PgConn => Environment.GetEnvironmentVariable("AGENTOS_PG_CONN") ?? string.Empty;

    [Fact]
    public async Task Tenant_ExportsEverything_ThenPurgesToZero()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }
        if (PgConn.Length == 0) { Assert.Skip("AGENTOS_PG_CONN not set."); }

        var id = Guid.NewGuid().ToString("N")[..10];
        var tenant = $"purge-{id}";
        var user = $"purge{id}";
        var password = $"Purge-{id}-Pass9!";
        var marker = $"e2e-purge-{id}";
        var purged = false;
        try
        {
            // A real tenant, through the public signup saga.
            await _fx.Context.ClearCookiesAsync();
            await _fx.Page.GotoAsync("/signup");
            await _fx.Page.GetByPlaceholder("acme (optional)").FillAsync(tenant);
            await _fx.Page.GetByPlaceholder("Acme Corp").FillAsync($"Purge {id}");
            await _fx.Page.GetByPlaceholder("alice", new() { Exact = true }).FillAsync(user);
            var pw = _fx.Page.Locator("input[type=password]");
            await pw.Nth(0).FillAsync(password);
            await pw.Nth(1).FillAsync(password);
            await _fx.Page.GetByRole(AriaRole.Button, new() { Name = "Create workspace" }).ClickAsync();
            await _fx.Page.WaitForURLAsync("**/signup/verify-email", new() { Timeout = 30_000 });

            // Data it owns: a board, a run with a metric (FK child), and a runner whose token hash must not export.
            await SeedAsync(tenant, marker);

            // Export (platform admin, other tenant).
            await _fx.LoginAsync();   // operator: admin + platform-admin
            var export = await _fx.Page.APIRequest.GetAsync($"/tenant/export?tenant={tenant}");
            Assert.Equal(200, export.Status);
            var body = await export.TextAsync();
            using var doc = JsonDocument.Parse(body);
            var tables = doc.RootElement.GetProperty("tables");
            Assert.Equal(tenant, doc.RootElement.GetProperty("tenant").GetString());
            Assert.Contains(marker, tables.GetProperty("workspaces.workspaces").GetRawText(), StringComparison.Ordinal);
            Assert.Equal(1, tables.GetProperty("pipeline.run_metrics").GetArrayLength());
            Assert.DoesNotContain("TokenHash", tables.GetProperty("sessions.runners").GetRawText(), StringComparison.Ordinal);

            // Purge through the UI.
            await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
            var win = _fx.Page.Locator(".appwin.focused");
            await win.Locator(".prefs-cat", new() { HasTextString = "Data" }).First.ClickAsync();
            var row = win.Locator("tr", new() { HasTextString = tenant });
            await row.GetByRole(AriaRole.Button, new() { Name = "Purge…" }).ClickAsync();
            await win.GetByLabel("Type the tenant id to confirm").FillAsync(tenant);
            await win.GetByRole(AriaRole.Button, new() { Name = "Purge tenant" }).ClickAsync();
            await Assertions.Expect(win.Locator(".data-report")).ToContainTextAsync(tenant, new() { Timeout = 30_000 });
            purged = true;

            Assert.Equal(0L, await RemainingRowsAsync(tenant));
            Assert.Equal(0, await KeycloakUsersAsync(user));
            Assert.True(await AuditedAsync(tenant), "tenant.purged missing from the platform audit trail");
        }
        finally
        {
            if (!purged) { await CleanupAsync(tenant, user); }
            await RemoveAuditAsync(tenant);
        }
    }

    private static async Task SeedAsync(string tenant, string marker)
    {
        await using var conn = new NpgsqlConnection(PgConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces.workspaces ("Id","TenantId","Name","Kind","CredentialRef","CreatedAtUtc","Status","ProjectOwner","ProjectScope")
            VALUES (gen_random_uuid(), @t, @m, 0, 'none', now(), 'Connected', '', '');
            WITH r AS (
              INSERT INTO pipeline.pipeline_runs ("Id","TenantId","UserStoryText","Status","TotalCostUsd","TotalTokensIn","TotalTokensOut","IterationCount","CreatedAtUtc","CompletedAtUtc","ResultJson")
              VALUES (gen_random_uuid(), @t, @m, 'Done', 0, 0, 0, 1, now(), now(), '{}'::jsonb) RETURNING "Id")
            INSERT INTO pipeline.run_metrics ("TenantId","RunId","KcId","Iteration","AgentName","Model","Provider","TokensIn","TokensOut","LatencyMs","CostUsd","Success","TimestampUtc")
            SELECT @t, r."Id", 'KC1', 0, 'Seed', 'm', 'p', 1, 1, 1, 0, true, now() FROM r;
            INSERT INTO sessions.runners ("Id","TenantId","OwnerUserId","Label","TokenHash","Status","CreatedAtUtc")
            VALUES (gen_random_uuid(), @t, 'e2e', @m, 'secret-hash', 'Revoked', now());
            """;
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("m", marker);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> RemainingRowsAsync(string tenant)
    {
        await using var conn = new NpgsqlConnection(PgConn);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var list = conn.CreateCommand())
        {
            list.CommandText = "SELECT quote_ident(table_schema)||'.'||quote_ident(table_name) FROM information_schema.columns WHERE column_name = 'TenantId' AND table_schema NOT IN ('pg_catalog','information_schema')";
            await using var r = await list.ExecuteReaderAsync();
            while (await r.ReadAsync()) { tables.Add(r.GetString(0)); }
        }
        long total = 0;
        foreach (var table in tables)
        {
            await using var count = conn.CreateCommand();
            // The platform audit row is written under _platform, so only the tenant's own rows count here.
            count.CommandText = $"SELECT count(*) FROM {table} WHERE \"TenantId\" = @t";
            count.Parameters.AddWithValue("t", tenant);
            total += (long)(await count.ExecuteScalarAsync())!;
        }
        await using var registry = conn.CreateCommand();
        registry.CommandText = "SELECT count(*) FROM tenants.tenants WHERE \"Id\" = @t";
        registry.Parameters.AddWithValue("t", tenant);
        return total + (long)(await registry.ExecuteScalarAsync())!;
    }

    private static async Task<bool> AuditedAsync(string tenant)
    {
        await using var conn = new NpgsqlConnection(PgConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM tenants.audit_events WHERE \"TenantId\" = '_platform' AND \"Action\" = 'tenant.purged' AND \"Target\" LIKE @p";
        cmd.Parameters.AddWithValue("p", tenant + ":%");
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task RemoveAuditAsync(string tenant)
    {
        await using var conn = new NpgsqlConnection(PgConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tenants.audit_events WHERE (\"TenantId\" = '_platform' AND \"Target\" LIKE @p) OR \"TenantId\" = @t";
        cmd.Parameters.AddWithValue("p", tenant + ":%");
        cmd.Parameters.AddWithValue("t", tenant);
        await cmd.ExecuteNonQueryAsync();
    }

    // Safety net when the test fails before the purge ran: remove everything it created.
    private static async Task CleanupAsync(string tenant, string user)
    {
        await using (var conn = new NpgsqlConnection(PgConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM pipeline.run_metrics WHERE "TenantId" = @t; DELETE FROM pipeline.pipeline_runs WHERE "TenantId" = @t;
                DELETE FROM workspaces.workspaces WHERE "TenantId" = @t; DELETE FROM sessions.runners WHERE "TenantId" = @t;
                DELETE FROM tenants.audit_events WHERE "TenantId" = @t; DELETE FROM tenants.tenants WHERE "Id" = @t;
                """;
            cmd.Parameters.AddWithValue("t", tenant);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var userId in await KeycloakUserIdsAsync(user))
        {
            using var http = await KeycloakAdminAsync();
            using var _ = await http.DeleteAsync($"{KcUrl}/admin/realms/agentic/users/{userId}");
        }
    }

    private static string KcUrl => (Environment.GetEnvironmentVariable("AGENTOS_KC_URL") ?? "https://localhost:8080").TrimEnd('/');

    private static async Task<int> KeycloakUsersAsync(string username) => (await KeycloakUserIdsAsync(username)).Count;

    private static async Task<List<string>> KeycloakUserIdsAsync(string username)
    {
        using var http = await KeycloakAdminAsync();
        var users = await http.GetFromJsonAsync<JsonElement>($"{KcUrl}/admin/realms/agentic/users?username={Uri.EscapeDataString(username)}&exact=true");
        var ids = new List<string>();
        foreach (var u in users.EnumerateArray()) { ids.Add(u.GetProperty("id").GetString()!); }
        return ids;
    }

    private static async Task<HttpClient> KeycloakAdminAsync()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || req.RequestUri?.IsLoopback == true,
        };
        var http = new HttpClient(handler, disposeHandler: true);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = Environment.GetEnvironmentVariable("AGENTOS_KC_ADMIN_USER") ?? "admin",
            ["password"] = Environment.GetEnvironmentVariable("AGENTOS_KC_ADMIN_PASSWORD") ?? "admin",
        });
        using var token = await http.PostAsync($"{KcUrl}/realms/master/protocol/openid-connect/token", form);
        token.EnsureSuccessStatusCode();
        var access = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        return http;
    }
}
