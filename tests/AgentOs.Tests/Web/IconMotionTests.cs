// Living icons — every Icon has its own micro-animation. Guards the contract: the component emits the
// ic-<name> hook (and icon-loop when asked), EVERY icon name declared in Icon.razor has a --ic-anim motion in
// app.css (so a new icon can't ship static), every referenced keyframe exists, and motion can be turned off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentOs.Web.Shell.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class IconMotionTests
{
    private static async Task<string> RenderAsync(string name, bool loop)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Icon>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Icon.Name)] = name,
                [nameof(Icon.Loop)] = loop,
            }))).ToHtmlString());
    }

    [Fact]
    public async Task Render_EmitsMotionHook_AndLoopOnlyWhenAsked()
    {
        (await RenderAsync("gear", loop: false)).ShouldContain("icon ic-gear");
        (await RenderAsync("gear", loop: false)).ShouldNotContain("icon-loop");
        (await RenderAsync("arrow-clockwise", loop: true)).ShouldContain("icon-loop");
    }

    [Fact]
    public void EveryIcon_HasItsOwnMotion()
    {
        var names = CaseName().Matches(File.ReadAllText(Repo("src", "AgentOs.Web.Shell", "UI", "Icon.razor")))
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        names.Length.ShouldBeGreaterThan(40);

        var css = File.ReadAllText(Repo("src", "AgentOs.Web", "wwwroot", "app.css"));
        var missing = names.Where(n => !Regex.IsMatch(css, @"\.ic-" + Regex.Escape(n) + @"\s*\{[^}]*--ic-anim:")).ToArray();
        missing.ShouldBeEmpty($"icons without a motion in app.css: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedKeyframe_IsDefined()
    {
        var css = File.ReadAllText(Repo("src", "AgentOs.Web", "wwwroot", "app.css"));
        var used = AnimName().Matches(css).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
        var undefined = used.Where(k => !css.Contains("@keyframes " + k + " ", StringComparison.Ordinal)
                                     && !css.Contains("@keyframes " + k + "{", StringComparison.Ordinal)).ToArray();
        undefined.ShouldBeEmpty($"--ic-anim references undefined keyframes: {string.Join(", ", undefined)}");
    }

    [Fact]
    public void Motion_CanAlwaysBeTurnedOff()
    {
        var css = File.ReadAllText(Repo("src", "AgentOs.Web", "wwwroot", "app.css"));
        css.ShouldContain(":root[data-agent-motion=\"off\"] .icon");
        Regex.IsMatch(css, @"@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.icon").ShouldBeTrue();
    }

    private static string Repo(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx")))
            {
                return Path.Join([dir.FullName, .. parts]);
            }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }

    [GeneratedRegex("case \"([a-z0-9-]+)\"")]
    private static partial Regex CaseName();

    [GeneratedRegex(@"--ic-anim:\s*([a-z0-9-]+)")]
    private static partial Regex AnimName();
}
