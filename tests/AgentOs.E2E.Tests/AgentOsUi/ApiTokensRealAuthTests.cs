// REAL-AUTH E2E — E1.3 on the full stack: a tenant admin creates a service-account token (scope "run") in Settings →
// API tokens, a machine calls the real Api's /v1 routes with `Authorization: Bearer aos_…` and is authenticated as that
// tenant with the token's scopes; the unversioned route still answers, with a Deprecation header; after Revoke in the UI
// the same call is refused. Cleanup removes the token row and its audit rows.
//   - Gates: RUN_AGENTOS_E2E_REAL=true; Api at AGENTOS_API_URL (default https://localhost:5080); AGENTOS_PG_CONN for cleanup.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Npgsql;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class ApiTokensRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public ApiTokensRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    private static string ApiUrl => (Environment.GetEnvironmentVariable("AGENTOS_API_URL") ?? "https://localhost:5080").TrimEnd('/');

    [Fact]
    public async Task Token_AuthenticatesAgainstTheApi_UntilRevoked()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }
        var name = $"e2e-token-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            await _fx.LoginAsync();
            await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
            var win = _fx.Page.Locator(".appwin.focused");
            await win.Locator(".prefs-cat", new() { HasTextString = "API tokens" }).First.ClickAsync();
            await win.GetByPlaceholder("github-actions-deploy").FillAsync(name);
            await win.GetByRole(AriaRole.Button, new() { Name = "Create token" }).ClickAsync();
            var token = await win.GetByLabel("New API token").InputValueAsync(new() { Timeout = 15_000 });
            Assert.StartsWith("aos_", token, StringComparison.Ordinal);

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || req.RequestUri?.IsLoopback == true,
            };
            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using (var me = await http.GetAsync($"{ApiUrl}/v1/tenants/me"))
            {
                Assert.Equal(HttpStatusCode.OK, me.StatusCode);
                var body = await me.Content.ReadAsStringAsync();
                Assert.Contains("\"default\"", body, StringComparison.Ordinal);   // the operator's tenant
                Assert.Contains($"service:{name}", body, StringComparison.Ordinal);
                Assert.Contains("\"api_token\"", body, StringComparison.Ordinal);
                Assert.False(me.Headers.Contains("Deprecation"));
            }
            using (var legacy = await http.GetAsync($"{ApiUrl}/tenants/me"))
            {
                Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
                Assert.True(legacy.Headers.Contains("Deprecation"));
            }

            var row = win.Locator("tr", new() { HasTextString = name });
            await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
            await Assertions.Expect(row.Locator(".badge")).ToHaveTextAsync("revoked", new() { Timeout = 15_000 });

            using var after = await http.GetAsync($"{ApiUrl}/v1/tenants/me");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("AGENTOS_PG_CONN") is { Length: > 0 } cs)
            {
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM tenants.api_tokens WHERE "Name" = @n;
                    DELETE FROM tenants.audit_events WHERE "Action" LIKE 'apitoken.%' AND "Target" LIKE @p;
                    """;
                cmd.Parameters.AddWithValue("n", name);
                cmd.Parameters.AddWithValue("p", name + " (%");
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
