// "Run on my machine" without YOLO: the scoped flags never bypass permissions, deny history rewrites and
// credential access, and every run gets its own directory under the configured root.

using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ScopedAutonomyTests
{
    [Theory]
    [InlineData("scoped", true)]
    [InlineData(" SCOPED ", true)]
    [InlineData("yolo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnabled_Setting_OnlyScopedOptsIn(string? setting, bool expected) =>
        ScopedAutonomy.IsEnabled(setting).ShouldBe(expected);

    [Fact]
    public void ArgsFor_Claude_AcceptsEditsWithAllowAndDenyLists_NeverBypasses()
    {
        var args = ScopedAutonomy.ArgsFor("claude");

        args.ShouldContain("acceptEdits");
        args.ShouldNotContain("bypassPermissions");
        args.ShouldNotContain("--dangerously-skip-permissions");
        args.ShouldContain("Bash(git clone *)");
        args.ShouldContain("Bash(dotnet *)");
        args.ShouldContain("Bash(git push --force*)");
        args.ShouldContain("Bash(gh auth *)");
        args.ShouldContain("Read(~/.ssh/**)");
        args.ShouldContain("Read(~/AppData/Roaming/**)");
        args.ShouldNotContain("Bash(cat *)");
        args.ShouldNotContain("Bash(node *)");
        // Deny list comes after the allow list so the variadic --allowedTools ends at --disallowedTools.
        Array.IndexOf(args, "--disallowedTools").ShouldBeGreaterThan(Array.IndexOf(args, "--allowedTools"));
    }

    [Fact]
    public void ClaudeLists_NothingIsBothAllowedAndDenied() =>
        ScopedAutonomy.ClaudeAllowed.Intersect(ScopedAutonomy.ClaudeDenied).ShouldBeEmpty();

    // A bare file-tool allow rule applies to every path on the machine; acceptEdits is what keeps edits inside
    // the run directory (found by a live smoke run that wrote into the home directory).
    [Fact]
    public void ClaudeAllowed_NoUnscopedFileTool() =>
        ScopedAutonomy.ClaudeAllowed.ShouldAllBe(rule => rule.StartsWith("Bash(", StringComparison.Ordinal));

    // A Read deny rule that covered the run root would lock the agent out of its own checkout (a live smoke run
    // with "Read(~/AppData/**)" could not read README.md under %LOCALAPPDATA%).
    [Fact]
    public void ClaudeDenied_NoReadRuleCoversTheDefaultRunRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.GetRelativePath(home, ScopedAutonomy.DefaultRoot()).Replace('\\', '/') + "/";
        var denied = ScopedAutonomy.ClaudeDenied
            .Where(r => r.StartsWith("Read(~/", StringComparison.Ordinal))
            .Select(r => r["Read(~/".Length..^1].Replace("**", string.Empty, StringComparison.Ordinal));
        denied.ShouldAllBe(prefix => !root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // claude parses a trailing ":*" as its legacy prefix syntax and then matches the "*" before it literally.
    [Fact]
    public void ClaudeRules_NoneEndInLegacyPrefixSyntax() =>
        ScopedAutonomy.ClaudeAllowed.Concat(ScopedAutonomy.ClaudeDenied)
            .ShouldAllBe(rule => !rule.EndsWith(":*)", StringComparison.Ordinal));

    [Theory]
    [InlineData("codex")]
    [InlineData("C:\\tools\\codex.cmd")]
    public void ArgsFor_Codex_UsesWorkspaceWriteSandbox(string command) =>
        ScopedAutonomy.ArgsFor(command).ShouldBe(["--sandbox", "workspace-write"]);

    [Fact]
    public void CreateRunDirectory_PlainId_CreatesEmptyDirUnderRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "agentos-scoped-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = ScopedAutonomy.CreateRunDirectory(root, "3b53273fe53b42e2");

            Directory.Exists(dir).ShouldBeTrue();
            Path.GetDirectoryName(dir).ShouldBe(Path.GetFullPath(root));
            dir.ShouldEndWith("-3b53273fe53b42e2");
            Directory.EnumerateFileSystemEntries(dir).ShouldBeEmpty();
        }
        finally
        {
            ScopedAutonomy.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("..\\..\\Windows")]
    [InlineData("../../etc")]
    [InlineData("")]
    public void CreateRunDirectory_CraftedId_StaysUnderRoot(string requestId)
    {
        var root = Path.Join(Path.GetTempPath(), "agentos-scoped-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = ScopedAutonomy.CreateRunDirectory(root, requestId);

            Path.GetDirectoryName(dir).ShouldBe(Path.GetFullPath(root));
            dir.ShouldNotContain("..");
        }
        finally
        {
            ScopedAutonomy.TryDelete(root);
        }
    }

    [Fact]
    public void TryDelete_ReadOnlyFiles_RemovesDirectory()
    {
        var dir = Path.Join(Path.GetTempPath(), "agentos-scoped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(dir, ".git", "objects"));
        var pack = Path.Join(dir, ".git", "objects", "pack.idx");
        File.WriteAllText(pack, "x");
        File.SetAttributes(pack, FileAttributes.ReadOnly);

        ScopedAutonomy.TryDelete(dir);

        Directory.Exists(dir).ShouldBeFalse();
    }
}
