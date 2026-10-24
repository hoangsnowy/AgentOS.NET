// Ticket files (ADR-0015) — "Files for the AI" in the desktop: attach files (multiple, and a whole folder that keeps
// each file's place), see them listed with size and who added them, get a plain-language refusal for an unusable name,
// remove them. Runs against the standalone Web (no database): ticket files use the Development in-memory store there,
// so the flow is real end to end. Board › New idea hosts the section before any project exists; Board › Tasks and the
// Work dialog use the same component. The three limits show on Settings › Autopilot.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class TicketFilesTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public TicketFilesTests(AgentOsPageFixture fx) => _fx = fx;

    [Fact]
    public async Task NewIdea_FilesForTheAi_UploadFilesAndAFolder_ListThem_RefuseABadName_RemoveThem()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await _fx.Page.Locator(".dock-item[title=\"Board\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await Assertions.Expect(win.Locator(".appwin-title")).ToHaveTextAsync("Board");
        await win.Locator(".board-tab", new() { HasTextString = "New idea" }).ClickAsync();

        var section = win.Locator("[data-testid=ticket-files]");
        await Assertions.Expect(section).ToBeVisibleAsync();
        await Assertions.Expect(section).ToContainTextAsync("Files the AI can use for this ticket");
        await Assertions.Expect(section).ToContainTextAsync("nothing is downloaded from the internet");
        await Assertions.Expect(section.Locator("[data-testid=ticket-files-empty]")).ToBeVisibleAsync();

        // Keyboard: the picker is a real, focusable file input.
        var input = section.Locator("[data-testid=ticket-files-input]");
        await input.FocusAsync();
        await Assertions.Expect(input).ToBeFocusedAsync();

        // Two files at once.
        await input.SetInputFilesAsync(new[]
        {
            new FilePayload { Name = "logo.png", MimeType = "image/png", Buffer = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3] },
            new FilePayload { Name = "brief.md", MimeType = "text/markdown", Buffer = Encoding.UTF8.GetBytes("# Brief\nUse the logo.") },
        });
        var table = section.Locator(".ticket-files-table");
        await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(2);
        await Assertions.Expect(table).ToContainTextAsync("logo.png");
        await Assertions.Expect(table).ToContainTextAsync("7 bytes");

        // A whole folder keeps each file's place in it.
        var folder = Path.Join(Path.GetTempPath(), "agentos-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var skill = Path.Join(folder, "design-skill");
        Directory.CreateDirectory(Path.Join(skill, "assets"));
        await File.WriteAllTextAsync(Path.Join(skill, "SKILL.md"), "# Design skill");
        await File.WriteAllTextAsync(Path.Join(skill, "assets", "tokens.json"), "{}");
        try
        {
            await section.GetByLabel("Add a whole folder for the AI").SetInputFilesAsync(skill);
            await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(4);
            await Assertions.Expect(table).ToContainTextAsync("design-skill/assets/tokens.json");
            await Assertions.Expect(table).ToContainTextAsync("design-skill/SKILL.md");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // A name Windows can't hold is refused, in plain words.
        await input.SetInputFilesAsync(new FilePayload { Name = "con.txt", MimeType = "text/plain", Buffer = [1] });
        await Assertions.Expect(section.Locator(".ticket-files-errors")).ToContainTextAsync("reserved name");
        await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(4);

        // Remove every file again (zero residue), one button each.
        foreach (var path in new[] { "brief.md", "logo.png", "design-skill/SKILL.md", "design-skill/assets/tokens.json" })
        {
            await section.GetByRole(AriaRole.Button, new() { Name = $"Remove {path}" }).ClickAsync();
            await Assertions.Expect(table.Locator("code", new() { HasTextString = path })).ToHaveCountAsync(0);
        }
        await Assertions.Expect(section.Locator("[data-testid=ticket-files-empty]")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AutopilotSettings_ShowTheThreeFileLimits()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await Assertions.Expect(_fx.Page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync(new() { Timeout = 30_000 });
        await _fx.Page.Locator(".dock-item[title=\"Settings\"]").First.ClickAsync();
        var win = _fx.Page.Locator(".appwin.focused");
        await win.Locator(".prefs-cat", new() { HasTextString = "Autopilot" }).ClickAsync();
        await Assertions.Expect(win.Locator(".page-title")).ToHaveTextAsync("Autopilot");

        await Assertions.Expect(win.Locator("input[aria-label=\"Largest file (MB)\"]")).ToHaveValueAsync("10");
        await Assertions.Expect(win.Locator("input[aria-label=\"All files of a ticket (MB)\"]")).ToHaveValueAsync("50");
        await Assertions.Expect(win.Locator("input[aria-label=\"Files per ticket\"]")).ToHaveValueAsync("500");
    }
}
