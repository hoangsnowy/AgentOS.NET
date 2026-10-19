// UX phase 2: the Terminal and Files apps are Board tabs (Log, Files) — the dock is four apps — and the Home
// checklist's "Pair" step lands on Board → Runners through a launch tab.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class BoardTabsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public BoardTabsTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Board_HostsLogAndFiles_DockIsFourApps()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await Assertions.Expect(_fx.Page.Locator(".dock-item")).ToHaveCountAsync(4);

        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var board = _fx.Page.Locator(".appwin.focused");
        await board.Locator(".board-tab", new() { HasTextString = "Log" }).ClickAsync();
        await Assertions.Expect(board.Locator(".board-embed .term")).ToBeVisibleAsync();
        await board.Locator(".board-tab", new() { HasTextString = "Files" }).ClickAsync();
        await Assertions.Expect(board.Locator(".board-embed .files")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task HomeChecklist_Pair_OpensBoardOnRunners()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();

        await _fx.Page.Locator(".dock-item[title=\"Home\"]").First.ClickAsync();
        var home = _fx.Page.Locator(".appwin.focused");
        await home.Locator(".home-step[data-step=\"pair\"]").GetByRole(AriaRole.Button, new() { Name = "Pair" }).ClickAsync();
        var board = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(board.Locator(".appwin-title")).ToHaveTextAsync("Board");
        await Assertions.Expect(board.Locator(".board-tab.active")).ToContainTextAsync("Runners");
    }
}
