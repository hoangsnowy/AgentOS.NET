// UX phase 2: Home is the task-first landing. One story + "Run now" opens Workflow and STARTS the run (no second
// Run click); the admin setup checklist is computed from real state and its first step is adding a model key.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class HomeTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public HomeTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Home_RunNow_StartsTheWorkflowRun_AndShowsTheSetupChecklist()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await _fx.Page.Locator(".dock-item[title=\"Home\"]").First.ClickAsync();
        var home = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(home.Locator(".appwin-title")).ToHaveTextAsync("Home");

        // Dev-auth is an admin with no model key: the checklist leads with it, and the offline note says so.
        await Assertions.Expect(home.Locator(".home-step[data-step=\"key\"]")).ToContainTextAsync("Connect an AI account");
        await Assertions.Expect(home.Locator(".home-note")).ToContainTextAsync("No model key yet");

        var runNow = home.GetByRole(AriaRole.Button, new() { Name = "Run now" });
        await Assertions.Expect(runNow).ToBeDisabledAsync();   // nothing to run without a story
        await home.GetByLabel("What should the agents build?").FillAsync("Add a /health endpoint");
        await runNow.ClickAsync();

        var workflow = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(workflow.Locator(".appwin-title")).ToHaveTextAsync("Workflow");
        await Assertions.Expect(workflow.Locator(".syn-runinput")).ToHaveValueAsync("Add a /health endpoint");
        await Assertions.Expect(workflow.GetByText("▶ Run", new() { Exact = false }).First).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }
}
