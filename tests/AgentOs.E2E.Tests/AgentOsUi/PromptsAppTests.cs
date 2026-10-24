// Settings → AI → Prompts: the workspace rules box and the Board agents' prompts render. The rules placeholder used
// "&#10;" in markup, which a component attribute renders as text — the example showed literal entities instead of
// three lines.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class PromptsAppTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public PromptsAppTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Prompts_WorkspaceRulesExample_IsThreeLinesWithoutEntities_AndBoardAgentsAreListed()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Prompts" }).ClickAsync();

        var rules = win.Locator("textarea[aria-label=\"Workspace rules\"]");
        await Assertions.Expect(rules).ToBeVisibleAsync();
        var placeholder = await rules.GetAttributeAsync("placeholder") ?? string.Empty;
        Assert.DoesNotContain("&#", placeholder, System.StringComparison.Ordinal);
        Assert.Equal(3, placeholder.Split('\n').Length);

        await Assertions.Expect(win.GetByText("Board agents", new() { Exact = true })).ToBeVisibleAsync();
    }
}
