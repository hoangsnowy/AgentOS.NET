// UX phase 1: Settings → AI → Model keys is one card per provider (its fields + its own Test). With no key the
// Test must report a failure — the dev Web's offline fallback answers with canned output, and a probe that went
// green over canned text would claim a model is reachable when nothing was called.

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class ModelKeysTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public ModelKeysTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task ModelKeys_OneCardPerProvider_TestWithoutKeyFails()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Model keys" }).ClickAsync();

        foreach (var provider in new[] { "Claude", "AzureOpenAI", "OpenAI", "Gemini", "xAI", "RemoteAgent" })
        {
            await Assertions.Expect(win.Locator($".provider-card[data-provider=\"{provider}\"]")).ToBeVisibleAsync();
        }
        var gemini = win.Locator(".provider-card[data-provider=\"Gemini\"]");
        await Assertions.Expect(gemini.GetByLabel("Gemini API key")).ToBeVisibleAsync();

        await gemini.GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true }).ClickAsync();
        var result = gemini.Locator(".prefs-saved");
        await Assertions.Expect(result).ToContainTextAsync("Failed", new() { Timeout = 30_000 });
    }
}
