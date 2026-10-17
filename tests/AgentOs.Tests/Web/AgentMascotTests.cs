// AgentMascot — the cartoon agent rendered per role and run state. Rendered through the framework's own
// HtmlRenderer (no bUnit). Guards the contract the CSS relies on (role-*/is-* classes, one prop per role),
// accessibility (role=img + name), self-containment (no ids/defs/scripts, so any number can share a page),
// and the stylesheet side: every m-* class the markup uses is defined, and motion can always be turned off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentOs.Web.Components.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class AgentMascotTests
{
    private static async Task<string> RenderAsync(string? role, AgentMascot.MascotState state = AgentMascot.MascotState.Idle, int size = 40)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AgentMascot>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AgentMascot.Role)] = role,
                [nameof(AgentMascot.State)] = state,
                [nameof(AgentMascot.Size)] = size,
            }));
            return output.ToHtmlString();
        });
    }

    [Theory]
    [InlineData("Orchestrator", "role-orchestrator", "m-baton")]
    [InlineData("Requirement", "role-requirement", "m-pencil")]
    [InlineData("Coding", "role-coding", "m-screen")]
    [InlineData("Testing", "role-testing", "m-lens")]
    [InlineData("Qa", "role-qa", "m-stamp")]
    [InlineData(" qa ", "role-qa", "m-stamp")]              // case/whitespace-insensitive
    [InlineData("IssueWork", "role-coding", "m-screen")]
    [InlineData("Decomposer", "role-orchestrator", "m-baton")]
    [InlineData(null, "role-generic", "m-bulb")]
    [InlineData("SomePluginAgent", "role-generic", "m-bulb")]
    public async Task Render_Role_SelectsPaletteAndProp(string? role, string roleClass, string prop)
    {
        var html = await RenderAsync(role);
        html.ShouldContain(roleClass);
        html.ShouldContain(prop);
    }

    [Theory]
    [InlineData(AgentMascot.MascotState.Idle, "is-idle")]
    [InlineData(AgentMascot.MascotState.Running, "is-running")]
    [InlineData(AgentMascot.MascotState.Done, "is-done")]
    [InlineData(AgentMascot.MascotState.Failed, "is-failed")]
    [InlineData(AgentMascot.MascotState.Canned, "is-canned")]
    [InlineData(AgentMascot.MascotState.Skipped, "is-skipped")]
    public async Task Render_State_MapsToStateClass(AgentMascot.MascotState state, string stateClass)
        => (await RenderAsync("Coding", state)).ShouldContain(stateClass);

    [Fact]
    public async Task Render_EveryState_HasADistinctClass()
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in Enum.GetValues<AgentMascot.MascotState>())
        {
            var html = await RenderAsync("Qa", state);
            classes.Add(StateClass().Match(html).Value).ShouldBeTrue($"{state} reuses another state's class");
        }
    }

    [Fact]
    public async Task Render_IsAccessible_AndSized()
    {
        var html = System.Net.WebUtility.HtmlDecode(await RenderAsync("Testing", AgentMascot.MascotState.Failed, size: 28));
        html.ShouldContain("role=\"img\"");
        html.ShouldContain("aria-label=\"Testing — failed\"");
        html.ShouldContain("<title>Testing — failed</title>");
        html.ShouldContain("width:28px;height:28px");
    }

    [Fact]
    public async Task Render_IsSelfContained_SoManyCanShareAPage()
    {
        foreach (var role in new[] { "Orchestrator", "Requirement", "Coding", "Testing", "Qa", null })
        {
            var html = await RenderAsync(role, AgentMascot.MascotState.Running);
            html.ShouldNotContain(" id=");      // duplicate ids would break url(#…) refs and a11y
            html.ShouldNotContain("<defs");
            html.ShouldNotContain("<script");
            html.ShouldNotContain("url(#");
        }
    }

    [Fact]
    public async Task Stylesheet_DefinesEveryMascotClass_AndCanStopMotion()
    {
        var css = await File.ReadAllTextAsync(AppCss());
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in new[] { "Orchestrator", "Requirement", "Coding", "Testing", "Qa", null })
        {
            var html = await RenderAsync(role);
            foreach (Match m in ClassAttr().Matches(html))
            {
                used.UnionWith(m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        // Exact class token: ".m-eye" must not be satisfied by ".m-eyes".
        var phantom = used.Where(c => !Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![\w-])")).ToArray();
        phantom.ShouldBeEmpty($"classes used by AgentMascot but not defined in app.css: {string.Join(", ", phantom)}");

        css.ShouldContain("@media (prefers-reduced-motion: reduce)");
        css.ShouldContain(":root[data-agent-motion=\"off\"] .mascot");
    }

    // Performance guard (measured: idle infinite loops inside windows with a backdrop filter took the desktop
    // from 60 to 20 fps). Only a RUNNING agent may loop; every other state plays a finite number of times.
    [Fact]
    public async Task Stylesheet_OnlyRunningMascotsLoop()
    {
        var css = await File.ReadAllTextAsync(AppCss());
        var block = css[css.IndexOf("/* ===== Agent mascots", StringComparison.Ordinal)..css.IndexOf("/* ===== Living icons", StringComparison.Ordinal)];
        var offenders = block.Split('\n')
            .Where(l => l.Contains("infinite", StringComparison.Ordinal) && l.TrimStart().StartsWith(".mascot", StringComparison.Ordinal))
            .Where(l => !l.Contains("is-running", StringComparison.Ordinal))
            .ToArray();
        offenders.ShouldBeEmpty($"mascot rules that loop outside the running state: {string.Join(" | ", offenders)}");
    }

    // Performance guard: window/shell glass must come from --glass-filter (which is `none` at 0%), never an
    // unconditional blur — even blur(0px) builds a backdrop layer that re-runs on every repaint.
    [Fact]
    public async Task Stylesheet_WindowGlass_IsOffUnlessEnabled()
    {
        var css = await File.ReadAllTextAsync(AppCss());
        Regex.IsMatch(css, @"\.appwin\s*\{[^}]*backdrop-filter:\s*var\(--glass-filter,\s*none\)").ShouldBeTrue();
        css.ShouldNotContain("backdrop-filter: blur(var(--glass-blur))");
    }

    private static string AppCss()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Join(dir.FullName, "src", "AgentOs.Web", "wwwroot", "app.css");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("app.css not found above the test output directory.");
    }

    [GeneratedRegex("class=\"([^\"]+)\"")]
    private static partial Regex ClassAttr();

    [GeneratedRegex("is-[a-z]+")]
    private static partial Regex StateClass();
}
