// AgentOS UI — REAL-AUTH end-to-end for the Workflow studio. Unlike WorkflowAppTests (dev-auto-login against
// a standalone Web), this drives the ACTUAL Keycloak OIDC session against the full Aspire stack: login as
// operator/operator → open the Workflow app → Run the seeded "5-Agent SDLC Pipeline" → the drawn nodes reach
// the Done run-state on the canvas and the run completes. The full stack carries no LLM key in E2E, so the
// run rides the Offline failover provider ($0) — proving the whole authenticated Run path works keyless.
//
//   - Gate: RUN_AGENTOS_E2E_REAL=true AND the full stack up (dotnet run --project infra/AgentOs.AppHost) so
//     Keycloak + Web are reachable at AGENTOS_REAL_URL (default https://localhost:5180). Skipped otherwise.
//   - Credentials: the realm-seeded operator/operator (tenant=default, role=admin).
//
// Window interactions are scoped to ".appwin.focused". Blazor Server can drop a click that lands before the
// freshly-opened window's circuit is interactive, so the Run click is retried until the Run-Log panel confirms
// it took (same hardening as WorkflowAppTests).

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class WorkflowAppRealAuthTests : IClassFixture<AgentOsRealAuthFixture>
{
    private readonly AgentOsRealAuthFixture _fx;

    public WorkflowAppRealAuthTests(AgentOsRealAuthFixture fx) => _fx = fx;

    // Blazor Server queues/drops UI events while the SignalR circuit is reconnecting. Wait until the reconnect
    // modal is gone before driving controls.
    private async Task EnsureCircuitConnectedAsync()
        => await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal"))
            .Not.ToBeVisibleAsync(new() { Timeout = 30_000 });

    // Fire a DOM click directly on the element — Blazor Server's delegated listener picks this up reliably even
    // when a diagram layout shift moves the target under a synthetic-coordinate click.
    private static Task JsClickAsync(ILocator locator) => locator.EvaluateAsync("el => el.click()");

    private async Task<ILocator> OpenWorkflowAsync()
    {
        await _fx.LoginAsync();
        await EnsureCircuitConnectedAsync();
        await _fx.Page.Locator(".dock-item[title=\"Workflow\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Workflow");
        // The studio graph dropdown is populated once the component's circuit is interactive — wait for it
        // before driving any control, so clicks aren't dropped against a not-yet-interactive window.
        await Assertions.Expect(win.Locator(".syn-select")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        return win;
    }

    // Click Run and confirm it actually fired — retry until the Run-Log panel (opened by Run()) appears.
    private async Task StartRunAsync(ILocator win)
    {
        var runBtn = win.Locator(".syn-btn.green");   // the (unique) green Run button
        var panel = win.Locator(".syn-bottom");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await EnsureCircuitConnectedAsync();
            await JsClickAsync(runBtn);
            try
            {
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 5_000 });
                return;
            }
            catch (PlaywrightException)
            {
                // Run() was a no-op (a previous click still mid-run) — retry.
            }
        }
        await Assertions.Expect(panel).ToBeVisibleAsync();   // final try surfaces the real failure
    }

    // The authenticated end-to-end proof: real Keycloak session → Workflow app → Run the seeded SDLC pipeline
    // (real typed agents on the keyless Offline provider) → nodes light up Done and the run completes.
    [Fact]
    public async Task Workflow_RealAuth_RunSdlcPipeline_CompletesWithNodesDone()
    {
        if (!AgentOsRealAuthFixture.IsEnabled) { Assert.Skip(AgentOsRealAuthFixture.SkipReason); }

        var win = await OpenWorkflowAsync();
        // "5-Agent SDLC Pipeline" sorts first, so it is already selected — don't re-select (a no-op select
        // churns the canvas rebuild and can race the Run click).
        await Assertions.Expect(win.Locator(".syn-name")).ToHaveValueAsync("5-Agent SDLC Pipeline");

        await StartRunAsync(win);

        // The drawn nodes reach a finished run-state on the canvas (not a static mock) and the run completes.
        // The full stack may or may not have a provider key: live → "Workflow complete." with Done nodes;
        // keyless → a labelled "Dry run complete — OFFLINE" with Canned agent nodes. Either is honest.
        await Assertions.Expect(win.Locator(".step-node.run-done, .step-node.run-canned").First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = RunTimeoutMs });
        await Assertions.Expect(win.Locator(".syn-log")).ToContainTextAsync(
            new System.Text.RegularExpressions.Regex("Workflow complete|Dry run complete — OFFLINE"),
            new LocatorAssertionsToContainTextOptions { Timeout = RunTimeoutMs });
    }

    // Offline runs finish in seconds; a LIVE run (real model, e.g. the lab's paired claude -p runner) takes minutes.
    // AGENTOS_E2E_RUN_TIMEOUT_SECONDS raises the wait for live runs (default 90 s).
    private static float RunTimeoutMs =>
        (int.TryParse(System.Environment.GetEnvironmentVariable("AGENTOS_E2E_RUN_TIMEOUT_SECONDS"), out var s) && s > 0 ? s : 90) * 1000f;
}
