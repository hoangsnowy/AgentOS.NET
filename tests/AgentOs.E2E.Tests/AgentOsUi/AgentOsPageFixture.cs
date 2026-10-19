// AgentOS.NET desktop UI — Playwright fixture.
//
// Each test class gets one browser + page. The fixture:
//   - Honours AGENTOS_URL env var (defaults to http://localhost:5180 — see launchSettings.json).
//   - Is skipped unless RUN_AGENTOS_E2E=true (mirrors the existing RUN_E2E gate without conflating
//     the Aspire-bootstrapped suites with the "app already running" suite).
//   - Auto-installs Chromium on first use.
//   - Sets the localStorage signed-in flag before the first navigation so the LoginOverlay does
//     NOT block tests that don't care about it. The login-overlay test class clears it instead.
//
// Why a separate gate from RUN_E2E:
//   The Aspire suites (AspireApiTests, WebUiSmokeTests) need Docker + a 3-minute boot.
//   These UI tests run against a manually-started `dotnet run --project src/AgentOs.Web`
//   for fast inner-loop iteration. Running them under Aspire would also work, but the URL
//   would be dynamic — defer that until CI integration.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

/// <summary>
/// Shared per-test-class Playwright fixture. Boots a Chromium page pointed at AGENTOS_URL.
/// Pre-seeds localStorage so the login overlay is dismissed by default — override in tests
/// that exercise the login flow (call <see cref="ClearAuthAsync"/> before reload).
/// </summary>
public sealed class AgentOsPageFixture : IAsyncLifetime
{
    private static readonly string[] PlaywrightInstallArgs = ["install", "chromium"];

    public const string SkipReason =
        "AgentOS.NET UI E2E: set RUN_AGENTOS_E2E=true and start the Web at AGENTOS_URL " +
        "(default http://localhost:5180) before running.";

    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("AGENTOS_URL") ?? "http://localhost:5180";

    public IPlaywright Playwright { get; private set; } = default!;
    public IBrowser Browser { get; private set; } = default!;
    public IBrowserContext Context { get; private set; } = default!;
    public IPage Page { get; private set; } = default!;

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E"), "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Expect timeout for every E2E assertion: 15 s on CI (GITHUB_ACTIONS), 5 s locally.</summary>
    public static float ExpectTimeoutMs { get; } = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 15_000 : 5_000;

    public async ValueTask InitializeAsync()
    {
        if (!IsEnabled)
        {
            return; // tests will Skip; no Playwright install needed.
        }
        // CI runners are several times slower than a dev box: a cross-window step (open Settings, render a page)
        // can exceed Playwright's 5 s expect default there. One budget for the whole suite, not per assertion.
        Assertions.SetDefaultExpectTimeout(ExpectTimeoutMs);

        // Microsoft.Playwright.Program.Main returns non-zero if browsers already exist — that's fine.
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
        });
        Page = await Context.NewPageAsync();
    }

    /// <summary>Seed the signed-in flag and reload — most tests don't care about login.</summary>
    public async Task GotoDesktopAsync()
    {
        await Page.GotoAsync("/");
        // Set storage AFTER first goto so the origin exists; reload to apply.
        await Page.EvaluateAsync("localStorage.setItem('agentic-signed-in', 'developer')");
        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        // Blazor Server reconnects the SignalR circuit; wait for the dock to render before
        // the first interactive click.
        await Page.Locator(".dock").WaitForAsync();
        await Page.Locator(".topbar").WaitForAsync();
    }

    /// <summary>Clear the signed-in flag — used by the login-overlay test only.</summary>
    public async Task ClearAuthAsync()
    {
        await Page.GotoAsync("/");
        await Page.EvaluateAsync("localStorage.removeItem('agentic-signed-in')");
    }

    public async ValueTask DisposeAsync()
    {
        if (Page is not null) { await Page.CloseAsync(); }
        if (Context is not null) { await Context.CloseAsync(); }
        if (Browser is not null) { await Browser.CloseAsync(); }
        Playwright?.Dispose();
    }
}
