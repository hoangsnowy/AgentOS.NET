// GAP-14 (PR #326 review): SettingsFields is the one desktop surface that writes settings at platform scope. Two things
// keep a tenant admin from flipping a platform-wide switch through it, and this source guard pins both (there is no
// component-render test stack in this project):
//   1. a read-only row (platform-scope for a tenant admin, or operator-locked) renders NO Save/Reset handler at all —
//      Blazor Server dispatches a registered handler for any element the client names, disabled attribute or not;
//   2. the write path proves the platform-admin role from the signed-in principal and only then enters
//      RowLevelSecurity.PlatformScope("platform-settings") — the scope ISettings now requires for a platform write.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed partial class SettingsFieldsPlatformWriteGuardTests
{
    [GeneratedRegex(@"<Btn\b[^>]*OnClick=""\(\) => (Save|Reset)\(f\)""")]
    private static partial Regex SaveOrResetButton();

    [Fact]
    public void ReadOnlyRow_RendersNoSaveOrResetHandler()
    {
        var source = Source();
        var guard = source.IndexOf("@if (!readOnly)", StringComparison.Ordinal);
        guard.ShouldBeGreaterThanOrEqualTo(0, "Save/Reset must be inside an `@if (!readOnly)` block");

        var buttons = SaveOrResetButton().Matches(source);
        buttons.Count.ShouldBe(2);
        foreach (Match m in buttons)
        {
            m.Index.ShouldBeGreaterThan(guard, $"{m.Value} is rendered outside the `@if (!readOnly)` block");
            m.Value.ShouldNotContain("readOnly", customMessage: "a disabled button is not a gate — don't render the handler at all");
        }
    }

    [Fact]
    public void PlatformWrite_ChecksThePrincipalThenEntersThePlatformScope()
    {
        var source = Source();
        var check = source.IndexOf("scope == SettingScope.Platform && !await IsPlatformAdminAsync()", StringComparison.Ordinal);
        var enter = source.IndexOf("RowLevelSecurity.PlatformScope(\"platform-settings\")", StringComparison.Ordinal);
        var write = source.IndexOf("Settings.SetAsync(", StringComparison.Ordinal);

        check.ShouldBeGreaterThanOrEqualTo(0, "the write must re-check the signed-in principal's platform-admin role");
        enter.ShouldBeGreaterThan(check, "the platform scope is entered only after the role check");
        write.ShouldBeGreaterThan(enter, "ISettings.SetAsync runs inside the scope the component entered");
        source.ShouldContain("state.User.IsPlatformAdmin()", customMessage: "the proof is the principal's role, not a parameter");
    }

    private static string Source()
        => File.ReadAllText(Path.Join(RepoRoot(), "src", "AgentOs.Web", "Components", "UI", "SettingsFields.razor"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new InvalidOperationException("Repo root (AgentOs.slnx) not found.");
    }
}
