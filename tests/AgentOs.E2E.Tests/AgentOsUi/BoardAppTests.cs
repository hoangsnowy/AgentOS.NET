// AgentOS.NET UI — the Board app: connected Boards (GitHub Projects v2), paired Runners, Sessions.
// Same gate + fixture as the other desktop UI tests: skipped unless RUN_AGENTOS_E2E=true with a
// Web running at AGENTOS_URL. The register-runner flow needs no DB — the one-time pairing token is
// minted by the real IRunnerPairingService, so the token panel appears even against Null repos.
//
// All window interactions are scoped to ".appwin.focused" (the newest, top-most window). The Web's
// WindowManagerService is a singleton, so windows opened by earlier tests/runs persist and stack;
// scoping to the focused window keeps these tests robust against that accumulation.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class BoardAppTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public BoardAppTests(AgentOsPageFixture fx) => _fx = fx;

    private async Task<ILocator> OpenBoardAsync()
    {
        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");
        return win;
    }

    // Open the Board app from its desktop icon and confirm the four panes render.
    [Fact]
    public async Task Board_Opens_ShowsTabs()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenBoardAsync();

        await Assertions.Expect(win.Locator(".board-tab", new() { HasTextString = "Projects" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".board-tab", new() { HasTextString = "New idea" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".board-tab", new() { HasTextString = "Computers" })).ToBeVisibleAsync();
        await Assertions.Expect(win.Locator(".board-tab", new() { HasTextString = "Tasks" })).ToBeVisibleAsync();
    }

    // The Boards tab shows the connect-a-board form.
    [Fact]
    public async Task Board_BoardsTab_ShowsConnectForm()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenBoardAsync();
        await win.Locator(".board-tab", new() { HasTextString = "Projects" }).ClickAsync();

        await Assertions.Expect(win.GetByPlaceholder("ghp_… (needs repo + read:project)")).ToBeVisibleAsync();
        await Assertions.Expect(win.GetByRole(AriaRole.Button, new() { Name = "Connect" })).ToBeVisibleAsync();
    }

    // Register a runner and confirm the one-time pairing token (REMOTE_AGENT_ID/TOKEN) is shown.
    [Fact]
    public async Task Board_RegisterRunner_ShowsOneTimePairingToken()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        var win = await OpenBoardAsync();

        // Navigate to Runners tab (default is now Boards). Fill a label and register. (The register
        // controls sit in the body interior, clear of the window's thin edge resize-handles.)
        await win.Locator(".board-tab", new() { HasTextString = "Computers" }).ClickAsync();
        await win.GetByPlaceholder("My laptop").FillAsync("CI test runner");
        await win.GetByRole(AriaRole.Button, new() { Name = "Connect this computer" }).ClickAsync();

        var token = win.Locator(".cred-readout textarea");
        await Assertions.Expect(token).ToBeVisibleAsync();
        await Assertions.Expect(token).ToContainTextAsync("REMOTE_AGENT_TOKEN");
        await Assertions.Expect(token).ToContainTextAsync("REMOTE_AGENT_ID");
    }
}
