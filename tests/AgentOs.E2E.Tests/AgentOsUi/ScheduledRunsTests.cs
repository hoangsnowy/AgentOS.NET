// Scheduled runs (parity item 3): from Home, schedule a story (presets are cron underneath, UTC), see it in the
// Scheduled list, refuse a too-frequent cron, and delete it again (no residue).

using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class ScheduledRunsTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public ScheduledRunsTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task Home_Schedule_Weekdays_ListsIt_RefusesEveryMinute_ThenDeletes()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }
        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Home\"]").First.ClickAsync();
        var home = _fx.Page.Locator(".appwin.focused");
        var story = home.GetByLabel("What should the agents build?");

        // Too frequent: every minute is refused (each run spends real tokens).
        await story.FillAsync("Nightly dependency audit");
        await home.GetByRole(AriaRole.Button, new() { Name = "Schedule…" }).ClickAsync();
        await home.GetByLabel("When").SelectOptionAsync("cron");
        await home.GetByLabel("Cron (UTC)").FillAsync("* * * * *");
        await home.GetByRole(AriaRole.Button, new() { Name = "Save schedule" }).ClickAsync();
        await Assertions.Expect(home.Locator(".home-schedule [role=alert]")).ToContainTextAsync("15 minutes");

        // A sane preset is saved and listed.
        await home.GetByLabel("When").SelectOptionAsync("weekdays");
        await home.GetByLabel("Time (UTC)").FillAsync("09:00");
        await home.GetByRole(AriaRole.Button, new() { Name = "Save schedule" }).ClickAsync();
        var row = home.Locator(".home-run[data-schedule]", new() { HasTextString = "Nightly dependency audit" });
        await Assertions.Expect(row).ToContainTextAsync("Weekdays at 09:00 UTC");
        await Assertions.Expect(row).ToContainTextAsync("Active");

        await row.GetByRole(AriaRole.Button, new() { Name = "Pause" }).ClickAsync();
        await Assertions.Expect(row).ToContainTextAsync("Paused");
        await row.GetByRole(AriaRole.Button, new() { Name = "Delete schedule: Nightly dependency audit" }).ClickAsync();
        await Assertions.Expect(row).ToHaveCountAsync(0);
    }
}
