// AgentOS.NET desktop UI — REAL-AUTH Playwright fixture. Unlike AgentOsPageFixture (dev-auto-login + a
// localStorage flag against a standalone Web), this drives the actual Keycloak OIDC login against the
// full Aspire stack (AppHost): https://localhost:5180 → redirect to Keycloak → fill credentials →
// back to the authenticated desktop. This is the true end-to-end auth path.
//
//   - Gate: RUN_AGENTOS_E2E_REAL=true (separate from RUN_AGENTOS_E2E so the fast dev-auth suite and the
//     full-stack suite never run by accident together).
//   - Target: AGENTOS_REAL_URL (default https://localhost:5180 — the AppHost Web). HTTPS dev cert is
//     accepted via IgnoreHTTPSErrors.
//   - Credentials: the realm-seeded `operator` / `operator` (tenant=default, role=admin) from
//     infra/keycloak/agentic-realm.json.

using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class AgentOsRealAuthFixture : IAsyncLifetime
{
    private static readonly string[] PlaywrightInstallArgs = ["install", "chromium"];

    public const string SkipReason =
        "Real-auth E2E: set RUN_AGENTOS_E2E_REAL=true and start the full stack " +
        "(dotnet run --project infra/AgentOs.AppHost) so Keycloak + Web are up at AGENTOS_REAL_URL " +
        "(default https://localhost:5180) before running.";

    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("AGENTOS_REAL_URL") ?? "https://localhost:5180";

    public IPlaywright Playwright { get; private set; } = default!;
    public IBrowser Browser { get; private set; } = default!;
    public IBrowserContext Context { get; private set; } = default!;
    public IPage Page { get; private set; } = default!;

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true",
            StringComparison.OrdinalIgnoreCase);

    public async ValueTask InitializeAsync()
    {
        if (!IsEnabled) { return; }
        Assertions.SetDefaultExpectTimeout(AgentOsPageFixture.ExpectTimeoutMs);

        Microsoft.Playwright.Program.Main(PlaywrightInstallArgs);
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !string.Equals(Environment.GetEnvironmentVariable("PWDEBUG"), "1", StringComparison.Ordinal),
        });
        Context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true, // AppHost Web serves an https dev cert on :5180
        });
        Page = await Context.NewPageAsync();
    }

    /// <summary>Navigate to the desktop and complete the real Keycloak login. The desktop is globally
    /// [Authorize] → "/" redirects to the Keycloak login form (standard ids username/password/kc-login).
    /// On success the OIDC callback lands back on the desktop; we wait for the dock to render.</summary>
    public async Task LoginAsync(string username = "operator", string password = "operator")
    {
        // Tests in a class share this browser context: without a reset, the second test's navigation rides the
        // first test's Keycloak session straight into the desktop and never shows #username (and a test that
        // wants a different user would silently stay signed in as the previous one). Start every login clean.
        await Context.ClearCookiesAsync();
        await Page.GotoAsync("/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.Locator("#username").WaitForAsync(new() { Timeout = 30000 });
        await Page.Locator("#username").FillAsync(username);
        await Page.Locator("#password").FillAsync(password);
        await Page.Locator("#kc-login, button[type=submit]").First.ClickAsync();
        await Page.Locator(".dock").WaitForAsync(new() { Timeout = 30000 });
        await Page.Locator(".topbar").WaitForAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Page is not null) { await Page.CloseAsync(); }
        if (Context is not null) { await Context.CloseAsync(); }
        if (Browser is not null) { await Browser.CloseAsync(); }
        Playwright?.Dispose();
    }
}
