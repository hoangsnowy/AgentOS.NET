// Scoped autonomy keeps a run's writes in its own folder. A Testing agent on the lab, refused `git worktree add ../base`,
// wrote a helper script into its folder and ran it with `node ./.tester-base.mjs` — leaving `../base-122` next to the run
// folders. The PreToolUse guard resolves every path a call would write (real path resolution against a real temp run
// folder here) and blocks what lands outside, a helper the run wrote itself, and the run's own git / claude settings.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ScopedWriteGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _run;

    public ScopedWriteGuardTests()
    {
        _root = Path.Join(Path.GetTempPath(), "agentos-guard-" + Guid.NewGuid().ToString("N"));
        _run = Path.Join(_root, "20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7");
        Directory.CreateDirectory(Path.Join(_run, "repo", "tools"));
    }

    public void Dispose() => _ = TestFiles.TryDeleteDirectory(_root);

    // Committed, unchanged repository files are the "tools/" ones here; everything else counts as written by the run.
    private static bool CleanTools(string fullPath) => fullPath.Replace('\\', '/').Contains("/tools/", StringComparison.Ordinal);

    private string? Bash(string command, string? cwd = null) =>
        ScopedWriteGuard.CheckCommand(command, cwd ?? _run, _run, powerShell: false, CleanTools);

    private string? Ps(string command, string? cwd = null) =>
        ScopedWriteGuard.CheckCommand(command, cwd ?? _run, _run, powerShell: true, CleanTools);

    // ── what the lab run did ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("git worktree add -q ../../base-122 origin/main 2>&1 || git worktree add -q ../base origin/main; git worktree list")]
    [InlineData("git worktree add ../base origin/main")]
    [InlineData("mkdir -p ../base-122 && git archive origin/main web .claude/skills/taste-app | tar -x -C ../base-122")]
    [InlineData("node ./.tester-base.mjs")]
    [InlineData("dotnet test --results-directory ../trx84")]
    [InlineData("node tools/validate.mjs > ../validate-out.json")]
    public void CheckCommand_WhatTheLabRunDid_IsBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Theory]
    [InlineData("git worktree add .base-main origin/main")]
    [InlineData("node ./repo/tools/lint.mjs ../base-122/web/src --app")] // the repo's own script; reading elsewhere is fine
    [InlineData("mkdir -p out/base && git archive origin/main web | tar -x -C out/base")]
    public void CheckCommand_SameWorkInsideTheRunFolder_IsAllowed(string command) => Bash(command).ShouldBeNull();

    // ── paths ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mkdir -p ../x")]
    [InlineData("mkdir -p src/../../x")]
    [InlineData("touch ~/x")]
    [InlineData("cp a.txt /etc/x")]
    [InlineData("mv a.txt ../a.txt")]
    [InlineData("rm -rf ../other-run")]
    [InlineData("ln -s .. up")]
    [InlineData("cp -t ../x a.txt")]
    [InlineData("cp --target-directory=../x a.txt")]
    [InlineData("tee ../log.txt")]
    [InlineData("unzip a.zip -d ../x")]
    [InlineData("mkdir \"$HOME/x\"")]
    [InlineData("mkdir $(pwd)/../x")]
    [InlineData("mkdir `pwd`/x")]
    public void CheckCommand_WritesOutside_AreBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Theory]
    [InlineData("mkdir -p repo/build/out")]
    [InlineData("mkdir src && cd src && mkdir a")]
    [InlineData("touch './with space.txt'")]
    [InlineData("cp a.txt b.txt")]
    [InlineData("dotnet build 2>&1 | tail -n 50")]
    [InlineData("dotnet test > test.log 2>&1")]
    [InlineData("npm ci 2>/dev/null")]
    [InlineData("ls -la ..")]   // reading outside is not a write
    [InlineData("git status")]
    public void CheckCommand_WritesInside_AreAllowed(string command) => Bash(command).ShouldBeNull();

    [Fact]
    public void CheckCommand_AbsolutePathInside_IsAllowed_Outside_IsBlocked()
    {
        Bash($"mkdir \"{Path.Join(_run, "deep", "x")}\"").ShouldBeNull();
        Bash($"mkdir \"{Path.Join(_root, "base-122")}\"").ShouldNotBeNull();
    }

    [Fact]
    public void CheckCommand_SiblingWithTheRunFolderAsPrefix_IsOutside() =>
        Bash($"mkdir \"{_run}-copy\"").ShouldNotBeNull(); // "<run>-copy" starts with "<run>" but is a sibling

    // ── cd ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cd .. && ls")]
    [InlineData("cd ..")]
    [InlineData("cd")]
    [InlineData("cd ~")]
    [InlineData("cd repo && cd ../.. && mkdir x")]
    [InlineData("cd repo && mkdir ../../x")]
    public void CheckCommand_CdOutOfTheRunFolder_IsBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Fact]
    public void CheckCommand_ShellAlreadyOutside_BlocksCommandsButAllowsCdBack()
    {
        Bash("ls", cwd: _root).ShouldNotBeNull();
        Bash("dotnet build", cwd: _root).ShouldNotBeNull();
        Bash($"cd \"{_run}\" && dotnet build", cwd: _root).ShouldBeNull();
    }

    [Fact]
    public void CheckCommand_CdToAComputedFolder_LaterWritesAreBlocked()
    {
        Bash("cd \"$(git rev-parse --show-toplevel)\" && ls").ShouldBeNull();
        Bash("cd \"$(git rev-parse --show-toplevel)\" && git worktree add x").ShouldNotBeNull();
        Bash("cd \"$(git rev-parse --show-toplevel)\" && mkdir x").ShouldNotBeNull();
    }

    // ── git / gh / package tools ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("git clone https://github.com/o/r.git ../r")]
    [InlineData("git clone --depth 1 -b main https://github.com/o/r.git ../r")]
    [InlineData("git clone --separate-git-dir=../g https://github.com/o/r.git r")]
    [InlineData("git -C ../other status")]
    [InlineData("git -C .. log")]
    [InlineData("git --work-tree=../x checkout main")]
    [InlineData("git --git-dir ../x/.git log")]
    [InlineData("git archive -o ../main.tar origin/main")]
    [InlineData("git archive --output=../main.tar origin/main")]
    [InlineData("git init ../x")]
    [InlineData("git bundle create ../all.bundle --all")]
    [InlineData("git format-patch -o ../patches origin/main")]
    [InlineData("gh repo clone o/r ../r")]
    [InlineData("dotnet new console -o ../x")]
    [InlineData("dotnet publish -c Release --output ../pub")]
    [InlineData("dotnet tool install -g dotnet-ef")]
    [InlineData("npm install -g http-server")]
    [InlineData("npm --prefix ../x install")]
    [InlineData("pnpm add --global x")]
    [InlineData("curl -o ../x.html http://localhost:5000")]
    public void CheckCommand_ToolsWritingOutside_AreBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Theory]
    [InlineData("git clone https://github.com/o/r.git")]
    [InlineData("git clone https://github.com/o/r.git repo")]
    [InlineData("git clone --depth 1 -b main https://github.com/o/r.git .")]
    [InlineData("git -C repo status")]
    [InlineData("git -c core.autocrlf=false commit -m x")]
    [InlineData("gh repo clone o/r")]
    [InlineData("gh pr create --title x --body y")]
    [InlineData("dotnet new console -o app")]
    [InlineData("dotnet test --results-directory TestResults")]
    [InlineData("npm install")]
    [InlineData("npx playwright test --reporter=line")]
    [InlineData("curl -s http://localhost:5000/health")]
    public void CheckCommand_ToolsWritingInside_AreAllowed(string command) => Bash(command).ShouldBeNull();

    // A heredoc commit message is text, not commands — "rm -rf ../x" in it must not block the commit.
    [Fact]
    public void CheckCommand_HeredocCommitMessage_IsNotParsedAsCommands()
    {
        const string commit = "git commit -m \"$(cat <<'EOF'\nfix(board): keep ../x out (1) of it\n\nrm -rf ../x\nEOF\n)\"";
        Bash(commit).ShouldBeNull();
        Bash("cat > notes.md <<'EOF'\nmkdir ../x\nEOF\ngit add notes.md").ShouldBeNull();
        Bash("cat > ../notes.md <<'EOF'\nhello\nEOF").ShouldNotBeNull();
    }

    // ── scripts ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("node ./helper.mjs")]                // written by the run (not a clean repository file)
    [InlineData("node ../outside.mjs")]
    [InlineData("node -e \"require('fs').mkdirSync('../x')\"")]
    [InlineData("node --eval=1")]
    [InlineData("node --import ./hook.mjs repo/tools/x.mjs")]
    [InlineData("python3 -c \"open('../x','w')\"")]
    [InlineData("python helper.py")]
    [InlineData("sh -c \"mkdir ../x\"")]
    [InlineData("bash -c 'cd .. && touch x'")]
    [InlineData("xargs rm")]
    [InlineData("FOO=1 node ./helper.mjs")]
    [InlineData("env FOO=1 node ./helper.mjs")]
    public void CheckCommand_ScriptsThatCanWriteAnywhere_AreBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Theory]
    [InlineData("node repo/tools/gen.mjs")]
    [InlineData("node --test")]
    [InlineData("python -m pytest")]
    [InlineData("sh -c \"dotnet build\"")]
    [InlineData("sh .agentos/bin/copy-in .agentos/files/logo.png repo/wwwroot/logo.png")]
    public void CheckCommand_RepositoryScripts_AreAllowed(string command) => Bash(command).ShouldBeNull();

    // ── PowerShell ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("New-Item -ItemType Directory -Path ..\\base-122")]
    [InlineData("Set-Location ..")]
    [InlineData("cd ..; git worktree add base main")]
    [InlineData("Copy-Item -Recurse web ..\\base-122\\web")]
    [InlineData("'x' | Out-File ..\\out.txt")]
    [InlineData("Get-ChildItem | Set-Content -Path ../list.txt")]
    [InlineData("dotnet build *> ..\\build.log")]
    [InlineData("[System.IO.File]::WriteAllText('..\\x', 'y')")]
    [InlineData("[IO.Directory]::CreateDirectory('..\\x')")]
    [InlineData("Get-Item . | ForEach-Object { New-Item -Path ..\\x -ItemType File }")]
    public void CheckCommand_PowerShellWritesOutside_AreBlocked(string command) => Ps(command).ShouldNotBeNull();

    [Theory]
    [InlineData("New-Item -ItemType Directory -Path out\\base -Force")]
    [InlineData("dotnet build *> build.log")]
    [InlineData("dotnet test 2>$null")]
    [InlineData("Copy-Item a.txt b.txt")]
    [InlineData("Set-Location repo; dotnet build")]
    [InlineData("git log -1 --format=`\"%H`\"")]
    public void CheckCommand_PowerShellWritesInside_AreAllowed(string command) => Ps(command).ShouldBeNull();

    // ── the file tools ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckFileTool_InsideIsAllowed_OutsideAndProtectedAreBlocked()
    {
        ScopedWriteGuard.CheckFileTool(Path.Join(_run, "repo", "src", "a.cs"), _run, _run).ShouldBeNull();
        ScopedWriteGuard.CheckFileTool("repo/src/a.cs", _run, _run).ShouldBeNull();
        ScopedWriteGuard.CheckFileTool(Path.Join(_run, "repo", ".claude", "skills", "x", "SKILL.md"), _run, _run).ShouldBeNull();
        ScopedWriteGuard.CheckFileTool(null, _run, _run).ShouldBeNull();

        ScopedWriteGuard.CheckFileTool(Path.Join(_root, "base-122", "x.mjs"), _run, _run).ShouldNotBeNull();
        ScopedWriteGuard.CheckFileTool("../x.txt", _run, _run).ShouldNotBeNull();
        ScopedWriteGuard.CheckFileTool("~/.bashrc", _run, _run).ShouldNotBeNull();
        ScopedWriteGuard.CheckFileTool(Path.Join(_run, "repo", ".git", "hooks", "pre-commit"), _run, _run).ShouldNotBeNull();
        ScopedWriteGuard.CheckFileTool(Path.Join(_run, "repo", ".claude", "settings.local.json"), _run, _run).ShouldNotBeNull();
        ScopedWriteGuard.CheckFileTool(Path.Join(_run, ".claude", "settings.json"), _run, _run).ShouldNotBeNull();
        Bash("echo '#!/bin/sh' > repo/.git/hooks/pre-commit").ShouldNotBeNull();
    }

    [Fact]
    public void CheckFileTool_GitBashDrivePath_ResolvesToTheWindowsPath()
    {
        if (!OperatingSystem.IsWindows()) { return; } // "/c/…" is a Git Bash spelling of "C:\…"
        var drive = char.ToLowerInvariant(_run[0]);
        var gitBash = "/" + drive + _run[2..].Replace('\\', '/') + "/repo/a.txt";

        ScopedWriteGuard.CheckFileTool(gitBash, _run, _run).ShouldBeNull();
        Bash($"mkdir \"/{drive}/Users/x\"").ShouldNotBeNull();
    }

    // ── the hook ──────────────────────────────────────────────────────────────────────────────────────────────

    private string HookJson(string tool, string field, string value) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["session_id"] = "s",
        ["hook_event_name"] = "PreToolUse",
        ["cwd"] = _run,
        ["tool_name"] = tool,
        ["tool_input"] = new Dictionary<string, string> { [field] = value },
    });

    [Theory]
    [InlineData("Bash", "command", "mkdir -p ../base-122", 2)]
    [InlineData("Bash", "command", "mkdir -p out", 0)]
    [InlineData("PowerShell", "command", "New-Item -ItemType Directory ..\\x", 2)]
    [InlineData("Write", "file_path", "../x.mjs", 2)]
    [InlineData("Write", "file_path", "repo/x.mjs", 0)]
    [InlineData("Edit", "file_path", "../x.cs", 2)]
    [InlineData("MultiEdit", "file_path", "../x.cs", 2)]
    [InlineData("NotebookEdit", "notebook_path", "../x.ipynb", 2)]
    [InlineData("Read", "file_path", "../x.cs", 0)] // reads are not this guard's business
    public void Run_HookInput_ExitCodeTwoBlocksWithTheReason(string tool, string field, string value, int expected)
    {
        using var input = new StringReader(HookJson(tool, field, value));
        using var error = new StringWriter();

        var code = ScopedWriteGuard.Run(input, error, _run);

        code.ShouldBe(expected);
        if (expected == 2) { error.ToString().ShouldStartWith("Blocked by the AgentOS.NET runner:"); }
        else { error.ToString().ShouldBeEmpty(); }
    }

    [Fact]
    public void Run_UnreadableInput_FailsClosed()
    {
        using var input = new StringReader("{not json");
        using var error = new StringWriter();

        ScopedWriteGuard.Run(input, error, _run).ShouldBe(2);
    }

    [Fact]
    public void Run_NoRunFolder_AllowsEverything()
    {
        using var input = new StringReader(HookJson("Bash", "command", "mkdir ../x"));
        using var error = new StringWriter();

        ScopedWriteGuard.Run(input, error, runDir: null).ShouldBe(0);
    }

    // ── wiring ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HookSettingsJson_InstallsTheGuardAsAPreToolUseHook()
    {
        using var doc = JsonDocument.Parse(ScopedWriteGuard.HookSettingsJson("\"C:/x/AgentOs.RemoteAgent.exe\" guard"));
        var entry = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];

        entry.GetProperty("matcher").GetString().ShouldBe("Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit");
        var hook = entry.GetProperty("hooks")[0];
        hook.GetProperty("type").GetString().ShouldBe("command");
        hook.GetProperty("command").GetString().ShouldBe("\"C:/x/AgentOs.RemoteAgent.exe\" guard");
    }

    [Theory]
    [InlineData(@"C:\Program Files\AgentOS\AgentOs.RemoteAgent.exe", null, "\"C:/Program Files/AgentOS/AgentOs.RemoteAgent.exe\" guard")]
    [InlineData("/usr/share/dotnet/dotnet", "/opt/agentos/AgentOs.RemoteAgent.dll", "\"/usr/share/dotnet/dotnet\" \"/opt/agentos/AgentOs.RemoteAgent.dll\" guard")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"D:\r\AgentOs.RemoteAgent.dll", "\"C:/Program Files/dotnet/dotnet.exe\" \"D:/r/AgentOs.RemoteAgent.dll\" guard")]
    public void GuardCommand_QuotesWithForwardSlashes(string processPath, string? entry, string expected) =>
        ScopedWriteGuard.GuardCommand(processPath, entry).ShouldBe(expected);

    [Theory]
    [InlineData("claude", true)]
    [InlineData(@"C:\Users\x\AppData\Roaming\npm\claude.cmd", true)]
    [InlineData("codex", false)]
    public void AppliesTo_ClaudeButNotCodex(string command, bool expected) => ScopedWriteGuard.AppliesTo(command).ShouldBe(expected);

    [Fact]
    public void WriteSettings_LandsOutsideTheRunFolder()
    {
        var folder = Path.Join(_root, "settings");

        var path = ScopedWriteGuard.WriteSettings(folder, _run, "\"x\" guard");

        ScopedWriteGuard.IsInside(path, _run).ShouldBeFalse();
        File.ReadAllText(path).ShouldContain("PreToolUse");
    }

    // ── the repository-script check, against real git ────────────────────────────────────────────────────────

    [Fact]
    public void IsCleanRepoFile_CommittedUnchanged_Yes_WrittenOrChanged_No()
    {
        var repo = Path.Join(_run, "repo");
        if (!Git(repo, "init", "-q")) { return; } // no git on this machine
        File.WriteAllText(Path.Join(repo, "tools", "gen.mjs"), "console.log(1)\n");
        Git(repo, "add", ".").ShouldBeTrue();
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "init").ShouldBeTrue();
        File.WriteAllText(Path.Join(repo, "helper.mjs"), "x");

        ScopedWriteGuard.IsCleanRepoFile(Path.Join(repo, "tools", "gen.mjs")).ShouldBeTrue();
        ScopedWriteGuard.IsCleanRepoFile(Path.Join(repo, "helper.mjs")).ShouldBeFalse();     // untracked
        Git(repo, "add", "helper.mjs").ShouldBeTrue();
        ScopedWriteGuard.IsCleanRepoFile(Path.Join(repo, "helper.mjs")).ShouldBeFalse();     // staged, not committed
        File.AppendAllText(Path.Join(repo, "tools", "gen.mjs"), "require('fs')\n");
        ScopedWriteGuard.IsCleanRepoFile(Path.Join(repo, "tools", "gen.mjs")).ShouldBeFalse(); // changed by the run
        ScopedWriteGuard.IsCleanRepoFile(Path.Join(repo, "missing.mjs")).ShouldBeFalse();
        ScopedWriteGuard.IsCleanRepoFile(Path.Join(_root, "outside.mjs")).ShouldBeFalse();
    }

    private static bool Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        try
        {
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
