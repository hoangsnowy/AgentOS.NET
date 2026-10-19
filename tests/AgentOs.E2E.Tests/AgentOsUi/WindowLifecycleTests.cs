// AgentOS.NET UI — window manager + dock + topbar + toast.
// Covers test-plan scenarios 4, 5, 7, 9, 10. Skeletons for 6, 8, 11, 12.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace AgentOs.E2E.Tests.AgentOsUi;

public sealed class WindowLifecycleTests : IClassFixture<AgentOsPageFixture>
{
    private readonly AgentOsPageFixture _fx;

    public WindowLifecycleTests(AgentOsPageFixture fx) => _fx = fx;

    // Helper: launch the named app via its dock button. The dock entries have title="<Name>".
    private async Task LaunchFromDockAsync(string title)
    {
        await _fx.Page.Locator($".dock-item[title=\"{title}\"]").First.ClickAsync();
        await _fx.Page.Locator($".appwin .appwin-title:has-text(\"{title}\")").WaitForAsync();
    }

    private ILocator AppWindow(string title) =>
        _fx.Page.Locator($".appwin:has(.appwin-title:has-text(\"{title}\"))");

    // Scenario 4: open → focus (Z-bump) → minimize → restore → maximize toggle → close.
    [Fact]
    public async Task Window_Lifecycle_OpenFocusMinimizeRestoreMaximizeClose()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();

        // Open Insights + Settings.
        await LaunchFromDockAsync("Home");
        await LaunchFromDockAsync("Settings");

        var pipeline = AppWindow("Home");
        var settings = AppWindow("Settings");
        await Assertions.Expect(pipeline).ToBeVisibleAsync();
        await Assertions.Expect(settings).ToBeVisibleAsync();

        // Focus Insights (click its titlebar) — its z-index should now be highest.
        await pipeline.Locator(".appwin-titlebar").ClickAsync();

        // Focus bumps Z on the server, which re-renders over SignalR; the front-most window gains
        // `.focused` (driven off max-Z). Web-first wait for that before reading the inline z-index,
        // otherwise we race the round-trip and read the pre-click value.
        await Assertions.Expect(pipeline).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bfocused\b"));
        await Assertions.Expect(settings).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bfocused\b"));

        var pipelineZ = int.Parse(await GetZ(pipeline), System.Globalization.CultureInfo.InvariantCulture);
        var settingsZ = int.Parse(await GetZ(settings), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(pipelineZ > settingsZ, $"Insights z ({pipelineZ}) should be above Settings ({settingsZ}) after focus.");

        // Minimize Insights — the window vanishes from view but the dock indicator stays.
        await pipeline.Locator(".appwin-btn[title=\"Minimize\"]").ClickAsync();
        await Assertions.Expect(pipeline).ToBeHiddenAsync();

        var dockDot = _fx.Page.Locator(".dock-item[title=\"Home\"] .di-dot");
        await Assertions.Expect(dockDot).ToBeVisibleAsync();

        // Restore by clicking the dock icon again (OpenApp focuses an existing entry).
        await _fx.Page.Locator(".dock-item[title=\"Home\"]").ClickAsync();
        await Assertions.Expect(pipeline).ToBeVisibleAsync();

        // Maximize toggle (□ → ❐).
        var maxBtn = pipeline.Locator(".appwin-btn").Nth(1);
        await maxBtn.ClickAsync();
        await Assertions.Expect(pipeline).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bmaximized\b"));

        // Restore (toggle off).
        await maxBtn.ClickAsync();
        await Assertions.Expect(pipeline).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bmaximized\b"));

        // Close.
        await pipeline.Locator(".appwin-btn-close").ClickAsync();
        await Assertions.Expect(pipeline).ToBeHiddenAsync();
    }

    // Scenario 5: maximized geometry: left:0, top:30, right:0, bottom:80.
    [Fact]
    public async Task Maximized_Geometry_FillsBetweenTopBarAndDock()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();
        await LaunchFromDockAsync("Settings");

        var win = AppWindow("Settings");
        await win.Locator(".appwin-btn").Nth(1).ClickAsync(); // maximize

        // ToggleMax flips state on the server and re-renders the maximized PositionStyle over
        // SignalR. Web-first wait for the `maximized` class before reading the inline style,
        // otherwise we race the round-trip and read the still-windowed geometry.
        await Assertions.Expect(win).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bmaximized\b"));

        // Read the inline style we render — AppFrame: "left:0; top:30px; right:0; bottom:80px;"
        var style = await win.GetAttributeAsync("style");
        Assert.NotNull(style);
        Assert.Contains("left:0", style!, StringComparison.Ordinal);
        Assert.Contains("top:30px", style, StringComparison.Ordinal);
        Assert.Contains("right:0", style, StringComparison.Ordinal);
        Assert.Contains("bottom:80px", style, StringComparison.Ordinal);
    }

    // Scenario 7: dock pinned apps + running indicator dot.
    [Fact]
    public async Task Dock_PinnedApps_ShowRunningDot_WhenOpen()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();

        // 6 pinned apps in the Dash (GNOME Dash — no Start button).
        foreach (var t in new[] { "Home", "Workflow", "Board", "Settings" })
        {
            await Assertions.Expect(_fx.Page.Locator($".dock-item[title=\"{t}\"]")).ToBeVisibleAsync();
        }

        // Nothing running yet — no .di-dot.
        Assert.Equal(0, await _fx.Page.Locator(".dock-item .di-dot").CountAsync());

        // Launch Insights; dot appears on its dock item.
        await LaunchFromDockAsync("Home");
        var dot = _fx.Page.Locator(".dock-item[title=\"Home\"] .di-dot");
        await Assertions.Expect(dot).ToBeVisibleAsync();
    }

    // Scenario 9: TopBar center reflects the focused window title (or "AgentOS.NET — Desktop").
    [Fact]
    public async Task TopBar_Center_Reflects_FocusedWindowTitle()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();

        // GNOME panel: focused-app title sits top-left (.tb-appname); the clock is centered.
        var center = _fx.Page.Locator(".topbar .tb-appname");
        await Assertions.Expect(center).ToContainTextAsync("AgentOS.NET — Desktop");

        await LaunchFromDockAsync("Home");
        await Assertions.Expect(center).ToContainTextAsync("Home");

        await LaunchFromDockAsync("Settings");
        await Assertions.Expect(center).ToContainTextAsync("Settings");

        // Click Insights titlebar → it becomes top-most → topbar updates.
        await AppWindow("Home").Locator(".appwin-titlebar").ClickAsync();
        await Assertions.Expect(center).ToContainTextAsync("Home");
    }

    // Scenario 10: toast container is anchored top-right (top: 42px) + each toast has × dismiss.
    [Fact]
    public async Task Toast_AppearsAt_TopRight_AndDismissesOnClose()
    {
        if (!AgentOsPageFixture.IsEnabled) { Assert.Skip(AgentOsPageFixture.SkipReason); }

        await _fx.GotoDesktopAsync();

        // Trigger a toast via the System app's notification test (the GNOME wallpaper menu no longer
        // carries a debug item). Right-click → "Display settings…" opens the System app.
        await _fx.Page.Locator(".desktop").ClickAsync(new LocatorClickOptions
        {
            Button = MouseButton.Right,
            Position = new Position { X = 240, Y = 240 },
        });
        await _fx.Page.Locator(".ctxmenu .ctx-item:has-text(\"Display settings\")").ClickAsync();
        var sys = _fx.Page.Locator(".appwin.focused");
        await sys.Locator(".prefs-cat", new() { HasTextString = "Notifications" }).ClickAsync();
        await sys.GetByRole(AriaRole.Button, new() { Name = "Send test" }).ClickAsync();

        var toast = _fx.Page.Locator(".toast-container .toast").First;
        await Assertions.Expect(toast).ToBeVisibleAsync();

        // Container is anchored top:42px (under the 30px TopBar).
        var container = _fx.Page.Locator(".toast-container");
        var topPx = await container.EvaluateAsync<double>(
            "el => parseFloat(getComputedStyle(el).top)");
        Assert.InRange(topPx, 40, 44);

        var viewport = _fx.Page.ViewportSize!;
        var box = await container.BoundingBoxAsync();
        Assert.NotNull(box);
        // Right-anchored: the right edge of the container should be within ~40px of viewport width.
        Assert.True(box!.X + box.Width > viewport.Width - 64,
            $"toast container right edge {box.X + box.Width} expected near viewport width {viewport.Width}");

        // Click ×.
        await toast.Locator(".toast-close").ClickAsync();
        await Assertions.Expect(toast).ToBeHiddenAsync();
    }

    // ----- Scaffolded -----

    [Fact]
    public Task Resize_Handle_Drag_Changes_W_H_WithMinClamps_280x200()
    {
        Assert.Skip("scaffolded — covers scenario 6 (resize handle drag with clamps).");
        return Task.CompletedTask;
    }

    [Fact]
    public Task StartMenu_Search_FiltersApps_AndLaunchesTopHitOnEnter()
    {
        Assert.Skip("scaffolded — Kickoff Start menu: typing filters .ko-row results; Enter launches the top hit.");
        return Task.CompletedTask;
    }

    [Fact]
    public Task SystemApp_Tabs_SwitchBody_General_Appearance_Notifications_About_Session()
    {
        Assert.Skip("scaffolded — covers System app tab switching across .prefs-cat (General/Appearance/Notifications/About/Session).");
        return Task.CompletedTask;
    }

    [Fact]
    public Task Toggle_Component_FlipsOnOff_AndUpdatesOnClass()
    {
        Assert.Skip("scaffolded — covers scenario 12 (.toggle .on class on checkbox change).");
        return Task.CompletedTask;
    }

    private static async Task<string> GetZ(ILocator l)
    {
        var z = await l.EvaluateAsync<string>("el => getComputedStyle(el).zIndex");
        return string.IsNullOrEmpty(z) || z == "auto" ? "0" : z;
    }
}
