// Scoped autonomy keeps a run's writes in its own folder. A Testing agent on the lab, refused `git worktree add ../base`,
// wrote a helper script into its folder and ran it with `node ./.tester-base.mjs` — leaving `../base-122` next to the run
// folders. The PreToolUse guard resolves every path a call would write (real path resolution against a real temp run
// folder here) and blocks what lands outside, a new script the run wrote itself (with "commit it first"), and the run's
// own git / husky / claude settings — while a ticket's own scripts, build outputs and temp files keep working.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ScopedWriteGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _run;
    private readonly string _tmp;

    public ScopedWriteGuardTests()
    {
        _root = Path.Join(Path.GetTempPath(), "agentos-guard-" + Guid.NewGuid().ToString("N"));
        _run = Path.Join(_root, "20261102-154906-d1ed26ddc4d74ab186c0ce6d6559fba7");
        _tmp = _run + RunsRootWatch.TempSuffix;
        Directory.CreateDirectory(Path.Join(_run, "repo", "tools"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() => _ = TestFiles.TryDeleteDirectory(_root);

    // What git would say, by folder: tools/ committed, edited/ tracked with changes, dist/ + node_modules/ ignored,
    // missing/ not there; anything else is a new file the run wrote.
    private static ScriptState FakeGit(string fullPath)
    {
        var p = fullPath.Replace('\\', '/');
        if (p.Contains("/tools/", StringComparison.Ordinal)) { return ScriptState.Clean; }
        if (p.Contains("/edited/", StringComparison.Ordinal)) { return ScriptState.Tracked; }
        if (p.Contains("/dist/", StringComparison.Ordinal) || p.Contains("/node_modules/", StringComparison.Ordinal)) { return ScriptState.Ignored; }
        if (p.Contains("/missing/", StringComparison.Ordinal)) { return ScriptState.Missing; }
        return ScriptState.Untracked;
    }

    private GuardContext Ctx(Func<string, bool>? writtenEarlier = null) =>
        new(_run, _tmp) { ScriptStateOf = FakeGit, WrittenEarlier = writtenEarlier ?? (static _ => false) };

    private string? Bash(string command, string? cwd = null) =>
        ScopedWriteGuard.CheckCommand(command, cwd ?? _run, Ctx(), powerShell: false);

    private string? Ps(string command, string? cwd = null) =>
        ScopedWriteGuard.CheckCommand(command, cwd ?? _run, Ctx(), powerShell: true);

    private string? File(string? path) => ScopedWriteGuard.CheckFileTool(path, _run, Ctx());

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
    [InlineData("cp -t../x a.txt")]
    [InlineData("cp --target-directory=../x a.txt")]
    [InlineData("tee ../log.txt")]
    [InlineData("unzip a.zip -d ../x")]
    [InlineData("tar -xf a.tar -C../x")]
    [InlineData("mkdir \"$HOME/x\"")]
    [InlineData("mkdir $(pwd)/../x")]
    [InlineData("mkdir `pwd`/x")]
    [InlineData("x=../x; mkdir $x")]
    [InlineData("sudo mkdir ../x")]
    [InlineData("time mkdir ../x")]
    [InlineData("{ mkdir ../x; }")]
    [InlineData("if true; then mkdir ../x; fi")]
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
    [InlineData("for i in 1 2 3; do curl -s http://localhost:5000/health && break; sleep 2; done")]
    public void CheckCommand_WritesInside_AreAllowed(string command) => Bash(command).ShouldBeNull();

    [Theory]
    [InlineData("npm run build >> ../x.log")]
    [InlineData("npm run build 2> ../x.log")]
    [InlineData("npm run build &> ../x.log")]
    [InlineData("npm run build >& ../x.log")]
    [InlineData("npm run build >&../x.log")]
    [InlineData("npm test <> ../x.log")]
    public void CheckCommand_EveryRedirectionForm_IsChecked(string command) => Bash(command).ShouldNotBeNull();

    [Fact]
    public void CheckCommand_AbsolutePathInside_IsAllowed_Outside_IsBlocked()
    {
        Bash($"mkdir \"{Path.Join(_run, "deep", "x")}\"").ShouldBeNull();
        Bash($"mkdir \"{Path.Join(_root, "base-122")}\"").ShouldNotBeNull();
    }

    [Fact]
    public void CheckCommand_SiblingWithTheRunFolderAsPrefix_IsOutside() =>
        Bash($"mkdir \"{_run}-copy\"").ShouldNotBeNull(); // "<run>-copy" starts with "<run>" but is a sibling

    // ── temp files: the run's own temp folder ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("npm run build > \"$TEMP/build.log\" 2>&1")]
    [InlineData("npm run build > \"${TMPDIR}/build.log\"")]
    [InlineData("mkdir -p $TMP/out")]
    [InlineData("echo x > \"$AGENTOS_RUN_DIR/notes.txt\"")]
    public void CheckCommand_TheRunsTempFolderAndVariables_AreAllowed(string command) => Bash(command).ShouldBeNull();

    [Fact]
    public void CheckCommand_GitBashTmp_IsTheRunsTempFolder()
    {
        var verdict = Bash("dotnet test > /tmp/test.log 2>&1; tail -50 /tmp/test.log");

        // Windows: Git Bash's /tmp is the CLI's TEMP = the run's temp folder. Elsewhere /tmp is the shared one — outside
        // the run (the CLI's TMPDIR is the run's temp folder).
        if (OperatingSystem.IsWindows()) { verdict.ShouldBeNull(); }
        else { verdict.ShouldNotBeNull(); }
    }

    [Fact]
    public void CheckCommand_PowerShellEnvTemp_IsTheRunsTempFolder() =>
        Ps("dotnet test *> $env:TEMP\\test.log").ShouldBeNull();

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
    public void CheckCommand_CdToTheRepositoryTop_StaysInsideAndWritesAreChecked()
    {
        Directory.CreateDirectory(Path.Join(_run, "repo", ".git"));
        var src = Path.Join(_run, "repo", "src");
        Directory.CreateDirectory(src);

        Bash("cd \"$(git rev-parse --show-toplevel)\" && npm test", cwd: src).ShouldBeNull();
        Bash("cd \"$(git rev-parse --show-toplevel)\" && mkdir out", cwd: src).ShouldBeNull();
        Bash("cd \"$(git rev-parse --show-toplevel)\" && mkdir ../../x", cwd: src).ShouldNotBeNull();
    }

    [Fact]
    public void CheckCommand_CdToAComputedFolder_LaterWritesAreBlocked()
    {
        Bash("cd \"$SOMEWHERE\" && ls").ShouldBeNull();
        Bash("cd \"$SOMEWHERE\" && mkdir x").ShouldNotBeNull();
        Bash("cd \"$(git rev-parse --show-toplevel)\" && mkdir x").ShouldNotBeNull(); // no repository here: unknown
    }

    [Fact]
    public void CheckCommand_CdBack_IsAllowedInsideAndFollowed()
    {
        Bash("cd -").ShouldBeNull();
        Bash("cd repo && cd - && mkdir x").ShouldBeNull();
        Bash("cd repo && cd - && mkdir ../x").ShouldNotBeNull();
    }

    // ── git / gh / package tools ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("git clone https://github.com/o/r.git ../r")]
    [InlineData("git clone -- https://github.com/o/r.git ../r")]
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
    [InlineData("dotnet publish -o:../pub")]
    [InlineData("dotnet build -p:OutputPath=../out/")]
    [InlineData("dotnet build --property:BaseIntermediateOutputPath=../obj/")]
    [InlineData("dotnet build \"/p:Configuration=Release;OutDir=../out\"")]
    [InlineData("dotnet tool install -g dotnet-ef")]
    [InlineData("npm install -g http-server")]
    [InlineData("npm --prefix ../x install")]
    [InlineData("pnpm add --global x")]
    [InlineData("yarn global add x")]
    [InlineData("curl -o ../x.html http://localhost:5000")]
    [InlineData("curl http://localhost:5000 --output=../x")]
    [InlineData("curl -so ../x http://localhost:5000")]
    [InlineData("curl -sSo../x http://localhost:5000")]
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
    [InlineData("dotnet build -p:OutputPath=out/ -p:Configuration=Release")]
    [InlineData("dotnet test --logger \"trx;LogFileName=r.trx\" --results-directory TestResults")]
    [InlineData("dotnet run --project src/App.AppHost > apphost.log 2>&1 &")]
    [InlineData("npm install")]
    [InlineData("npm install -D -P vitest")]
    [InlineData("npm i -O fsevents")]
    [InlineData("npx playwright test --reporter=line")]
    [InlineData("npx playwright install --with-deps chromium")]
    [InlineData("curl -s http://localhost:5000/health")]
    [InlineData("curl -s -o out/health.json http://localhost:5000/health")]
    public void CheckCommand_ToolsWritingInside_AreAllowed(string command) => Bash(command).ShouldBeNull();

    // ── heredocs and command substitutions ────────────────────────────────────────────────────────────────────

    // A heredoc commit message or pull-request body is text, not commands — "rm -rf ../x", "1)" or quotes in it must not
    // block the call; a substitution that does write is checked like any command.
    [Theory]
    [InlineData("git commit -m \"$(cat <<'EOF'\nfix(board): keep ../x out (1) of it\n\nrm -rf ../x\nEOF\n)\"")]
    [InlineData("gh pr create --title \"feat: icons\" --body \"$(cat <<'EOF'\n## Summary\n1) generated icons\n2) wired them (see #12)\n\n## Test plan\n- npm run build\nEOF\n)\"")]
    [InlineData("gh pr create --title \"x\" --body \"$(cat <<'EOF'\n## Summary\n1) Fixed the \"login\" redirect -> /home\n- removed ../tmp usage\nEOF\n)\"")]
    [InlineData("git commit -m \"$(cat <<'EOF'\nfix: handle case 1) where it's off\n\nCo-Authored-By: X <x@y>\nEOF\n)\"")]
    [InlineData("cat > notes.md <<'EOF'\nmkdir ../x\nEOF\ngit add notes.md")]
    [InlineData("cat > notes.md <<EOF\nhello $USER\nEOF")]
    public void CheckCommand_HeredocText_IsNotParsedAsCommands(string command) => Bash(command).ShouldBeNull();

    [Theory]
    [InlineData("cat > ../notes.md <<'EOF'\nhello\nEOF")]
    [InlineData("ls $(mkdir ../x)")]
    [InlineData("git commit -m \"$(mkdir ../x)\"")]
    [InlineData("git status `mkdir ../x`")]
    [InlineData("echo \"$(cd .. && touch x)\"")]
    public void CheckCommand_Substitutions_AreChecked(string command) => Bash(command).ShouldNotBeNull();

    // ── scripts: what a ticket needs, and what the incident did ───────────────────────────────────────────────

    [Theory]
    [InlineData("node repo/tools/gen.mjs")]               // committed
    [InlineData("node repo/edited/mod.mjs")]              // tracked, changed by the run: ships in the pull request
    [InlineData("node repo/dist/server.js")]              // a build output the run didn't write itself
    [InlineData("node repo/node_modules/.bin/vite build")]
    [InlineData("npm run build && node repo/missing/server.js")] // not built yet when checked
    [InlineData("node --test")]
    [InlineData("node --version")]
    [InlineData("python -m pytest")]
    [InlineData("sh -c \"dotnet build\"")]
    [InlineData("sh .agentos/bin/copy-in .agentos/files/logo.png repo/wwwroot/logo.png")]
    [InlineData("./repo/node_modules/.bin/vite build")]
    [InlineData("./repo/tools/build.sh")]
    public void CheckCommand_RepositoryScriptsAndBuildOutputs_AreAllowed(string command) => Bash(command).ShouldBeNull();

    [Theory]
    [InlineData("node ./helper.mjs")]
    [InlineData("node ../outside.mjs")]
    [InlineData("node -e \"require('fs').mkdirSync('../x')\"")]
    [InlineData("node --eval=1")]
    [InlineData("node --import ./hook.mjs repo/tools/x.mjs")]
    [InlineData("node -r ./hook.mjs repo/tools/x.mjs")]
    [InlineData("NODE_OPTIONS='--require ./hook.mjs' node repo/tools/x.mjs")]
    [InlineData("NODE_OPTIONS=--require=./hook.mjs npm test")]
    [InlineData("node < helper.mjs")]
    [InlineData("node - < helper.mjs")]
    [InlineData("cat helper.mjs | node")]
    [InlineData("node <<'EOF'\nrequire('fs').mkdirSync('../x')\nEOF")]
    [InlineData("python - <<'EOF'\nopen('../x','w')\nEOF")]
    [InlineData("python3 -c \"open('../x','w')\"")]
    [InlineData("python helper.py")]
    [InlineData("sh -c \"mkdir ../x\"")]
    [InlineData("bash -lc 'mkdir ../x'")]
    [InlineData("bash -c -- 'mkdir ../x'")]
    [InlineData("bash -c 'cd .. && touch x'")]
    [InlineData("bash helper.sh")]
    [InlineData("./helper.sh")]
    [InlineData("source ./helper.sh")]
    [InlineData("xargs rm")]
    [InlineData("ls | xargs -I{} cp {} ../x")]
    [InlineData("find . -name '*.cs' -exec cp {} ../x \\;")]
    [InlineData("FOO=1 node ./helper.mjs")]
    [InlineData("env FOO=1 node ./helper.mjs")]
    [InlineData("nohup node ./helper.mjs &")]
    [InlineData("dotnet run helper.cs")]
    [InlineData("dotnet run --file helper.cs")]
    [InlineData("dotnet fsi helper.fsx")]
    [InlineData("dotnet exec helper.dll")]
    [InlineData("npm exec -- node -e \"require('fs').mkdirSync('../x')\"")]
    [InlineData("npm x -c 'mkdir ../x'")]
    [InlineData("npx node ./helper.mjs")]
    [InlineData("pnpm dlx node ./helper.mjs")]
    [InlineData("yarn node ./helper.mjs")]
    [InlineData("npm pkg set scripts.x=\"node ./new.mjs\" && npm run x")]
    public void CheckCommand_CodeTheRunCouldHaveWritten_IsBlocked(string command) => Bash(command).ShouldNotBeNull();

    // A new script the ticket asked for runs once it is committed — the block says exactly how.
    [Fact]
    public void CheckCommand_NewUntrackedScript_SaysCommitItFirst()
    {
        var reason = Bash("node web/scripts/gen-icons.mjs").ShouldNotBeNull();

        reason.ShouldContain("Commit it first (`git add web/scripts/gen-icons.mjs && git commit -m");
        reason.ShouldContain("ships in the pull request");
        reason.ShouldContain("run it again");
    }

    // A gitignored file the run wrote itself (here: earlier, per the ledger) is not a build output.
    [Fact]
    public void CheckCommand_IgnoredFileTheRunWrote_IsBlocked()
    {
        var written = Path.GetFullPath(Path.Join(_run, "repo", "dist", "evil.js"));
        var ctx = Ctx(path => ScopedWriteGuard.IsInside(path, written));

        ScopedWriteGuard.CheckCommand("node repo/dist/evil.js", _run, ctx, powerShell: false).ShouldNotBeNull();
        ScopedWriteGuard.CheckCommand("node repo/dist/server.js", _run, Ctx(), powerShell: false).ShouldBeNull();
        // …and within one command: written, then run.
        Bash("cp helper.mjs repo/dist/x.js && node repo/dist/x.js").ShouldNotBeNull();
    }

    [Fact]
    public void CheckCommand_WriteTargets_AreRecordedForTheLedger()
    {
        var ctx = Ctx();

        ScopedWriteGuard.CheckCommand("echo x > out/a.txt && cp a b && mkdir -p d", _run, ctx, powerShell: false).ShouldBeNull();

        ctx.Written.ShouldContain(Path.GetFullPath(Path.Join(_run, "out", "a.txt")));
        ctx.Written.ShouldContain(Path.GetFullPath(Path.Join(_run, "b")));
        ctx.Written.ShouldNotContain(Path.GetFullPath(Path.Join(_run, "d"))); // a folder made is not content written
    }

    // ── PowerShell / cmd ──────────────────────────────────────────────────────────────────────────────────────

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
    [InlineData("& { mkdir ../x }")]
    [InlineData("Start-Process node -ArgumentList './helper.mjs'")]
    [InlineData("Invoke-Expression 'mkdir ../x'")]
    [InlineData("iex 'mkdir ../x'")]
    [InlineData("cmd /c mkdir ..\\x")]
    [InlineData("pwsh -Command \"New-Item -ItemType Directory ../x\"")]
    [InlineData("pwsh -File ./helper.ps1")]
    public void CheckCommand_PowerShellWritesOutside_AreBlocked(string command) => Ps(command).ShouldNotBeNull();

    [Fact]
    public void CheckCommand_PwshEncodedCommand_IsDecodedAndChecked()
    {
        var bad = Convert.ToBase64String(Encoding.Unicode.GetBytes("New-Item -ItemType Directory ../x"));
        var fine = Convert.ToBase64String(Encoding.Unicode.GetBytes("Get-ChildItem"));

        Bash("pwsh -EncodedCommand " + bad).ShouldNotBeNull();
        Bash("pwsh -enc " + fine).ShouldBeNull();
        Bash("pwsh -EncodedCommand not-base64!").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("New-Item -ItemType Directory -Path out\\base -Force")]
    [InlineData("Set-Content -Path notes.txt -Value '../not-a-path'")]
    [InlineData("dotnet build *> build.log")]
    [InlineData("dotnet test 2>$null")]
    [InlineData("Copy-Item a.txt b.txt")]
    [InlineData("Set-Location repo; dotnet build")]
    [InlineData("git log -1 --format=`\"%H`\"")]
    [InlineData("[System.IO.Path]::GetFileName('a/b.txt')")]
    [InlineData("pwsh -NoProfile -File repo/tools/build.ps1")]
    public void CheckCommand_PowerShellWritesInside_AreAllowed(string command) => Ps(command).ShouldBeNull();

    // ── the file tools ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckFileTool_InsideIsAllowed_OutsideAndProtectedAreBlocked()
    {
        File(Path.Join(_run, "repo", "src", "a.cs")).ShouldBeNull();
        File("repo/src/a.cs").ShouldBeNull();
        File(Path.Join(_run, "repo", ".claude", "skills", "x", "SKILL.md")).ShouldBeNull();
        File(Path.Join(_run, "repo", "package.json")).ShouldBeNull();
        File(null).ShouldBeNull();

        File(Path.Join(_root, "base-122", "x.mjs")).ShouldNotBeNull();
        File("../x.txt").ShouldNotBeNull();
        File("~/.bashrc").ShouldNotBeNull();
        File(Path.Join(_run, "repo", ".git", "hooks", "pre-commit")).ShouldNotBeNull();
        File(Path.Join(_run, "repo", ".git", "config")).ShouldNotBeNull();
        File(Path.Join(_run, "repo", ".husky", "pre-commit")).ShouldNotBeNull();
        File(Path.Join(_run, "repo", ".claude", "settings.local.json")).ShouldNotBeNull();
        File(Path.Join(_run, ".claude", "settings.json")).ShouldNotBeNull();
        Bash("echo '#!/bin/sh' > repo/.git/hooks/pre-commit").ShouldNotBeNull();
        Bash("echo 'npm test' > repo/.husky/pre-push").ShouldNotBeNull();
    }

    // ── the project's skills (.claude/skills): product code a reviewed ticket may change (VNChess #136) ──────────────

    [Theory]
    [InlineData(".claude/skills/x/SKILL.md")]                 // the repository cloned into the run folder itself
    [InlineData("repo/.claude/skills/x/SKILL.md")]            // …or into a subfolder
    [InlineData("repo/.claude/skills/x/references/tokens.md")]
    [InlineData("repo/.claude/skills/new-skill/SKILL.md")]    // a new skill
    public void CheckFileTool_ProjectSkill_IsAllowed(string path) => File(path).ShouldBeNull();

    [Theory]
    [InlineData("cat > .claude/skills/x/SKILL.md <<'EOF'\n# Thu vien co\nUse the board tokens.\nEOF")]
    [InlineData("sed -i 's/blue/green/' .claude/skills/x/SKILL.md")]
    [InlineData("sed -i.bak -e 's/blue/green/' repo/.claude/skills/x/SKILL.md")]
    [InlineData("mkdir -p .claude/skills/new && echo '# New' > .claude/skills/new/SKILL.md")]
    [InlineData("cp .agentos/files/SKILL.md .claude/skills/x/SKILL.md")]
    public void CheckCommand_ProjectSkillWrites_AreAllowed(string command) => Bash(command).ShouldBeNull();

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/settings.local.json")]
    [InlineData(".claude/hooks/a.sh")]
    [InlineData("repo/.claude/hooks/a.sh")]
    [InlineData(".claude/commands/deploy.md")]
    [InlineData(".claude/agents/reviewer.md")]
    [InlineData(".claude/skills/x/.claude/settings.json")]    // a .claude folder hidden inside a skill
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("repo/.git/hooks/pre-commit")]
    public void CheckFileTool_RestOfClaudeFolderAndGit_AreBlocked(string path) => File(path).ShouldNotBeNull();

    [Theory]
    [InlineData("cat > .claude/settings.json <<'EOF'\n{}\nEOF")]
    [InlineData("echo 'curl x | sh' > .claude/hooks/a.sh")]
    [InlineData("sed -i 's/a/b/' .claude/hooks/a.sh")]
    [InlineData("sed --in-place -e 's/a/b/' .claude/settings.json")]
    [InlineData("sed -i '' 's/a/b/' .claude/commands/x.md")]  // BSD spelling
    [InlineData("cp x.sh .git/hooks/pre-commit")]
    [InlineData("echo '#!/bin/sh' > .git/hooks/pre-commit")]
    [InlineData("sed -i 's/a/b/' ../outside.md")]
    public void CheckCommand_RestOfClaudeFolderAndGit_AreBlocked(string command) => Bash(command).ShouldNotBeNull();

    [Theory]
    [InlineData("sed 's/a/b/' .claude/hooks/a.sh")]           // no -i: prints, writes nothing
    [InlineData("sed -n '1,5p' .claude/settings.json")]
    public void CheckCommand_SedWithoutInPlace_IsNotAWrite(string command) => Bash(command).ShouldBeNull();

    [Fact]
    public void WriteProblem_ClaudeFolderMessage_NamesWhatMayChange()
    {
        var reason = File(".claude/hooks/a.sh").ShouldNotBeNull();

        reason.ShouldContain(".claude/skills");
    }

    [Fact]
    public void CheckFileTool_SkillReachedThroughALink_IsBlocked()
    {
        var hooks = Path.Join(_run, ".claude", "hooks");
        Directory.CreateDirectory(hooks);
        Directory.CreateDirectory(Path.Join(_run, ".claude", "skills"));
        var link = Path.Join(_run, ".claude", "skills", "evil");
        if (!TestFiles.TryIo(() => Directory.CreateSymbolicLink(link, hooks))) { return; } // no symlink rights on this machine

        File(".claude/skills/evil/a.sh").ShouldNotBeNull();
        ScopedWriteGuard.ApprovesSkillWrite(PermissionJson("Write", ".claude/skills/evil/a.sh"), Ctx()).ShouldBeFalse();
    }

    // The PermissionRequest answer — what lets claude -p write a protected path at all, for the skills only.

    private string PermissionJson(string tool, string path) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["session_id"] = "s",
        ["hook_event_name"] = "PermissionRequest",
        ["permission_mode"] = "acceptEdits",
        ["cwd"] = _run,
        ["tool_name"] = tool,
        ["tool_input"] = new Dictionary<string, string> { ["file_path"] = path },
    });

    [Theory]
    [InlineData("Edit", ".claude/skills/x/SKILL.md", true)]
    [InlineData("Write", ".claude/skills/new/SKILL.md", true)]
    [InlineData("MultiEdit", "repo/.claude/skills/x/SKILL.md", true)]
    [InlineData("Write", ".claude/skills/x/scripts/check.py", true)]
    [InlineData("Write", ".claude/settings.json", false)]
    [InlineData("Write", ".claude/settings.local.json", false)]
    [InlineData("Write", ".claude/hooks/a.sh", false)]
    [InlineData("Write", ".claude/commands/x.md", false)]
    [InlineData("Write", ".claude/agents/x.md", false)]
    [InlineData("Write", ".claude/skills", false)]                 // the folder itself, not a skill's file
    [InlineData("Write", ".claude/skills/x/.envrc", false)]        // hidden files below skills
    [InlineData("Write", ".claude/skills/.hidden/SKILL.md", false)]
    [InlineData("Write", ".git/hooks/pre-commit", false)]
    [InlineData("Write", ".vscode/settings.json", false)]
    [InlineData("Write", "../.claude/skills/x/SKILL.md", false)]    // outside the run folder
    [InlineData("NotebookEdit", ".claude/skills/x/SKILL.md", false)]
    [InlineData("Bash", ".claude/skills/x/SKILL.md", false)]
    public void ApprovesSkillWrite_OnlyTheProjectSkillsFiles(string tool, string path, bool expected) =>
        ScopedWriteGuard.ApprovesSkillWrite(PermissionJson(tool, path), Ctx()).ShouldBe(expected);

    [Fact]
    public void Run_PermissionRequestForASkill_AnswersAllowOnStdout()
    {
        using var input = new StringReader(PermissionJson("Edit", Path.Join(_run, ".claude", "skills", "thu-vien-co-design", "SKILL.md")));
        using var error = new StringWriter();
        using var output = new StringWriter();

        ScopedWriteGuard.Run(input, error, _run, _tmp, output: output).ShouldBe(0);

        using var doc = JsonDocument.Parse(output.ToString());
        var answer = doc.RootElement.GetProperty("hookSpecificOutput");
        answer.GetProperty("hookEventName").GetString().ShouldBe("PermissionRequest");
        answer.GetProperty("decision").GetProperty("behavior").GetString().ShouldBe("allow");
        error.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/hooks/a.sh")]
    [InlineData(".git/hooks/pre-commit")]
    public void Run_PermissionRequestForAnythingElse_GivesNoAnswer(string path)
    {
        using var input = new StringReader(PermissionJson("Write", path));
        using var error = new StringWriter();
        using var output = new StringWriter();

        ScopedWriteGuard.Run(input, error, _run, _tmp, output: output).ShouldBe(0);

        output.ToString().ShouldBeEmpty(); // unanswered: claude -p denies it
    }

    [Fact]
    public void Run_PermissionRequestOutsideAScopedRun_GivesNoAnswer()
    {
        using var input = new StringReader(PermissionJson("Edit", ".claude/skills/x/SKILL.md"));
        using var error = new StringWriter();
        using var output = new StringWriter();

        ScopedWriteGuard.Run(input, error, runDir: null, output: output).ShouldBe(0);

        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void CheckFileTool_GitBashDrivePath_ResolvesToTheWindowsPath()
    {
        if (!OperatingSystem.IsWindows()) { return; } // "/c/…" is a Git Bash spelling of "C:\…"
        var drive = char.ToLowerInvariant(_run[0]);
        var gitBash = "/" + drive + _run[2..].Replace('\\', '/') + "/repo/a.txt";

        File(gitBash).ShouldBeNull();
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

        var code = ScopedWriteGuard.Run(input, error, _run, _tmp);

        code.ShouldBe(expected);
        if (expected == 2) { error.ToString().ShouldStartWith("Blocked by the AgentOS.NET runner:"); }
        else { error.ToString().ShouldBeEmpty(); }
    }

    // The ledger: an allowed call's write targets are appended; a later call can't pass the ignored file off as a build output.
    [Fact]
    public void Run_Ledger_RemembersWhatEarlierCallsWrote()
    {
        var ledger = Path.Join(_root, "run.ledger");
        Directory.CreateDirectory(Path.Join(_run, "repo", "dist"));
        var evil = Path.Join(_run, "repo", "dist", "evil.js");
        using (var write = new StringReader(HookJson("Write", "file_path", evil)))
        using (var error = new StringWriter())
        {
            ScopedWriteGuard.Run(write, error, _run, _tmp, ledger).ShouldBe(0);
        }
        System.IO.File.WriteAllText(evil, "x");

        System.IO.File.ReadAllText(ledger).ShouldContain("evil.js");
        using var run = new StringReader(HookJson("Bash", "command", "node repo/dist/evil.js"));
        using var runError = new StringWriter();
        // The real git check: not a repository here, so the file is refused either way — the ledger decides for ignored ones.
        ScopedWriteGuard.Run(run, runError, _run, _tmp, ledger).ShouldBe(2);
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

    // An allow rule can't pre-approve a protected path (.claude/skills) — claude's safety check runs first — so the skill
    // permission is a PermissionRequest hook in the per-run settings, never an --allowedTools rule.
    [Fact]
    public void HookSettingsJson_AnswersPermissionRequestsForTheFileTools()
    {
        using var doc = JsonDocument.Parse(ScopedWriteGuard.HookSettingsJson("\"C:/x/AgentOs.RemoteAgent.exe\" guard"));
        var entry = doc.RootElement.GetProperty("hooks").GetProperty("PermissionRequest")[0];

        entry.GetProperty("matcher").GetString().ShouldBe("Write|Edit|MultiEdit");
        entry.GetProperty("hooks")[0].GetProperty("command").GetString().ShouldBe("\"C:/x/AgentOs.RemoteAgent.exe\" guard");
    }

    // The skill permission rides on the guard's settings, which only a scoped claude run with the runner's own flags gets.
    [Theory]
    [InlineData(true, false, false, "claude", true)]
    [InlineData(false, false, false, "claude", false)] // answer-only
    [InlineData(true, true, false, "claude", false)]   // REMOTE_AGENT_YOLO
    [InlineData(true, false, true, "claude", false)]   // REMOTE_AGENT_ARGS
    [InlineData(true, false, false, "codex", false)]
    public void Guards_OnlyScopedClaudeRuns(bool scoped, bool yolo, bool customArgs, string command, bool expected) =>
        ScopedWriteGuard.Guards(scoped, yolo, customArgs, command).ShouldBe(expected);

    private static bool IsSkillScriptRule(string rule) => rule is "Bash(node .claude/skills/*)" or "PowerShell(node .claude/skills/*)";

    [Fact]
    public void ArgsFor_NeverAllowsTheClaudeFolderByRule()
    {
        // Running a skill's own script is not an edit (edits to .claude/skills ride on the guard's hook only) — the one
        // .claude rule allowed, for Bash and its PowerShell twin.
        ScopedAutonomy.ArgsFor("claude").ShouldNotContain(a => a.Contains(".claude", StringComparison.OrdinalIgnoreCase)
            && !a.StartsWith("Read(", StringComparison.Ordinal) && !IsSkillScriptRule(a));
        ScopedAutonomy.ClaudeAllowed.ShouldNotContain(a => a.StartsWith("Edit", StringComparison.Ordinal) || a.StartsWith("Write", StringComparison.Ordinal));
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
    public void WriteSettings_AndLedger_LandOutsideTheRunFolder_TempBesideIt()
    {
        var folder = Path.Join(_root, "settings");

        var path = ScopedWriteGuard.WriteSettings(folder, _run, "\"x\" guard");

        ScopedWriteGuard.IsInside(path, _run).ShouldBeFalse();
        ScopedWriteGuard.IsInside(ScopedWriteGuard.LedgerPath(folder, _run), _run).ShouldBeFalse();
        System.IO.File.ReadAllText(path).ShouldContain("PreToolUse");
        ScopedWriteGuard.TempFolderFor(_run).ShouldBe(_run + ".tmp");
        RunsRootWatch.IsRunnersOwn(Path.GetFileName(ScopedWriteGuard.TempFolderFor(_run))).ShouldBeTrue();
    }

    // ── what git says, against real git ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScriptStateOf_RealGit_CleanTrackedUntrackedIgnoredMissing()
    {
        var repo = Path.Join(_run, "repo");
        if (!Git(repo, "init", "-q")) { return; } // no git on this machine
        System.IO.File.WriteAllText(Path.Join(repo, "tools", "gen.mjs"), "console.log(1)\n");
        System.IO.File.WriteAllText(Path.Join(repo, ".gitignore"), "dist/\n");
        Git(repo, "add", ".").ShouldBeTrue();
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "init").ShouldBeTrue();
        System.IO.File.WriteAllText(Path.Join(repo, "helper.mjs"), "x");
        Directory.CreateDirectory(Path.Join(repo, "dist"));
        System.IO.File.WriteAllText(Path.Join(repo, "dist", "server.js"), "x");

        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "tools", "gen.mjs")).ShouldBe(ScriptState.Clean);
        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "helper.mjs")).ShouldBe(ScriptState.Untracked);
        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "dist", "server.js")).ShouldBe(ScriptState.Ignored);
        Git(repo, "add", "helper.mjs").ShouldBeTrue();
        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "helper.mjs")).ShouldBe(ScriptState.Tracked);       // staged
        System.IO.File.AppendAllText(Path.Join(repo, "tools", "gen.mjs"), "require('fs')\n");
        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "tools", "gen.mjs")).ShouldBe(ScriptState.Tracked);  // changed by the run
        ScopedWriteGuard.ScriptStateOf(Path.Join(repo, "missing.mjs")).ShouldBe(ScriptState.Missing);
        System.IO.File.WriteAllText(Path.Join(_root, "outside.mjs"), "x");
        ScopedWriteGuard.ScriptStateOf(Path.Join(_root, "outside.mjs")).ShouldBe(ScriptState.NotInRepo);
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
