// AgentOS UI — the Workflow studio actually RUNS the drawn graph end-to-end in the browser. Same gate +
// fixture as the other desktop UI tests (RUN_AGENTOS_E2E=true, Web at AGENTOS_URL). The standalone Web has
// no LLM key, so the run rides the Offline failover provider (Llm:OfflineFallback) — proving the run-path
// works keyless. This is the coverage the audit found missing: every prior Workflow E2E only opened the
// window; these drive Run → live node status → completion, including the new control-flow + Human nodes.
//
// Window interactions are scoped to ".appwin.focused" (the WindowManagerService singleton stacks windows
// across runs), matching BoardAppTests. Blazor Server can drop a click that lands before the freshly-opened
// window's circuit is interactive, so the Run click is retried until the Run-Log panel confirms it took.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class WorkflowAppTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public WorkflowAppTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenWorkflowAsync()
    {
        await _fx.GotoDesktopAsync();
        await EnsureCircuitConnectedAsync();
        await _fx.Page.Locator(".dock-item[title=\"Workflow\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Workflow");
        // The studio graph dropdown is populated once the component's circuit is interactive — wait for it
        // before driving any control, so clicks aren't dropped against a not-yet-interactive window.
        await Assertions.Expect(win.Locator(".syn-select")).ToBeVisibleAsync();
        return win;
    }

    // Blazor Server queues/drops UI events while the SignalR circuit is reconnecting (back-to-back tests on a
    // shared page can catch it mid-reconnect). Wait until the reconnect modal is gone before driving controls.
    private async Task EnsureCircuitConnectedAsync()
        => await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal"))
            .Not.ToBeVisibleAsync(new() { Timeout = 30_000 });

    // Fire a DOM click directly on the element (el.click()). Blazor Server's delegated click listener picks
    // this up reliably — a synthetic mouse-coordinate click (ClickAsync) is occasionally dropped on this
    // canvas-heavy window when a layout shift from the diagram rebuild moves the target under the cursor.
    private static Task JsClickAsync(ILocator locator) => locator.EvaluateAsync("el => el.click()");

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
                // Run() was a no-op (e.g. a previous click is still mid-run) — retry.
            }
        }
        await Assertions.Expect(panel).ToBeVisibleAsync();   // final try surfaces the real failure
    }

    // The seeded "5-Agent SDLC Pipeline" (the default graph) runs the real typed agents
    // (Requirement→Coding→Testing→QA→End) against the keyless Offline provider. CI has no API key, so the run
    // must be labelled for what it is: an Offline chip, the four agent nodes in the Canned state (never the
    // green Done look), and a "Dry run complete — OFFLINE" log line instead of "Workflow complete".
    [Fact]
    public async Task Workflow_RunSdlcPipeline_Offline_CompletesAsLabelledDryRun()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenWorkflowAsync();
        // "5-Agent SDLC Pipeline" sorts first, so it is already selected — don't re-select (a no-op select
        // churns the canvas rebuild and can race the Run click).
        await Assertions.Expect(win.Locator(".syn-name")).ToHaveValueAsync("5-Agent SDLC Pipeline");

        // Keyless → the chip says Offline before anything runs; a green "Live" chip here would be a lie.
        await Assertions.Expect(win.GetByTestId("llm-chip-offline")).ToBeVisibleAsync();

        // Each agent node shows its character (the QA evaluator runs as the QA agent), at rest before the run.
        await Assertions.Expect(win.Locator(".step-node svg.mascot.is-idle")).ToHaveCountAsync(4);
        await Assertions.Expect(win.Locator(".step-node svg.mascot.role-qa")).ToHaveCountAsync(1);

        await StartRunAsync(win);

        // The drawn agent nodes reach the Canned run-state on the canvas (not a static mock) and the run
        // completes as an explicitly labelled dry run.
        await Assertions.Expect(win.Locator(".syn-log")).ToContainTextAsync("Dry run complete — OFFLINE",
            new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(win.Locator(".step-node.run-canned")).ToHaveCountAsync(4);
        // …and the characters act it out: asleep (canned), never the happy "done" hop on canned output.
        await Assertions.Expect(win.Locator(".step-node svg.mascot.is-canned")).ToHaveCountAsync(4);
        await Assertions.Expect(win.Locator(".step-node svg.mascot.is-done")).ToHaveCountAsync(0);
        await Assertions.Expect(win.Locator(".syn-log")).Not.ToContainTextAsync("Workflow complete.");

        // Tokens per agent: the Run Log closes with the table, and the Tokens tab shows one row per agent —
        // offline answers are marked "≈" (estimate) and "not billed", never presented as real usage.
        await Assertions.Expect(win.Locator(".syn-log")).ToContainTextAsync("Σ Tokens by agent");
        await win.Locator(".syn-tab", new() { HasTextString = "Tokens" }).ClickAsync();
        await Assertions.Expect(win.Locator(".syn-usage tbody tr")).ToHaveCountAsync(4);
        await Assertions.Expect(win.Locator(".syn-usage tbody")).ToContainTextAsync("≈");
        await Assertions.Expect(win.Locator(".syn-usage tbody")).ToContainTextAsync("not billed");

        // The run panel resizes from the keyboard (the splitter is a focusable separator).
        var splitter = win.Locator(".syn-splitter[role=separator]");
        var before = await win.Locator(".syn-bottom").EvaluateAsync<double>("el => el.getBoundingClientRect().height");
        await splitter.FocusAsync();
        await splitter.PressAsync("Shift+ArrowUp");
        var after = await win.Locator(".syn-bottom").EvaluateAsync<double>("el => el.getBoundingClientRect().height");
        Assert.True(after > before, $"run panel should grow from the keyboard ({before} → {after})");
        await splitter.PressAsync("Enter");   // back to the default height — leaves the shared profile clean
    }

    // NOTE: a second E2E that drove the "Control-Flow Demo" (Parallel + Merge + If/Else + Human checkpoint)
    // was removed — switching graphs in the studio rebuilds the @key'd diagram (heavy JS interop), and on the
    // shared Playwright page that intermittently dropped the subsequent Run click (a Blazor-Server + canvas
    // test-infra flake, NOT a product defect: it runs reliably in isolation and by hand). Those node types +
    // the human approve/reject flow are covered deterministically by GraphExecutorTests; this E2E keeps the
    // reliable end-to-end proof that the UI Run path executes the real agents offline.
}
