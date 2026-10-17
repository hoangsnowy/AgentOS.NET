// REAL-AUTH E2E — System → About on the full Aspire stack (dev or lab): every service is part of the run and
// must be reachable — Web, Api, Keycloak, PostgreSQL, SMTP (MailHog) — each with a version or greeting, and
// the platform admin (operator) sees the Infrastructure details. Gated by RUN_AGENTOS_E2E_REAL=true.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AboutServicesRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public AboutServicesRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    [Fact]
    public async Task About_FullStack_EveryServiceHealthy_WithVersions()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        await _fx.LoginAsync(); // operator — admin + platform-admin
        var win = await AboutServicesTests.OpenAboutAsync(_fx.Page);

        foreach (var id in new[] { "web", "api", "keycloak", "postgres", "smtp" })
        {
            var card = win.Locator($".about-row[data-service=\"{id}\"]");
            await Assertions.Expect(card.Locator(".about-state")).ToHaveTextAsync("Healthy", new() { Timeout = 15_000 });
            await Assertions.Expect(card.Locator(".about-ver")).ToBeVisibleAsync();
        }

        // Healthy rows start closed; expanding one shows its facts (keyboard-reachable native <details>).
        var postgres = win.Locator(".about-row[data-service=\"postgres\"]");
        await Assertions.Expect(postgres).Not.ToHaveAttributeAsync("open", "");
        await postgres.Locator("summary").ClickAsync();
        await Assertions.Expect(postgres.Locator(".about-row-body")).ToBeVisibleAsync();
        await Assertions.Expect(postgres).ToContainTextAsync("Schema · pipeline");
        await Assertions.Expect(postgres).ToContainTextAsync("migrations · latest");
        await Assertions.Expect(win.Locator(".about-row[data-service=\"keycloak\"] summary")).ToContainTextAsync("Realm agentic");

        // Platform admin: the infrastructure section is there and carries no secrets.
        await Assertions.Expect(postgres.Locator(".about-infra")).ToContainTextAsync("Host");
        await Assertions.Expect(win).Not.ToContainTextAsync("Password=");
    }
}
