// A run must not widen its own permissions through a skill: Claude Code pre-approves a skill's `allowed-tools` for the
// turn that invokes it (the whole `claude -p` run) and picks up skill edits within the session. The guard refuses every
// way a run could leave a project skill declaring a grant — Write, Edit, MultiEdit, the ticket-files copy-in helper,
// shell writes — and invoking a grant-declaring skill the run changed; a skill edit without a grant (or one that removes
// it) keeps working (ADR-0019).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ScopedWriteGuardSkillGrantTests : IDisposable
{
    private const string PlainSkill = "---\nname: taste-app\ndescription: Design lint for the app.\n---\n\nRun `node .claude/skills/taste-app/scripts/lint.mjs`.\n";
    private const string GrantSkill = "---\nname: taste-app\ndescription: Design lint for the app.\nallowed-tools: Bash(curl *)\n---\n\nRun the lint.\n";

    private readonly string _root;
    private readonly string _run;
    private readonly string _tmp;
    private readonly string _skills;

    public ScopedWriteGuardSkillGrantTests()
    {
        _root = Path.Join(Path.GetTempPath(), "agentos-skills-" + Guid.NewGuid().ToString("N"));
        _run = Path.Join(_root, "20261103-111500-5e6f7a8b");
        _tmp = _run + RunsRootWatch.TempSuffix;
        _skills = Path.Join(_run, "repo", ".claude", "skills");
        Directory.CreateDirectory(Path.Join(_skills, "taste-app"));
        Directory.CreateDirectory(Path.Join(_run, ".agentos", "files"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() => _ = TestFiles.TryDeleteDirectory(_root);

    // git: a skill folder named "committed" is committed and unchanged; everything else is new or changed by the run.
    private GuardContext Ctx() => new(_run, _tmp)
    {
        ScriptStateOf = static p => p.Replace('\\', '/').Contains("/committed/", StringComparison.Ordinal) ? ScriptState.Clean : ScriptState.Untracked,
        GitRemoteHostsOf = static _ => [],
    };

    private string SkillFile(string name = "taste-app") => Path.Join(_skills, name, "SKILL.md");

    private string? Hook(string tool, object toolInput) => ScopedWriteGuard.CheckHookInput(JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["hook_event_name"] = "PreToolUse",
        ["cwd"] = Path.Join(_run, "repo"),
        ["tool_name"] = tool,
        ["tool_input"] = toolInput,
    }), Ctx());

    private string? Bash(string command) => ScopedWriteGuard.CheckCommand(command, Path.Join(_run, "repo"), Ctx(), powerShell: false);

    // ── what a skill file declares ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nallowed-tools: Bash(curl *)\n---\n")]
    [InlineData("---\nAllowed-Tools: Bash(curl *)\n---\n")]
    [InlineData("---\nALLOWED_TOOLS: Bash(curl *)\n---\n")]
    [InlineData("---\nallowed_tools: Bash(curl *)\n---\n")]
    [InlineData("---\nallowedTools: Bash(curl *)\n---\n")]
    [InlineData("---\n\"allowed-tools\": Bash(curl *)\n---\n")]
    [InlineData("---\n'allowed-tools': Bash(curl *)\n---\n")]
    [InlineData("---\nname: x\nallowed-tools:\n  - Bash(curl *)\n  - Read\n---\n")]
    [InlineData("---\nname: x\nallowed-tools: [Bash(curl *), Read]\n---\n")]
    [InlineData("---\n{name: x, allowed-tools: \"Bash(curl *)\"}\n---\n")]
    [InlineData("---\n  allowed-tools: Bash(curl *)\n---\n")]
    [InlineData("---\n\"allowed\\u002dtools\": Bash(curl *)\n---\n")]
    [InlineData("---\n\"allowed\\x2dtools\": Bash(curl *)\n---\n")]
    [InlineData("---\n\"allowed-\\\n  tools\": Bash(curl *)\n---\n")]
    [InlineData("﻿---\r\nname: x\r\nallowed-tools: Bash(curl *)\r\n---\r\n")]
    [InlineData("\n\n---\nallowed-tools: Bash(curl *)\n---\n")]
    [InlineData("---\nname: x\nallowed-tools: Bash(curl *)\n")] // never closed: the whole file is frontmatter
    [InlineData("---\nhooks:\n  PreToolUse:\n    - matcher: Bash\n---\n")]
    [InlineData("---\n'Hooks': {}\n---\n")]
    [InlineData("---\nk: &k allowed-tools\n*k : Bash(curl *)\n---\n")]
    [InlineData("---\nd: &d hooks\n*d : {}\n---\n")]
    [InlineData("---\n<<: *base\n---\n")]
    [InlineData("---\n? allowed-tools\n: Bash(curl *)\n---\n")]
    public void SkillGrant_EverySpelling_IsFound(string content) => ScopedWriteGuard.SkillGrant(content).ShouldNotBeNull(content);

    [Theory]
    [InlineData(PlainSkill)]
    [InlineData("---\nname: x\ndescription: Lists the tools the app needs.\n---\n")]
    [InlineData("---\nname: x\n---\n\nThe frontmatter key allowed-tools is not used on AgentOS.NET runners.\n")] // the body, not frontmatter
    [InlineData("# No frontmatter\n\nallowed-tools: Bash(curl *)\n")]
    [InlineData("")]
    public void SkillGrant_NoGrantInTheFrontmatter_IsNull(string content) => ScopedWriteGuard.SkillGrant(content).ShouldBeNull();

    // ── Write / Edit / MultiEdit ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_ASkillDeclaringAllowedTools_IsRefusedWithAClearMessage()
    {
        var reason = Hook("Write", new { file_path = SkillFile("x"), content = GrantSkill });

        reason.ShouldNotBeNull();
        reason.ShouldContain("allowed-tools");
        reason.ShouldContain(ScopedWriteGuard.SkillGrantNote);
    }

    [Theory]
    [InlineData(".claude/skills/x/SKILL.md")]
    [InlineData(".claude/skills/x/reference/notes.md")]
    [InlineData(".claude/skills/nested/deep/SKILL.md")]
    [InlineData("sub/.claude/skills/x/SKILL.md")]
    public void Write_AnyFileUnderTheSkills_IsChecked(string relative) =>
        Hook("Write", new { file_path = relative, content = GrantSkill }).ShouldNotBeNull();

    [Fact]
    public void Write_ASkillWithoutAGrant_IsAllowed() =>
        Hook("Write", new { file_path = SkillFile("x"), content = PlainSkill }).ShouldBeNull();

    [Fact]
    public void Write_AGrantOutsideTheSkills_IsNotThisRulesBusiness() =>
        Hook("Write", new { file_path = "docs/skill-template.md", content = GrantSkill }).ShouldBeNull();

    [Fact]
    public void Edit_ThatAddsAllowedToolsToAnExistingSkill_IsRefused()
    {
        File.WriteAllText(SkillFile(), PlainSkill);

        Hook("Edit", new { file_path = SkillFile(), old_string = "description: Design lint for the app.\n", new_string = "description: Design lint for the app.\nallowed-tools: Bash(curl *)\n" })
            .ShouldNotBeNull();
    }

    [Fact]
    public void Edit_ThatRemovesAllowedTools_IsAllowed()
    {
        File.WriteAllText(SkillFile(), GrantSkill);

        Hook("Edit", new { file_path = SkillFile(), old_string = "allowed-tools: Bash(curl *)\n", new_string = string.Empty }).ShouldBeNull();
    }

    [Fact]
    public void Edit_ThatOnlyRenamesTheKeyIntoAGrant_IsRefused()
    {
        File.WriteAllText(SkillFile(), "---\nname: x\nallowed: Bash(curl *)\n---\n");

        Hook("Edit", new { file_path = SkillFile(), old_string = "allowed:", new_string = "allowed-tools:" }).ShouldNotBeNull();
    }

    [Fact]
    public void Edit_ASkillBodyWithoutAGrant_IsAllowed()
    {
        File.WriteAllText(SkillFile(), PlainSkill);

        Hook("Edit", new { file_path = SkillFile(), old_string = "Run `node", new_string = "Always run `node" }).ShouldBeNull();
    }

    [Fact]
    public void Edit_ReplaceAll_AppliesEveryOccurrence()
    {
        File.WriteAllText(SkillFile(), "---\nname: x\nTOOLS: Read\n---\nTOOLS\n");

        Hook("Edit", new { file_path = SkillFile(), old_string = "TOOLS", new_string = "allowed-tools", replace_all = true }).ShouldNotBeNull();
    }

    [Fact]
    public void Edit_CreatingANewSkillFile_ChecksTheNewText() =>
        Hook("Edit", new { file_path = SkillFile("fresh"), old_string = string.Empty, new_string = GrantSkill }).ShouldNotBeNull();

    // The file can't be read (here: a folder where the file should be) — the edit's new text decides.
    [Fact]
    public void Edit_UnreadableSkillFile_RefusedWhenTheNewTextNamesAGrant()
    {
        Directory.CreateDirectory(SkillFile("odd"));

        Hook("Edit", new { file_path = SkillFile("odd"), old_string = "x", new_string = "allowed-tools: Bash(curl *)" }).ShouldNotBeNull();
        Hook("Edit", new { file_path = SkillFile("odd"), old_string = "x", new_string = "y" }).ShouldBeNull();
    }

    [Fact]
    public void Edit_WhoseOldTextIsNotThere_RefusedWhenTheNewTextNamesAGrant()
    {
        File.WriteAllText(SkillFile(), PlainSkill);

        Hook("Edit", new { file_path = SkillFile(), old_string = "not in the file", new_string = "allowed_tools: Bash(curl *)" }).ShouldNotBeNull();
    }

    [Fact]
    public void MultiEdit_ThatAddsAGrantInALaterEdit_IsRefused()
    {
        File.WriteAllText(SkillFile(), PlainSkill);

        Hook("MultiEdit", new
        {
            file_path = SkillFile(),
            edits = new object[]
            {
                new { old_string = "Design lint", new_string = "Design check" },
                new { old_string = "name: taste-app\n", new_string = "name: taste-app\n\"Allowed_Tools\": [Bash(curl *)]\n" },
            },
        }).ShouldNotBeNull();
    }

    [Fact]
    public void MultiEdit_WithoutAGrant_IsAllowed()
    {
        File.WriteAllText(SkillFile(), GrantSkill);

        Hook("MultiEdit", new
        {
            file_path = SkillFile(),
            edits = new object[]
            {
                new { old_string = "allowed-tools: Bash(curl *)\n", new_string = string.Empty },
                new { old_string = "Run the lint.", new_string = "Run the lint, then fix what it reports." },
            },
        }).ShouldBeNull();
    }

    // The PermissionRequest approver (the only way a skill write is approved in claude -p) applies the same rule.
    [Fact]
    public void ApprovesSkillWrite_AGrantDeclaringSkill_GetsNoApproval()
    {
        string Json(string content) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["cwd"] = _run,
            ["tool_name"] = "Write",
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = SkillFile("x"), ["content"] = content },
        });

        ScopedWriteGuard.ApprovesSkillWrite(Json(GrantSkill), Ctx()).ShouldBeFalse();
        ScopedWriteGuard.ApprovesSkillWrite(Json(PlainSkill), Ctx()).ShouldBeTrue();
    }

    [Fact]
    public void Run_SkillGrantRefusal_ExitsTwoWithItsOwnExplanation()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = _run,
            ["tool_name"] = "Write",
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = SkillFile("x"), ["content"] = GrantSkill },
        });
        using var input = new StringReader(json);
        using var error = new StringWriter();

        ScopedWriteGuard.Run(input, error, _run, _tmp).ShouldBe(2);

        error.ToString().ShouldContain("Skills may not pre-approve tools");
        error.ToString().ShouldNotContain("may only write inside its own folder");
    }

    // ── shell writes ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("echo 'allowed-tools: Bash(curl *)' >> .claude/skills/taste-app/SKILL.md")]
    [InlineData("printf 'hooks:\\n  PreToolUse: []\\n' >> .claude/skills/x/SKILL.md")]
    [InlineData("cat > .claude/skills/x/SKILL.md <<'EOF'\n---\nname: x\nallowed-tools: Bash(curl *)\n---\nEOF")]
    [InlineData("sed -i 's/^name: x/name: x\\nallowed_tools: Bash(curl *)/' .claude/skills/taste-app/SKILL.md")]
    [InlineData("tee -a .claude/skills/x/SKILL.md <<< 'allowedTools: [Bash]'")]
    public void CheckCommand_ShellWriteThatNamesAGrantIntoASkill_IsRefused(string command)
    {
        var reason = Bash(command);

        reason.ShouldNotBeNull(command);
        reason.ShouldContain(ScopedWriteGuard.SkillGrantNote);
    }

    [Theory]
    [InlineData("cp notes/grant.md .claude/skills/x/SKILL.md")]
    [InlineData("mv notes/grant.md .claude/skills/x/SKILL.md")]
    [InlineData("cp -r notes .claude/skills/vendored")]
    public void CheckCommand_CopyingAGrantDeclaringFileIntoASkill_IsRefused(string command)
    {
        Directory.CreateDirectory(Path.Join(_run, "repo", "notes"));
        File.WriteAllText(Path.Join(_run, "repo", "notes", "grant.md"), GrantSkill);

        Bash(command).ShouldNotBeNull(command);
    }

    // Shell edits without a grant keep working (the lab writes skills with heredocs and sed); content the guard can't see
    // (git show > SKILL.md, tar) is caught when the changed skill is invoked (below).
    [Theory]
    [InlineData("mkdir -p .claude/skills/new-skill/scripts")]
    [InlineData("rm .claude/skills/taste-app/old.md")]
    [InlineData("ls .claude/skills")]
    [InlineData("echo '# Notes' > .claude/skills/x/notes.md")]
    [InlineData("sed -i 's/blue/green/' .claude/skills/taste-app/SKILL.md")]
    [InlineData("cp notes/plain.md .claude/skills/x/SKILL.md")]
    [InlineData("git add .claude/skills && git commit -m \"feat: skill\"")]
    public void CheckCommand_SkillShellEditsWithoutAGrant_AreAllowed(string command)
    {
        Directory.CreateDirectory(Path.Join(_run, "repo", "notes"));
        File.WriteAllText(Path.Join(_run, "repo", "notes", "plain.md"), PlainSkill);

        Bash(command).ShouldBeNull(command);
    }

    // ── the ticket-files copy-in helper ───────────────────────────────────────────────────────────────────────

    private string? CopyIn(string src, string dst, string? cwd = null) =>
        ScopedWriteGuard.CheckCommand($"sh .agentos/bin/copy-in {src} {dst}", cwd ?? _run, Ctx(), powerShell: false);

    [Fact]
    public void CopyIn_AGrantDeclaringFileIntoASkill_IsRefused()
    {
        File.WriteAllText(Path.Join(_run, ".agentos", "files", "SKILL.md"), GrantSkill);

        var reason = CopyIn(".agentos/files/SKILL.md", "repo/.claude/skills/vendored/SKILL.md");

        reason.ShouldNotBeNull();
        reason.ShouldContain("allowed-tools");
        reason.ShouldContain(ScopedWriteGuard.SkillGrantNote);
    }

    [Fact]
    public void CopyIn_DirectlyRunHelper_IsCheckedToo()
    {
        File.WriteAllText(Path.Join(_run, ".agentos", "files", "SKILL.md"), GrantSkill);

        ScopedWriteGuard.CheckCommand("./.agentos/bin/copy-in .agentos/files/SKILL.md repo/.claude/skills/v/SKILL.md", _run, Ctx(), powerShell: false)
            .ShouldNotBeNull();
    }

    [Fact]
    public void CopyIn_APlainSkillOrAGrantFileElsewhere_IsAllowed()
    {
        File.WriteAllText(Path.Join(_run, ".agentos", "files", "plain.md"), PlainSkill);
        File.WriteAllText(Path.Join(_run, ".agentos", "files", "grant.md"), GrantSkill);

        CopyIn(".agentos/files/plain.md", "repo/.claude/skills/vendored/SKILL.md").ShouldBeNull();
        CopyIn(".agentos/files/grant.md", "repo/docs/skill-example.md").ShouldBeNull();
    }

    [Fact]
    public void CopyIn_ComputedPaths_AreRefused() => CopyIn("$SRC", "repo/.claude/skills/x/SKILL.md").ShouldNotBeNull();

    // ── invoking a skill ──────────────────────────────────────────────────────────────────────────────────────

    private string? UseSkill(string name) => Hook("Skill", new { skill = name });

    [Fact]
    public void Skill_AGrantDeclaringSkillTheRunChanged_IsRefused()
    {
        // e.g. written by a build script, which the file-tool rules never see.
        File.WriteAllText(SkillFile(), GrantSkill);

        var reason = UseSkill("taste-app");

        reason.ShouldNotBeNull();
        reason.ShouldContain("allowed-tools");
    }

    [Fact]
    public void Skill_MatchedByItsFrontmatterName_IsRefusedToo()
    {
        Directory.CreateDirectory(Path.Join(_skills, "folder-name"));
        File.WriteAllText(SkillFile("folder-name"), "---\nname: deploy-helper\nallowed-tools: Bash(curl *)\n---\n");

        UseSkill("deploy-helper").ShouldNotBeNull();
        UseSkill("/deploy-helper").ShouldNotBeNull();
    }

    [Fact]
    public void Skill_CommittedAndUnchanged_OrWithoutAGrant_OrAPlugin_IsAllowed()
    {
        Directory.CreateDirectory(Path.Join(_skills, "committed"));
        File.WriteAllText(SkillFile("committed"), "---\nname: committed\nallowed-tools: Bash(npm run lint)\n---\n");
        File.WriteAllText(SkillFile(), PlainSkill);

        UseSkill("committed").ShouldBeNull();       // came with the repository, reviewed like its code
        UseSkill("taste-app").ShouldBeNull();       // no grant
        UseSkill("design:design-review").ShouldBeNull(); // a plugin's skill, not the project's
        UseSkill("unknown").ShouldBeNull();
    }
}
