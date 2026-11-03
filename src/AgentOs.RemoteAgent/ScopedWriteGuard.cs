// Scoped autonomy keeps a run's writes inside its own folder. `--permission-mode acceptEdits` auto-approves the CLI's
// edits inside the working directory only, but a Bash/PowerShell rule is a PREFIX match on the command line: it can't
// see that `mkdir ../x`, `git clone <repo> ../x`, `cd .. && …` or `> ../out.txt` reach outside. And a script the agent
// wrote into its own folder (auto-approved) and then ran (`node ./helper.mjs`, allowed for the repository's own
// scripts) can write anywhere — that is how a Testing agent left `../base-122` (a snapshot of main) next to the run
// folders on the lab.
//
// So every scoped claude run gets a PreToolUse hook (`--settings <file>`, written per run): claude runs
// `AgentOs.RemoteAgent guard` before each Bash / PowerShell / Write / Edit / MultiEdit / NotebookEdit call, with the call
// on stdin. The guard resolves every path the call would write — relative to the shell's current folder, following the
// `cd`s inside the command, expanding the variables it knows ($TEMP / $TMP / $TMPDIR = the run's own temp folder,
// $AGENTOS_RUN_DIR, $PWD, `$(pwd)`, `$(git rev-parse --show-toplevel)`) — and blocks the call (exit 2, the reason goes
// back to the agent) when one is outside the run folder or its temp folder (AGENTOS_RUN_DIR / AGENTOS_RUN_TMP):
//   - the file tools' file_path / notebook_path;
//   - `cd` / `pushd` / Set-Location out of the run folder, and any command run from a folder outside it;
//   - redirections (`>`, `>>`, `2>`, `&>`, `>&file`, `<>`, `*>`) — except /dev/null, NUL, $null;
//   - file-writing commands (mkdir, touch, cp, mv, rm, ln, tar, unzip, tee, New-Item, Copy-Item, Set-Content, Out-File…):
//     every argument that isn't an option, attached values too (`tar -C../x`, `cp -t../x`), and the files `sed -i` rewrites;
//   - git: `-C <dir>`, `--git-dir` / `--work-tree`, `worktree add <path>`, `clone <repo> <dir>`, `init <dir>`,
//     `archive -o`, `format-patch -o`, `bundle create`; gh: `repo clone <repo> <dir>`;
//   - dotnet / npm / pnpm / yarn / curl / wget output options (`-o`, `-o:`, `--output`, `-so<file>`, `--prefix`,
//     `-p:OutputPath=…`…), global installs (`-g`, `--global`) and `npm pkg set scripts.*`;
//   - code the agent could have written itself: a script run by an interpreter (node, deno, bun, tsx, ts-node, python,
//     sh/bash, pwsh -File, `dotnet run x.cs` / `--file`, `dotnet fsi`, `dotnet exec`, `./x`, `source x`) must be a file
//     the repository tracks (committed, or modified — either way it ships in the pull request's diff) or an ignored
//     build output (dist/, bin/, node_modules/…) the agent didn't write itself; a NEW untracked script is refused with
//     "commit it first"; inline code (`node -e`, `python -c`, a heredoc or a pipe into an interpreter) is refused;
//   - nested commands are checked the same way: `$(…)` and backticks, `sh -c` / `bash -lc`, `pwsh -Command` /
//     `-EncodedCommand`, `cmd /c`, `iex`, Start-Process, `find -exec`, `xargs`, `npm exec` / `npx` / `pnpm dlx`.
// A path that is computed in a way the guard can't follow (`$VAR`, an unknown `$(…)`) where a write goes is refused.
// Every write target an allowed call names is appended to the run's ledger (AGENTOS_GUARD_LEDGER), so an ignored file the
// agent wrote can't later pass as a build output.
// Inside the run folder, git's own folder (.git), husky's hooks (.husky) and Claude Code's project folder (.claude) are
// off limits too — except the project's skills (.claude/skills/**), which are product code for these repositories: they
// steer every agent that works on the project, so a reviewed ticket must be able to change them (ADR-0018 addendum).
//
// The project's skills, though, are one of Claude Code's PROTECTED paths: it never auto-approves a write under `.claude/`
// in acceptEdits mode ("Claude requested permissions to edit <path> which is a sensitive file."), an allow rule
// (`--allowedTools "Edit(…/.claude/skills/**)"`, relative or `//` absolute) does not pre-approve it — the safety check
// runs before the allow rules — and a PreToolUse "allow" doesn't either; in `claude -p` nobody can answer the prompt, so
// the write was denied (VNChess #136, 2026-11). What does answer it is a PermissionRequest hook: the same `guard`
// command, installed for that event too, approves a Write / Edit / MultiEdit of a file under the run folder's
// `.claude/skills/` (no hidden file, nothing reached through a link) and nothing else — every other prompt stays
// unanswered, so every other protected path (.claude/settings*, hooks, commands, agents, .git, .husky, .vscode …) stays
// denied. Verified against Claude Code 2.1.283 with the runner's own flags.
//
// Two more rules ride on the same hook (ADR-0019): a skill may not pre-approve tools — no write, copy or invocation of a
// project skill whose frontmatter declares `allowed-tools` / `hooks` (ScopedWriteGuard.SkillGrants.cs) — and no shell
// command may send data to another computer, whatever allow rule or skill grant approved it (ScopedWriteGuard.Egress.cs).
//
// The hook is a guard rail, not a sandbox: Claude Code runs the tool call anyway when the hook can't run (the runner's
// executable gone mid-run, a crash, a timeout, `disableAllHooks`) — only exit 2 blocks. And what a build tool does with
// the repository's own code (an npm script, an MSBuild target, a test the agent wrote) runs with the member's rights; the
// end-of-run sweep (RunsRootWatch in RunCleanup.cs) removes what such code leaves next to the run folders, and an OS
// sandbox remains the real boundary (docs/adr/0018).

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOs.RemoteAgent;

/// <summary>What git says about a script the agent wants to run.</summary>
internal enum ScriptState
{
    /// <summary>Committed and unchanged.</summary>
    Clean,
    /// <summary>Tracked, with changes (modified, staged, added) — it ships in the pull request's diff.</summary>
    Tracked,
    /// <summary>A new file git doesn't track.</summary>
    Untracked,
    /// <summary>Ignored by the repository (a build output, node_modules).</summary>
    Ignored,
    /// <summary>Not inside a repository checkout.</summary>
    NotInRepo,
    /// <summary>The file doesn't exist (yet).</summary>
    Missing,
}

/// <summary>What one guard check knows: the run's folders, how to ask git about a script, what the run wrote before —
/// and, as the check goes, what this call writes (recorded to the ledger when the call is allowed).</summary>
internal sealed class GuardContext
{
    public GuardContext(string runDir, string? tempDir = null)
    {
        RunDir = Path.GetFullPath(ScopedWriteGuard.FromGitBash(runDir));
        TempDir = string.IsNullOrWhiteSpace(tempDir) ? null : Path.GetFullPath(ScopedWriteGuard.FromGitBash(tempDir));
        // Not the shared /tmp off Windows: it is outside the run (the run's TMPDIR is its own temp folder). On Windows Git
        // Bash's /tmp IS the CLI's TEMP, so Resolve maps it there.
        Roots = TempDir is null ? [RunDir] : [RunDir, TempDir];
    }

    /// <summary>The run folder.</summary>
    public string RunDir { get; }

    /// <summary>The run's own temp folder (TEMP / TMP / TMPDIR of the CLI).</summary>
    public string? TempDir { get; }

    /// <summary>Where a write may land.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Asks git about a script (a fake in tests).</summary>
    public Func<string, ScriptState> ScriptStateOf { get; init; } = ScopedWriteGuard.ScriptStateOf;

    /// <summary>The hosts of the git remotes configured where a command runs (a fake in tests).</summary>
    public Func<string, IReadOnlyCollection<string>> GitRemoteHostsOf { get; init; } = ScopedWriteGuard.GitRemoteHosts;

    /// <summary>Git hosts the computer's owner allows besides the known forges (REMOTE_AGENT_GIT_HOSTS).</summary>
    public IReadOnlyList<string> ExtraGitHosts { get; init; } = [];

    /// <summary>Whether an earlier call of this run wrote that path (the ledger).</summary>
    public Func<string, bool> WrittenEarlier { get; init; } = static _ => false;

    /// <summary>Whether the command being checked names a skill grant (allowed-tools / hooks) anywhere in its text.</summary>
    public bool CommandMentionsGrant { get; set; }

    /// <summary>What this call writes (full paths).</summary>
    public List<string> Written { get; } = [];

    /// <summary>Inside the run folder or its temp folder.</summary>
    public bool IsWritable(string fullPath) => Roots.Any(root => ScopedWriteGuard.IsInside(fullPath, root));

    /// <summary>Written by this run: earlier (the ledger) or by this very call.</summary>
    public bool WrittenByRun(string fullPath) =>
        Written.Exists(w => ScopedWriteGuard.IsInside(fullPath, w)) || WrittenEarlier(fullPath);

    /// <summary>The value of a variable the guard can vouch for, or null.</summary>
    public string? Variable(string name, string? cwd) => name.ToUpperInvariant() switch
    {
        "TEMP" or "TMP" or "TMPDIR" or "AGENTOS_RUN_TMP" => TempDir,
        "AGENTOS_RUN_DIR" => RunDir,
        "PWD" => cwd,
        "HOME" or "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        _ => null,
    };
}

/// <summary>The PreToolUse guard that keeps a scoped run's writes inside its run folder.</summary>
internal static partial class ScopedWriteGuard
{
    /// <summary>The CLI environment variable naming the run folder (the hook reads it).</summary>
    internal const string RunDirVariable = "AGENTOS_RUN_DIR";

    /// <summary>The CLI environment variable naming the run's temp folder.</summary>
    internal const string RunTempVariable = "AGENTOS_RUN_TMP";

    /// <summary>The CLI environment variable naming the run's ledger of written paths.</summary>
    internal const string LedgerVariable = "AGENTOS_GUARD_LEDGER";

    /// <summary>How every block starts — CliProgress recognises it in a tool result and records the call as refused.</summary>
    internal const string BlockedPrefix = "Blocked by the AgentOS.NET runner:";

    /// <summary>The tools the hook is asked about.</summary>
    internal const string Matcher = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit|Skill";

    /// <summary>The tools whose permission prompts the hook may answer (a project skill's file, nothing else).</summary>
    internal const string PermissionMatcher = "Write|Edit|MultiEdit";

    /// <summary>The hook event Claude Code raises when a call needs a permission nobody can give in <c>claude -p</c>.</summary>
    internal const string PermissionRequestEvent = "PermissionRequest";

    /// <summary>A ledger holds at most this many paths; past it, every ignored script counts as written by the run.</summary>
    internal const int MaxLedgerLines = 20_000;

    private const int MaxDepth = 8;

    private static readonly HashSet<string> CdCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "chdir", "pushd", "set-location", "sl", "push-location",
    };

    private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkdir", "md", "touch", "rm", "rmdir", "rd", "del", "erase", "cp", "copy", "mv", "move", "ln", "install", "tee",
        "tar", "unzip", "7z", "rsync", "truncate", "dd", "chmod", "chown",
        "new-item", "ni", "remove-item", "ri", "copy-item", "cpi", "move-item", "mi", "rename-item", "rni", "ren",
        "set-content", "add-content", "ac", "out-file", "tee-object", "expand-archive", "compress-archive", "new-symlink",
    };

    // Write commands that put content somewhere (recorded to the ledger) — not mkdir / rm / touch / chmod.
    private static readonly HashSet<string> ContentCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cp", "copy", "mv", "move", "ln", "install", "tee", "tar", "unzip", "7z", "rsync", "dd", "copy-item", "cpi",
        "move-item", "mi", "rename-item", "rni", "ren", "set-content", "add-content", "ac", "out-file", "tee-object",
        "expand-archive", "new-item", "ni", "new-symlink",
    };

    // Short options whose value may be attached (`tar -C../x`, `cp -t../x`, `unzip -d../x`, `7z -o../x`).
    private static readonly Dictionary<string, string> AttachedValueLetters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tar"] = "Cf", ["cp"] = "t", ["mv"] = "t", ["install"] = "t", ["ln"] = "t", ["unzip"] = "d", ["7z"] = "o",
    };

    // PowerShell cmdlet parameters whose value is not a path.
    private static readonly HashSet<string> PsValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-value", "-itemtype", "-type", "-encoding", "-filter", "-include", "-exclude", "-inputobject", "-stream",
        "-delimiter", "-width", "-credential", "-newname",
    };

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "deno", "bun", "tsx", "ts-node", "python", "python3", "py",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "sh", "bash", "zsh", "dash", "ksh" };

    private static readonly HashSet<string> PowerShells = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell" };

    private static readonly HashSet<string> PackageManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "pnpm", "yarn", "npx", "pnpx", "bunx",
    };

    // Prefixes that run the next word as the command (and shell keywords that start a command).
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "nohup", "time", "command", "exec", "nice", "sudo", "doas", "timeout", "stdbuf", "builtin",
        "{", "!", "then", "do", "else", "elif", "if", "while", "until",
    };

    private static readonly HashSet<string> WrapperValueOptions = new(StringComparer.Ordinal)
    {
        "-u", "-g", "-n", "-s", "-k", "-C", "-D", "-p", "-U", "-h", "-r", "-t", "-i", "-o", "-e",
    };

    private static readonly HashSet<string> NullTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/dev/null", "nul", "$null", "/dev/stdout", "/dev/stderr", "con",
    };

    private static readonly string[] ProgramExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    private static readonly string[] GitStatusArgs = ["status", "--porcelain", "--ignored", "--"];

    // Files inside the run folder that are still off limits: git's own folder (a hook there runs on the next commit),
    // husky's hooks (the same, through the repository), and claude's project folder — its settings (a follow-up run
    // resumed in this folder would load their permissions and hooks), hooks, commands and agents — all but its skills.
    private static readonly string[] ProtectedFiles = ["settings.json", "settings.local.json"];

    private const string ClaudeFolder = ".claude";
    private const string SkillsFolder = "skills";

    // ── the hook ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>AgentOs.RemoteAgent guard</c>: read the hook's JSON from <paramref name="input"/>. For PreToolUse: 0 =
    /// allow, 2 = block (the reason on <paramref name="error"/> goes back to the agent); anything unexpected blocks — but note
    /// that Claude Code itself runs the call when the hook can't run at all (fail-open; see the header). For
    /// PermissionRequest: always 0; the approval of a project skill's file goes to <paramref name="output"/>, and anything
    /// else (or anything unexpected) writes nothing, which leaves the prompt unanswered — denied in <c>claude -p</c>.</summary>
    internal static int Run(TextReader input, TextWriter error, string? runDir, string? tempDir = null, string? ledgerPath = null,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);
        if (string.IsNullOrWhiteSpace(runDir)) { return 0; } // not a scoped run: nothing to confine
        var json = input.ReadToEnd();
        if (IsPermissionRequest(json))
        {
            try
            {
                if (output is not null && ApprovesSkillWrite(json, new GuardContext(runDir, tempDir)))
                {
                    output.Write(SkillApprovalJson());
                    output.Flush();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // no answer = the CLI's own decision: denied
            }
            return 0;
        }
        try
        {
            var ledger = Ledger.Load(ledgerPath);
            var ctx = new GuardContext(runDir, tempDir)
            {
                WrittenEarlier = ledger.Contains,
                ExtraGitHosts = ParseHosts(Environment.GetEnvironmentVariable(GitHostsVariable)),
            };
            if (CheckHookInput(json, ctx) is not { } reason)
            {
                ledger.Append(ctx.Written);
                return 0;
            }
            // A network or skill-grant refusal carries its own explanation; the others are about the run folder.
            var explained = reason.EndsWith(NetworkNote, StringComparison.Ordinal) || reason.Contains(SkillGrantNote, StringComparison.Ordinal);
            error.WriteLine(BlockedPrefix + " " + reason
                + (explained ? string.Empty : " This run may only write inside its own folder (" + runDir + ")."));
            return 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error.WriteLine(BlockedPrefix + " the call could not be checked (" + ex.GetType().Name + ").");
            return 2;
        }
    }

    /// <summary>Why the tool call in <paramref name="json"/> (a PreToolUse hook input) must not run, or null.</summary>
    internal static string? CheckHookInput(string json, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tool = root.TryGetProperty("tool_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var cwd = root.TryGetProperty("cwd", out var c) && c.GetString() is { Length: > 0 } dir ? dir : ctx.RunDir;
        if (!root.TryGetProperty("tool_input", out var toolInput) || toolInput.ValueKind != JsonValueKind.Object) { return null; }
        string? Field(string name) => toolInput.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            "Bash" => Field("command") is { } bash ? CheckCommand(bash, cwd, ctx, powerShell: false) : null,
            "PowerShell" => Field("command") is { } ps ? CheckCommand(ps, cwd, ctx, powerShell: true) : null,
            "Write" or "Edit" or "MultiEdit" => CheckFileTool(Field("file_path"), cwd, ctx)
                ?? CheckSkillGrant(tool, toolInput, Field("file_path"), cwd, ctx),
            "Skill" => CheckSkillUse(Field("skill") ?? Field("command") ?? Field("name"), cwd, ctx),
            "NotebookEdit" => CheckFileTool(Field("notebook_path"), cwd, ctx),
            _ => null,
        };
    }

    /// <summary>A file tool's target must resolve inside the run folder (and not be one of its protected files).</summary>
    internal static string? CheckFileTool(string? path, string cwd, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        if (Resolve(path, cwd, ctx.TempDir) is not { } full) { return $"'{path}' can't be resolved."; }
        if (WriteProblem(full, path, ctx) is { } problem) { return problem; }
        ctx.Written.Add(full);
        return null;
    }

    /// <summary>Why a write to <paramref name="full"/> is refused (outside the run's folders, or a protected file), or null.</summary>
    internal static string? WriteProblem(string full, string shown, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.IsWritable(full)) { return $"'{shown}' is outside the run folder."; }
        if (!IsInside(full, ctx.RunDir)) { return null; }
        var relative = Path.GetRelativePath(ctx.RunDir, full).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && Array.Exists(segments[..^1], seg => seg.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{shown}' is inside git's own folder (.git) — use git commands.";
        }
        if (segments.Length >= 2 && Array.Exists(segments[..^1], seg => seg.Equals(".husky", StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{shown}' is a git hook (.husky) — hooks run on the member's computer; they can't be changed in a run.";
        }
        if (segments.Length >= 2 && segments[^2].Equals(ClaudeFolder, StringComparison.OrdinalIgnoreCase)
            && ProtectedFiles.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return $"'{shown}' is Claude Code's settings file — a run may not change its own permissions.";
        }
        // Inside a .claude folder only its skills may change: not its hooks, commands, agents or anything else there.
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals(ClaudeFolder, StringComparison.OrdinalIgnoreCase)
                && !segments[i + 1].Equals(SkillsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{shown}' is in Claude Code's own folder (.claude) — a run may change the project's skills "
                    + "(.claude/skills) but not its settings, hooks, commands or agents.";
            }
        }
        if (IsProjectSkill(full, ctx.RunDir) && ThroughLink(full, ctx.RunDir))
        {
            return $"'{shown}' reaches the project's skills through a link — write the skill's own files.";
        }
        return null;
    }

    // ── the project's skills (.claude/skills) ──────────────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="full"/> is a file or folder below a <c>.claude/skills</c> folder inside
    /// <paramref name="runDir"/> (the run's checkout may sit in the run folder itself or in a subfolder of it).</summary>
    internal static bool IsProjectSkill(string full, string runDir) => SkillsAt(RelativeSegments(full, runDir)) >= 0;

    // The index of the "skills" segment of the first ".claude/skills" with something below it; -1 when there is none.
    private static int SkillsAt(string[] segments)
    {
        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i].Equals(ClaudeFolder, StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals(SkillsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }
        return -1;
    }

    // The path's folders and name below the run folder; empty when it is not inside it.
    private static string[] RelativeSegments(string full, string runDir) =>
        IsInside(full, runDir)
            ? Path.GetRelativePath(Path.GetFullPath(FromGitBash(runDir)), Path.GetFullPath(FromGitBash(full)))
                .Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>Whether a symbolic link or junction lies on the way from <paramref name="root"/> (exclusive) to
    /// <paramref name="full"/> (inclusive) — a skill file reached through one could really be a hook or a setting.
    /// Unreadable counts as a link.</summary>
    internal static bool ThroughLink(string full, string root)
    {
        var r = Path.GetFullPath(FromGitBash(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(FromGitBash(full)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (p.Length > r.Length && IsInside(p, r))
        {
            try
            {
                if (new FileInfo(p).LinkTarget is not null || new DirectoryInfo(p).LinkTarget is not null) { return true; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                return true;
            }
            if (Path.GetDirectoryName(p) is not { } parent) { break; }
            p = parent;
        }
        return false;
    }

    /// <summary>Whether the hook input is a PermissionRequest (a prompt the CLI would show a person).</summary>
    internal static bool IsPermissionRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("hook_event_name", out var e) && e.ValueKind == JsonValueKind.String
                && e.GetString() == PermissionRequestEvent;
        }
        catch (JsonException ex)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>Whether to answer a PermissionRequest with "allow": a Write / Edit / MultiEdit of a file below the run
    /// folder's <c>.claude/skills/</c> — no hidden file or folder below it, nothing reached through a link, and nothing the
    /// write rules refuse. Everything else gets no answer (denied in <c>claude -p</c>).</summary>
    internal static bool ApprovesSkillWrite(string json, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { return false; }
        var tool = root.TryGetProperty("tool_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (tool is not ("Write" or "Edit" or "MultiEdit")) { return false; }
        if (!root.TryGetProperty("tool_input", out var toolInput) || toolInput.ValueKind != JsonValueKind.Object
            || !toolInput.TryGetProperty("file_path", out var fp) || fp.ValueKind != JsonValueKind.String
            || fp.GetString() is not { Length: > 0 } path)
        {
            return false;
        }
        var cwd = root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } dir ? dir : ctx.RunDir;
        if (Resolve(path, cwd, ctx.TempDir) is not { } full) { return false; }
        var segments = RelativeSegments(full, ctx.RunDir);
        var skills = SkillsAt(segments);
        if (skills < 0 || segments.Skip(skills + 1).Any(s => s.StartsWith('.'))) { return false; }
        return WriteProblem(full, path, ctx) is null && !ThroughLink(full, ctx.RunDir)
            && CheckSkillGrant(tool, toolInput, path, cwd, ctx) is null; // a skill may not pre-approve tools
    }

    /// <summary>The PermissionRequest answer that approves the call.</summary>
    internal static string SkillApprovalJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("hookSpecificOutput");
            w.WriteString("hookEventName", PermissionRequestEvent);
            w.WriteStartObject("decision");
            w.WriteString("behavior", "allow");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ── shell commands ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Why <paramref name="command"/> (as the Bash or PowerShell tool would run it from <paramref name="cwd"/>)
    /// must not run, or null.</summary>
    internal static string? CheckCommand(string command, string cwd, GuardContext ctx, bool powerShell) =>
        CheckCommand(command, cwd, ctx, powerShell, 0);

    private static string? CheckCommand(string command, string? cwd, GuardContext ctx, bool powerShell, int depth)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(ctx);
        if (depth > MaxDepth) { return "the command nests other commands too deeply to be checked."; }
        ctx.CommandMentionsGrant |= MentionsGrant(command);
        if (powerShell && DotNetFileApi().IsMatch(command))
        {
            return "direct .NET file calls ([System.IO.File] / [IO.Directory] …) can't be checked — use the file tools or New-Item / Set-Content.";
        }
        if (powerShell && PowerShellNetworkCode(command) is { } network) { return network; }
        var state = new ShellState(cwd);
        foreach (var simple in ShellWords.Commands(command, powerShell))
        {
            if (CheckSimple(simple, state, ctx, powerShell, depth) is { } reason) { return reason; }
        }
        return null;
    }

    // The shell's folder as the command goes (null = computed, unknown) and the one before it (`cd -`).
    private sealed class ShellState(string? cwd)
    {
        public string? Here { get; set; } = cwd;
        public string? Previous { get; set; }
        public bool PreviousKnown { get; set; }
    }

    private static string? CheckSimple(ShellWords.Simple simple, ShellState state, GuardContext ctx, bool powerShell, int depth)
    {
        var cwd = state.Here;
        if (DevSocketProblem(simple) is { } socket) { return socket; }

        // What runs first: the command substitutions, in a subshell from the same folder.
        foreach (var sub in simple.Substitutions)
        {
            if (cwd is null) { return "it runs after a 'cd' to a computed folder, so its paths can't be checked."; }
            if (CheckCommand(sub, cwd, ctx, powerShell, depth + 1) is { } inner) { return inner; }
        }

        foreach (var target in simple.Redirects)
        {
            if (target.Text.Length == 0 || NullTargets.Contains(target.Text)) { continue; }
            if (Expand(target, cwd, ctx) is not { } text) { return $"the redirection target '{target.Text}' is computed, so it can't be checked."; }
            if (cwd is null || Resolve(text, cwd, ctx.TempDir) is not { } full) { return $"the redirection to '{target.Text}' can't be resolved."; }
            if (WriteProblem(full, target.Text, ctx) is { } problem) { return "the redirection is refused: " + problem; }
            if (ShellSkillWrite(full, target.Text, ctx) is { } skill) { return "the redirection is refused: " + skill; }
            ctx.Written.Add(full);
        }

        var words = simple.Words;
        var start = SkipPrefixes(words, powerShell, out var assignments);
        foreach (var a in assignments.Where(a => a.Text.StartsWith("NODE_OPTIONS=", StringComparison.Ordinal)))
        {
            if (CheckNodeOptions(a, cwd, ctx) is { } reason) { return reason; }
        }
        // CARGO_TARGET_DIR=<dir>: where cargo writes its build output — inside the run folder, like --target-dir.
        foreach (var dir in assignments.Where(a => a.Text.StartsWith("CARGO_TARGET_DIR=", StringComparison.OrdinalIgnoreCase))
            .Select(a => a with { Text = a.Text["CARGO_TARGET_DIR=".Length..] }))
        {
            if (cwd is null) { return "CARGO_TARGET_DIR is set after a 'cd' to a computed folder."; }
            if (CheckPaths("CARGO_TARGET_DIR", [dir], cwd, ctx, record: false) is { } reason) { return reason; }
        }
        if (start >= words.Count) { return null; }
        var programWord = words[start];
        var program = ProgramName(programWord.Text);
        var args = words.Skip(start + 1).ToList();

        // Network egress is refused whatever approved the call (an allow rule's prefix, a skill's grant).
        if (CheckEgress(program, args, assignments, cwd, ctx, powerShell) is { } egress) { return egress; }

        if (CdCommands.Contains(program)) { return CheckCd(program, args, state, ctx); }

        // A program named by its path inside the run folder (`./helper.sh`, `node_modules/.bin/x`) is code the run may
        // have written: the same rule as a script given to an interpreter.
        var runsAPath = !programWord.Dynamic && !(powerShell && programWord.Quoted)
            && programWord.Text.IndexOfAny(['/', '\\']) >= 0 && cwd is not null
            && Resolve(programWord.Text, cwd, ctx.TempDir) is { } programPath && ctx.IsWritable(programPath);

        var guarded = runsAPath || IsGuarded(program, powerShell);
        if (!guarded) { return cwd is not null && !ctx.IsWritable(cwd) ? "it runs from a folder outside the run folder." : null; }
        if (cwd is null) { return "it runs after a 'cd' to a computed folder, so its paths can't be checked."; }
        if (!ctx.IsWritable(cwd)) { return "it runs from a folder outside the run folder."; }
        if (runsAPath && CheckScript(programWord.Text, programWord, cwd, ctx) is { } own) { return own; }
        if (runsAPath && Resolve(programWord.Text, cwd, ctx.TempDir) is { } helper && IsCopyHelper(helper, ctx))
        {
            return CheckCopyIn(args, cwd, ctx);
        }

        if (WriteCommands.Contains(program))
        {
            var operands = Operands(program, args);
            return CheckPaths(program, operands, cwd, ctx, record: ContentCommands.Contains(program))
                ?? CopyIntoSkill(program, operands, cwd, ctx);
        }
        return program switch
        {
            "git" => CheckGit(args, cwd, ctx),
            "gh" => CheckGh(args, cwd, ctx),
            "xargs" => CheckXargs(args, cwd, ctx, powerShell, depth),
            "find" => CheckFind(args, cwd, ctx, powerShell, depth),
            "dotnet" => CheckDotnet(args, simple, cwd, ctx),
            "cargo" => CheckCargo(args, cwd, ctx),
            "msbuild" => CheckMsBuildProperties(args, cwd, ctx),
            "curl" or "wget" => CheckDownload(program, args, cwd, ctx),
            "sed" => CheckSed(args, cwd, ctx),
            "cmd" => CheckCmd(args, simple, cwd, ctx, depth),
            "." or "source" => args.Count > 0 ? CheckScript(program, args[0], cwd, ctx) : null,
            "iex" or "invoke-expression" => CheckIex(args, cwd, ctx, depth),
            "start-process" or "saps" or "start" when powerShell => CheckStartProcess(args, cwd, ctx, powerShell, depth),
            _ when PackageManagers.Contains(program) => CheckPackageManager(program, args, simple, cwd, ctx, powerShell, depth),
            _ when Interpreters.Contains(program) => CheckInterpreter(program, args, simple, cwd, ctx),
            _ when Shells.Contains(program) => CheckShell(program, args, simple, cwd, ctx, depth),
            _ when PowerShells.Contains(program) => CheckPwsh(args, simple, cwd, ctx, depth),
            _ => null,
        };
    }

    private static bool IsGuarded(string program, bool powerShell) =>
        WriteCommands.Contains(program) || Interpreters.Contains(program) || Shells.Contains(program)
        || PowerShells.Contains(program) || PackageManagers.Contains(program) || GuardedTools.Contains(program)
        || (powerShell && program is "start");

    private static readonly HashSet<string> GuardedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "gh", "xargs", "find", "dotnet", "cargo", "msbuild", "curl", "wget", "cmd", ".", "source", "iex", "invoke-expression",
        "start-process", "saps", "sed",
    };

    // Leading assignments (FOO=1), wrappers (env, sudo, nohup, timeout 10…) and shell keywords (then, do, {).
    private static int SkipPrefixes(IReadOnlyList<ShellWords.Word> words, bool powerShell, out List<ShellWords.Word> assignments)
    {
        assignments = [];
        var i = 0;
        while (i < words.Count)
        {
            var text = words[i].Text;
            if (IsAssignment(text, powerShell)) { assignments.Add(words[i]); i++; continue; }
            var name = ProgramName(text);
            if (!Wrappers.Contains(name) || (powerShell && name is "if" or "while" or "until" or "else" or "elif")) { break; }
            i++;
            while (i < words.Count && words[i].Text.StartsWith('-') && words[i].Text.Length > 1)
            {
                var opt = words[i].Text;
                i++;
                if (WrapperValueOptions.Contains(opt)) { i++; }
            }
            if (name == "timeout" && i < words.Count) { i++; } // the duration
        }
        return i;
    }

    private static string? CheckCd(string program, List<ShellWords.Word> args, ShellState state, GuardContext ctx)
    {
        var cwd = state.Here;
        var target = args.FirstOrDefault(a => !a.Text.StartsWith('-') || a.Text == "-");
        if (target is null) { return "'" + program + "' without a folder leaves the run folder."; }
        if (target.Text == "-")
        {
            // The folder before: known when this command changed it; otherwise the shell's own — unknown here.
            var back = state.PreviousKnown ? state.Previous : null;
            if (back is not null && !ctx.IsWritable(back)) { return $"'{program} -' leaves the run folder."; }
            (state.Previous, state.Here, state.PreviousKnown) = (cwd, back, true);
            return null;
        }
        if (Expand(target, cwd, ctx) is not { } text)
        {
            (state.Previous, state.Here, state.PreviousKnown) = (cwd, null, true); // computed: later writes can't be checked
            return null;
        }
        if (cwd is null || Resolve(text, cwd, ctx.TempDir) is not { } dir || !ctx.IsWritable(dir))
        {
            return $"'{program} {target.Text}' leaves the run folder.";
        }
        (state.Previous, state.Here, state.PreviousKnown) = (cwd, dir, true);
        return null;
    }

    private static string? CheckPaths(string what, IEnumerable<ShellWords.Word> paths, string cwd, GuardContext ctx, bool record)
    {
        foreach (var p in paths)
        {
            if (Expand(p, cwd, ctx) is not { } text) { return $"'{what}' gets a computed path ('{p.Text}'), so it can't be checked."; }
            if (Resolve(text, cwd, ctx.TempDir) is not { } full) { return $"'{what} {p.Text}': the path can't be resolved."; }
            if (WriteProblem(full, p.Text, ctx) is { } problem) { return $"'{what} {p.Text}' is refused: " + problem; }
            if (record && ShellSkillWrite(full, p.Text, ctx) is { } skill) { return $"'{what} {p.Text}' is refused: " + skill; }
            if (record) { ctx.Written.Add(full); }
        }
        return null;
    }

    // Arguments that aren't options; "--opt=value" contributes its value, an attached short value too (`-C../x`);
    // after "--" everything is an operand. PowerShell parameters whose value isn't a path are skipped with it.
    private static List<ShellWords.Word> Operands(string program, List<ShellWords.Word> args)
    {
        var result = new List<ShellWords.Word>();
        var endOfOptions = false;
        var attached = AttachedValueLetters.GetValueOrDefault(program);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!endOfOptions && a.Text == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !a.Text.StartsWith('-') || a.Text == "-") { result.Add(a); continue; }
            if (PsValueOptions.Contains(a.Text)) { i++; continue; }
            var eq = a.Text.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && eq < a.Text.Length - 1) { result.Add(a with { Text = a.Text[(eq + 1)..] }); continue; }
            if (attached is not null && AttachedValue(a.Text, attached) is { Length: > 0 } value) { result.Add(a with { Text = value }); }
        }
        return result;
    }

    // `-xzC../x` → "../x" when one of the letters takes a value and something follows it.
    private static string? AttachedValue(string option, string letters)
    {
        if (option.StartsWith("--", StringComparison.Ordinal) || option.Length < 3) { return null; }
        for (var k = 1; k < option.Length; k++)
        {
            if (letters.Contains(option[k], StringComparison.Ordinal)) { return k + 1 < option.Length ? option[(k + 1)..] : null; }
        }
        return null;
    }

    // Positional arguments, skipping the values of the options that take one.
    private static List<ShellWords.Word> Positionals(IEnumerable<ShellWords.Word> args, HashSet<string> valueOptions)
    {
        var result = new List<ShellWords.Word>();
        var list = args.ToList();
        var endOfOptions = false;
        for (var i = 0; i < list.Count; i++)
        {
            var text = list[i].Text;
            if (!endOfOptions && text == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !text.StartsWith('-') || text == "-") { result.Add(list[i]); continue; }
            if (!text.Contains('=', StringComparison.Ordinal) && valueOptions.Contains(text)) { i++; }
        }
        return result;
    }

    // The values of the named options: "--name value", "--name=value", "-o:value" / "--output:value" (dotnet), and a
    // short letter's attached value in a group ("-so../x" when 'o' is one of <paramref name="shortLetters"/>).
    private static IEnumerable<ShellWords.Word> OptionValues(List<ShellWords.Word> args, HashSet<string> names, string shortLetters = "")
    {
        for (var i = 0; i < args.Count; i++)
        {
            var text = args[i].Text;
            if (text == "--") { yield break; }
            if (!text.StartsWith('-')) { continue; }
            var sep = text.IndexOfAny(['=', ':']);
            if (sep > 0 && names.Contains(text[..sep]))
            {
                yield return args[i] with { Text = text[(sep + 1)..] };
            }
            else if (names.Contains(text))
            {
                if (i + 1 < args.Count) { yield return args[++i]; }
            }
            else if (shortLetters.Length > 0 && !text.StartsWith("--", StringComparison.Ordinal) && text.Length > 2)
            {
                for (var k = 1; k < text.Length; k++)
                {
                    if (!shortLetters.Contains(text[k], StringComparison.Ordinal)) { continue; }
                    if (k + 1 < text.Length) { yield return args[i] with { Text = text[(k + 1)..] }; }
                    else if (i + 1 < args.Count) { yield return args[++i]; }
                    break;
                }
            }
        }
    }

    // ── git / gh ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> GitGlobalWithValue = new(StringComparer.Ordinal) { "-c", "--namespace", "--config-env", "--exec-path" };
    private static readonly HashSet<string> GitDirOptions = new(StringComparer.Ordinal) { "-C", "--git-dir", "--work-tree" };
    private static readonly HashSet<string> CloneValueOptions = new(StringComparer.Ordinal)
    {
        "-b", "--branch", "-o", "--origin", "-u", "--upload-pack", "--reference", "--reference-if-able", "--depth",
        "--shallow-since", "--shallow-exclude", "-c", "--config", "--separate-git-dir", "--template", "-j", "--jobs",
        "--filter", "--bundle-uri", "--server-option", "--ref-format",
    };
    private static readonly HashSet<string> WorktreeAddValueOptions = new(StringComparer.Ordinal) { "-b", "-B", "--reason" };
    private static readonly HashSet<string> GitOutputOptions = new(StringComparer.Ordinal) { "-o", "--output", "--output-directory", "--separate-git-dir", "--template" };

    private static string? CheckGit(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        var i = 0;
        var gitCwd = cwd;
        while (i < args.Count && args[i].Text.StartsWith('-'))
        {
            var text = args[i].Text;
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            var name = eq > 0 ? text[..eq] : text;
            if (GitDirOptions.Contains(name))
            {
                ShellWords.Word? value = eq > 0 ? args[i] with { Text = text[(eq + 1)..] } : i + 1 < args.Count ? args[++i] : null;
                if (value is null) { return $"'git {text}' without a folder."; }
                if (CheckPaths("git " + name, [value], gitCwd, ctx, record: false) is { } reason) { return reason; }
                if (name == "-C" && Expand(value, gitCwd, ctx) is { } dir) { gitCwd = Resolve(dir, gitCwd, ctx.TempDir) ?? gitCwd; }
            }
            else if (eq < 0 && GitGlobalWithValue.Contains(name)) { i++; }
            i++;
        }
        if (i >= args.Count) { return null; }
        var sub = args[i].Text;
        var rest = args.Skip(i + 1).ToList();
        if (CheckPaths("git " + sub, OptionValues(rest, GitOutputOptions), gitCwd, ctx, record: false) is { } output) { return output; }
        switch (sub)
        {
            case "worktree" when rest.Count > 0 && rest[0].Text is "add" or "move":
                var paths = Positionals(rest.Skip(1), WorktreeAddValueOptions);
                var target = rest[0].Text == "add" ? paths.FirstOrDefault() : paths.ElementAtOrDefault(1);
                return target is null ? null : CheckPaths("git worktree " + rest[0].Text, [target], gitCwd, ctx, record: true);
            case "clone":
                var dest = Positionals(rest, CloneValueOptions).ElementAtOrDefault(1);
                return dest is null ? null : CheckPaths("git clone", [dest], gitCwd, ctx, record: false);
            case "init":
                var initDir = Positionals(rest, CloneValueOptions).FirstOrDefault();
                return initDir is null ? null : CheckPaths("git init", [initDir], gitCwd, ctx, record: false);
            case "bundle" when rest.Count > 1 && rest[0].Text == "create":
                return CheckPaths("git bundle create", [rest[1]], gitCwd, ctx, record: false);
            default:
                return null;
        }
    }

    private static readonly HashSet<string> NoValueOptions = new(StringComparer.Ordinal);

    private static string? CheckGh(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        var positional = Positionals(args, NoValueOptions);
        if (positional.Count >= 4 && positional[0].Text == "repo" && positional[1].Text == "clone")
        {
            return CheckPaths("gh repo clone", [positional[3]], cwd, ctx, record: false);
        }
        return null;
    }

    // ── nested commands ──────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> XargsValueOptions = new(StringComparer.Ordinal)
    {
        "-I", "-n", "-L", "-l", "-P", "-d", "-E", "-e", "-s", "-a", "--arg-file", "--delimiter", "--max-args", "--max-procs",
        "--max-lines", "--replace", "--eof", "--max-chars",
    };

    private static string? CheckXargs(List<ShellWords.Word> args, string cwd, GuardContext ctx, bool powerShell, int depth)
    {
        var i = 0;
        while (i < args.Count && args[i].Text.StartsWith('-'))
        {
            if (XargsValueOptions.Contains(args[i].Text)) { i++; }
            i++;
        }
        if (i >= args.Count) { return null; }
        var program = ProgramName(args[i].Text);
        if (IsNetworkTool(program)) { return $"'xargs {args[i].Text}' takes its addresses from its input, which can't be checked." + NetworkNote; }
        if (WriteCommands.Contains(program) || Interpreters.Contains(program) || Shells.Contains(program))
        {
            return $"'xargs {args[i].Text}' takes its paths from its input, which can't be checked.";
        }
        return CheckSynthesized(args.Skip(i), cwd, ctx, powerShell, depth);
    }

    private static readonly HashSet<string> FindOutputOptions = new(StringComparer.Ordinal) { "-fprint", "-fprint0", "-fls", "-fprintf" };

    private static string? CheckFind(List<ShellWords.Word> args, string cwd, GuardContext ctx, bool powerShell, int depth)
    {
        if (CheckPaths("find", OptionValues(args, FindOutputOptions), cwd, ctx, record: true) is { } output) { return output; }
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Text is not ("-exec" or "-execdir" or "-ok" or "-okdir")) { continue; }
            var end = args.FindIndex(i + 1, a => a.Text is ";" or "+");
            var command = args.Skip(i + 1).Take((end < 0 ? args.Count : end) - i - 1).ToList();
            if (CheckSynthesized(command, cwd, ctx, powerShell, depth) is { } reason) { return reason; }
            i = end < 0 ? args.Count : end;
        }
        return null;
    }

    // Words that another program runs as a command of their own.
    private static string? CheckSynthesized(IEnumerable<ShellWords.Word> words, string cwd, GuardContext ctx, bool powerShell, int depth)
    {
        var list = words.ToList();
        if (list.Count == 0) { return null; }
        if (depth > MaxDepth) { return "the command nests other commands too deeply to be checked."; }
        var simple = new ShellWords.Simple(list, [], [], [], PipedIn: false, StdinText: false);
        return CheckSimple(simple, new ShellState(cwd), ctx, powerShell, depth + 1);
    }

    private static string? CheckCmd(List<ShellWords.Word> args, ShellWords.Simple simple, string cwd, GuardContext ctx, int depth)
    {
        var at = args.FindIndex(a => a.Text.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Text.Equals("/k", StringComparison.OrdinalIgnoreCase));
        if (at < 0) { return simple.PipedIn || simple.StdinText || simple.Inputs.Count > 0 ? "'cmd' reads commands from its input, which can't be checked." : null; }
        var rest = args.Skip(at + 1).ToList();
        if (rest.Exists(w => w.Dynamic)) { return "'cmd /c' runs a computed command."; }
        // cmd reads backslashes literally: tokenise the rest like PowerShell does.
        return CheckCommand(string.Join(' ', rest.Select(w => Quote(w.Text))), cwd, ctx, powerShell: true, depth + 1);
    }

    private static string? CheckIex(List<ShellWords.Word> args, string cwd, GuardContext ctx, int depth)
    {
        var rest = args.Where(a => !a.Text.Equals("-command", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0) { return "Invoke-Expression runs code from its input, which can't be checked."; }
        if (rest.Exists(w => w.Dynamic)) { return "Invoke-Expression runs a computed command."; }
        return CheckCommand(string.Join(' ', rest.Select(w => w.Text)), cwd, ctx, powerShell: true, depth + 1);
    }

    private static string? CheckStartProcess(List<ShellWords.Word> args, string cwd, GuardContext ctx, bool powerShell, int depth)
    {
        ShellWords.Word? file = null;
        var argList = new List<ShellWords.Word>();
        var workDir = cwd;
        var positional = 0;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].Text.ToLowerInvariant();
            ShellWords.Word? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (name)
            {
                case "-filepath" or "-path":
                    file = Next();
                    break;
                case "-argumentlist" or "-args":
                    if (Next() is { } list) { argList.AddRange(SplitArgumentList(list)); }
                    break;
                case "-workingdirectory":
                    if (Next() is not { } wd || Expand(wd, cwd, ctx) is not { } wdText || Resolve(wdText, cwd, ctx.TempDir) is not { } wdFull || !ctx.IsWritable(wdFull))
                    {
                        return "Start-Process runs from a folder outside the run folder.";
                    }
                    workDir = wdFull;
                    break;
                case "-redirectstandardoutput" or "-redirectstandarderror" or "-redirectstandardinput":
                    if (Next() is { } redirect && name != "-redirectstandardinput"
                        && CheckPaths("Start-Process", [redirect], cwd, ctx, record: true) is { } reason) { return reason; }
                    break;
                default:
                    if (name.StartsWith('-')) { break; }
                    if (positional++ == 0) { file = args[i]; }
                    else { argList.AddRange(SplitArgumentList(args[i])); }
                    break;
            }
        }
        return file is null ? null : CheckSynthesized([file, .. argList], workDir, ctx, powerShell, depth);
    }

    // PowerShell's "-ArgumentList 'a','b c'" arrives as one word "a,b c": split on commas and spaces.
    private static IEnumerable<ShellWords.Word> SplitArgumentList(ShellWords.Word list) =>
        list.Text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => list with { Text = part });

    private static string Quote(string word) =>
        word.Length > 0 && word.IndexOfAny([' ', '\t', '&', '|', ';', '<', '>', '(', ')', '{', '}']) < 0
            ? word
            : "'" + word.Replace("'", "''", StringComparison.Ordinal) + "'";

    // ── package managers / build tools / downloads ────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> PmValueOptions = new(StringComparer.Ordinal)
    {
        "--prefix", "-C", "--dir", "--cwd", "--filter", "-w", "--workspace", "--tag", "--registry", "--cache", "--userconfig",
        "-p", "--package", "-c", "--call",
    };

    private static readonly HashSet<string> PmPathOptions = new(StringComparer.Ordinal)
    {
        "--prefix", "-C", "--dir", "--cwd", "--modules-folder", "--global-folder", "--cache-folder", "--virtual-store-dir",
    };

    private static readonly HashSet<string> CallOptions = new(StringComparer.Ordinal) { "-c", "--call" };

    private static string? CheckPackageManager(string program, List<ShellWords.Word> args, ShellWords.Simple simple, string cwd,
        GuardContext ctx, bool powerShell, int depth)
    {
        if (args.Exists(a => a.Text is "-g" or "--global" or "--location=global"))
        {
            return $"'{program} … {(args.Exists(a => a.Text == "-g") ? "-g" : "--global")}' installs outside the run folder.";
        }
        if (CheckPaths(program, OptionValues(args, PmPathOptions), cwd, ctx, record: false) is { } path) { return path; }
        var positional = Positionals(args, PmValueOptions);
        var first = positional.FirstOrDefault()?.Text;
        if (program == "yarn" && first == "global") { return "'yarn global' installs outside the run folder."; }
        if (first == "pkg" && positional.Skip(1).FirstOrDefault()?.Text == "set" && positional.Skip(2).Any(p => p.Text.StartsWith("scripts.", StringComparison.Ordinal)))
        {
            return $"'{program} pkg set scripts.…' adds a script the run would then run — edit package.json instead (it ships in the pull request).";
        }
        if (program == "yarn" && first == "node")
        {
            return CheckInterpreter("node", args.SkipWhile(a => a.Text != "node").Skip(1).ToList(), simple, cwd, ctx);
        }
        var execs = program is "npx" or "pnpx" or "bunx"
            || (program == "npm" && first is "exec" or "x")
            || (program is "pnpm" or "yarn" && first is "dlx" or "exec");
        if (!execs) { return null; }
        // `npm exec -c '<command>'`: a shell command.
        foreach (var call in OptionValues(args, CallOptions))
        {
            if (call.Dynamic) { return $"'{program}' runs a computed command."; }
            if (CheckCommand(call.Text, cwd, ctx, powerShell: false, depth + 1) is { } reason) { return reason; }
        }
        // Otherwise the words after the subcommand (and its options) are a command of their own.
        var rest = program is "npx" or "pnpx" or "bunx" ? args : args.SkipWhile(a => a.Text != first).Skip(1).ToList();
        var command = new List<ShellWords.Word>();
        var i = 0;
        while (i < rest.Count && rest[i].Text.StartsWith('-') && rest[i].Text != "--")
        {
            if (PmValueOptions.Contains(rest[i].Text)) { i++; }
            i++;
        }
        if (i < rest.Count && rest[i].Text == "--") { i++; }
        command.AddRange(rest.Skip(i));
        return CheckSynthesized(command, cwd, ctx, powerShell, depth);
    }

    private static readonly HashSet<string> CargoOutputOptions = new(StringComparer.Ordinal)
    {
        "--target-dir", "--artifact-dir", "--out-dir", "--root", "--lockfile-path",
    };

    // cargo: `install` puts a binary in ~/.cargo/bin (outside the run folder); output folders must stay inside; a
    // single-file package (`cargo run x.rs`, `-Zscript`) is code the run may have written.
    private static string? CheckCargo(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        if (CheckPaths("cargo", OptionValues(args, CargoOutputOptions), cwd, ctx, record: false) is { } output) { return output; }
        var positional = Positionals(args.Where(a => !a.Text.StartsWith('+')), CargoValueOptions);
        if (positional.FirstOrDefault()?.Text is "install" or "uninstall")
        {
            return $"'cargo {positional[0].Text}' installs outside the run folder (~/.cargo) — add the crate as a dependency instead.";
        }
        var script = positional.FirstOrDefault(p => p.Text.EndsWith(".rs", StringComparison.OrdinalIgnoreCase));
        return script is null ? null : CheckScript("cargo", script, cwd, ctx);
    }

    private static readonly HashSet<string> CargoValueOptions = new(StringComparer.Ordinal)
    {
        "-p", "--package", "--bin", "--example", "--test", "--bench", "-F", "--features", "-j", "--jobs", "--target",
        "--target-dir", "--artifact-dir", "--manifest-path", "--profile", "--message-format", "--color", "-Z", "--config",
        "--root", "--index", "--registry", "--git", "--branch", "--tag", "--rev", "--path", "--lockfile-path", "--exclude",
    };

    private static readonly HashSet<string> DotnetOutputOptions = new(StringComparer.Ordinal)
    {
        "-o", "--output", "--results-directory", "--diag", "--tool-path",
    };

    private static readonly HashSet<string> DotnetRunValueOptions = new(StringComparer.Ordinal)
    {
        "--project", "-p", "--launch-profile", "-lp", "-c", "--configuration", "-f", "--framework", "-r", "--runtime",
        "-a", "--arch", "--os", "-v", "--verbosity", "-e", "--environment", "--file",
    };

    private static string? CheckDotnet(List<ShellWords.Word> args, ShellWords.Simple simple, string cwd, GuardContext ctx)
    {
        if (CheckPaths("dotnet", OptionValues(args, DotnetOutputOptions), cwd, ctx, record: false) is { } output) { return output; }
        if (CheckMsBuildProperties(args, cwd, ctx) is { } property) { return property; }
        var sub = args.FirstOrDefault(a => !a.Text.StartsWith('-'))?.Text;
        var afterSub = sub is null ? [] : args.SkipWhile(a => a.Text != sub).Skip(1).ToList();
        switch (sub)
        {
            case "tool" when afterSub.Exists(a => a.Text is "-g" or "--global"):
                return "'dotnet tool … --global' installs outside the run folder.";
            case "run":
                // A file-based app (.NET 10 `dotnet run app.cs` / `--file`) is code the run may have written.
                var file = OptionValues(afterSub, ["--file"]).FirstOrDefault()
                    ?? Positionals(afterSub.TakeWhile(a => a.Text != "--"), DotnetRunValueOptions)
                        .FirstOrDefault(p => p.Text.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                return file is null ? null : CheckScript("dotnet run", file, cwd, ctx);
            case "fsi":
                var fsx = Positionals(afterSub, NoValueOptions).FirstOrDefault();
                return fsx is not null ? CheckScript("dotnet fsi", fsx, cwd, ctx) : StdinCode("dotnet fsi", simple, cwd, ctx);
            case "exec":
                var dll = Positionals(afterSub, NoValueOptions).FirstOrDefault();
                return dll is null ? null : CheckScript("dotnet exec", dll, cwd, ctx);
            default:
                return null;
        }
    }

    // `-p:OutputPath=../x`, `/p:A=B;C=D`, `--property:…`, `-property:…`, `-p A=B`: a property naming a folder or file
    // must stay inside.
    private static string? CheckMsBuildProperties(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var text = args[i].Text;
            string? assignments = null;
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? text[..colon].ToLowerInvariant() : text.ToLowerInvariant();
            if (name is "-p" or "/p" or "-property" or "--property" or "/property")
            {
                assignments = colon > 0 ? text[(colon + 1)..] : i + 1 < args.Count ? args[++i].Text : null;
            }
            if (assignments is null) { continue; }
            foreach (var pair in assignments.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || !PathProperty().IsMatch(pair[..eq])) { continue; }
                var value = new ShellWords.Word(pair[(eq + 1)..], args[i].Dynamic);
                if (CheckPaths("dotnet -p:" + pair[..eq], [value], cwd, ctx, record: false) is { } reason) { return reason; }
            }
        }
        return null;
    }

    private static readonly HashSet<string> CurlOutputs = new(StringComparer.Ordinal)
    {
        "-o", "--output", "--output-dir", "-D", "--dump-header", "-c", "--cookie-jar", "--trace", "--trace-ascii", "--stderr",
        "--libcurl", "--etag-save", "--hsts", "--alt-svc",
    };

    private static readonly HashSet<string> WgetOutputs = new(StringComparer.Ordinal)
    {
        "-O", "--output-document", "-P", "--directory-prefix", "-o", "--output-file", "-a", "--append-output",
    };

    private static string? CheckDownload(string program, List<ShellWords.Word> args, string cwd, GuardContext ctx) =>
        program == "curl"
            ? CheckPaths("curl", OptionValues(args, CurlOutputs, "oDc"), cwd, ctx, record: true)
            : CheckPaths("wget", OptionValues(args, WgetOutputs, "OPoa"), cwd, ctx, record: true);

    // `sed -i` / `--in-place` rewrites its file operands (the first operand is the script unless -e / -f gave one). GNU
    // takes the backup suffix attached (`-i.bak`), BSD as the next word (`-i ''`).
    private static string? CheckSed(List<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        var inPlace = false;
        var scriptGiven = false;
        var endOfOptions = false;
        var operands = new List<ShellWords.Word>();
        for (var i = 0; i < args.Count; i++)
        {
            var text = args[i].Text;
            if (!endOfOptions && text == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !text.StartsWith('-') || text == "-") { operands.Add(args[i]); continue; }
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = text.IndexOf('=', StringComparison.Ordinal);
                var name = eq > 0 ? text[..eq] : text;
                if (name == "--in-place") { inPlace = true; }
                else if (name is "--expression" or "--file") { scriptGiven = true; if (eq < 0) { i++; } }
                else if (name == "--line-length" && eq < 0) { i++; }
                continue;
            }
            for (var k = 1; k < text.Length; k++)
            {
                var letter = text[k];
                if (letter == 'i')
                {
                    inPlace = true;
                    if (text == "-i" && i + 1 < args.Count && args[i + 1].Text.Length == 0) { i++; } // BSD: -i ''
                    break; // the rest is the backup suffix
                }
                if (letter is 'e' or 'f') { scriptGiven = true; if (k == text.Length - 1) { i++; } break; }
                if (letter == 'l') { if (k == text.Length - 1) { i++; } break; }
            }
        }
        if (!inPlace) { return null; }
        if (!scriptGiven && operands.Count > 0) { operands.RemoveAt(0); }
        return CheckPaths("sed -i", operands, cwd, ctx, record: true);
    }

    // ── code the run could have written ─────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> InlineCode = new(StringComparer.Ordinal) { "-e", "--eval", "-p", "--print", "-c" };
    private static readonly HashSet<string> ScriptOptions = new(StringComparer.Ordinal) { "-r", "--require", "--import", "--loader", "--experimental-loader" };
    private static readonly HashSet<string> InterpreterValueOptions = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "-C", "--conditions", "--input-type", "--env-file",
        "--title", "-m", "-W", "-X",
    };
    private static readonly HashSet<string> InfoFlags = new(StringComparer.Ordinal) { "--version", "-v", "-V", "--help", "-h" };

    private static string? CheckInterpreter(string program, List<ShellWords.Word> args, ShellWords.Simple simple, string cwd, GuardContext ctx)
    {
        if (args.Exists(a => InlineCode.Contains(a.Text) || a.Text.StartsWith("--eval=", StringComparison.Ordinal) || a.Text.StartsWith("--print=", StringComparison.Ordinal)))
        {
            return $"'{program}' with inline code can write anywhere — run one of the repository's own scripts instead.";
        }
        if (args.Exists(a => a.Text == "-m")) { return null; } // python -m <module>: installed code, not a file of the run
        if (args.Count > 0 && args.TrueForAll(a => InfoFlags.Contains(a.Text))) { return null; }
        var scripts = OptionValues(args, ScriptOptions).Where(v => LooksLikePath(v.Text)).ToList();
        var positional = Positionals(args, InterpreterValueOptions);
        if (program is "deno" or "bun" && positional.Count > 0 && positional[0].Text is "run" or "test") { positional.RemoveAt(0); }
        var script = positional.FirstOrDefault();
        if (script is null || script.Text == "-")
        {
            if (StdinCode(program, simple, cwd, ctx) is { } stdin) { return stdin; }
        }
        else
        {
            scripts.Add(script);
        }
        foreach (var s in scripts)
        {
            if (CheckScript(program, s, cwd, ctx) is { } reason) { return reason; }
        }
        return null;
    }

    // An interpreter reading its program from stdin: a heredoc or a pipe is inline code; a file is checked like a script.
    private static string? StdinCode(string program, ShellWords.Simple simple, string cwd, GuardContext ctx)
    {
        if (simple.StdinText) { return $"'{program}' with inline code (a heredoc) can write anywhere — run one of the repository's own scripts instead."; }
        if (simple.PipedIn) { return $"'{program}' reading its code from a pipe can write anywhere — run one of the repository's own scripts instead."; }
        foreach (var input in simple.Inputs)
        {
            if (CheckScript(program, input, cwd, ctx) is { } reason) { return reason; }
        }
        return null;
    }

    private static string? CheckShell(string program, List<ShellWords.Word> args, ShellWords.Simple simple, string cwd, GuardContext ctx, int depth)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var text = args[i].Text;
            if (text == "--") { continue; }
            if (text.StartsWith("--", StringComparison.Ordinal)) { continue; } // --login, --norc…
            if (text.StartsWith('-') && text.Length > 1)
            {
                if (text is "-o" or "+o") { i++; continue; }
                if (!text.Contains('c', StringComparison.Ordinal)) { continue; }
                // -c / -lc / -ec: the next word (after a "--") is the command.
                var at = i + 1 < args.Count && args[i + 1].Text == "--" ? i + 2 : i + 1;
                if (at >= args.Count) { return null; }
                return args[at].Dynamic
                    ? $"'{program} {text}' runs a computed command."
                    : CheckCommand(args[at].Text, cwd, ctx, powerShell: false, depth + 1);
            }
            // `sh script.sh`; the runner's copy-in helper also gets its arguments checked.
            if (CheckScript(program, args[i], cwd, ctx) is { } script) { return script; }
            return Expand(args[i], cwd, ctx) is { } helperText && Resolve(helperText, cwd, ctx.TempDir) is { } helper && IsCopyHelper(helper, ctx)
                ? CheckCopyIn(args.Skip(i + 1).ToList(), cwd, ctx)
                : null;
        }
        return StdinCode(program, simple, cwd, ctx);
    }

    private static string? CheckPwsh(List<ShellWords.Word> args, ShellWords.Simple simple, string cwd, GuardContext ctx, int depth)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i].Text.ToLowerInvariant();
            if (!flag.StartsWith('-'))
            {
                return CheckScript("pwsh", args[i], cwd, ctx); // `pwsh script.ps1`
            }
            if (flag is "-c" or "-cmd" || (flag.Length >= 4 && "-command".StartsWith(flag, StringComparison.Ordinal)))
            {
                var rest = args.Skip(i + 1).ToList();
                if (rest.Exists(w => w.Dynamic)) { return "'pwsh -Command' runs a computed command."; }
                return CheckCommand(string.Join(' ', rest.Select(w => w.Text)), cwd, ctx, powerShell: true, depth + 1);
            }
            if (flag is "-e" or "-ec" || (flag.Length >= 3 && "-encodedcommand".StartsWith(flag, StringComparison.Ordinal)))
            {
                if (i + 1 >= args.Count) { return null; }
                return DecodeUtf16Base64(args[i + 1].Text) is { } decoded
                    ? CheckCommand(decoded, cwd, ctx, powerShell: true, depth + 1)
                    : "'pwsh -EncodedCommand' with a command that can't be decoded.";
            }
            if (flag is "-f" || (flag.Length >= 3 && "-file".StartsWith(flag, StringComparison.Ordinal)))
            {
                return i + 1 < args.Count ? CheckScript("pwsh -File", args[i + 1], cwd, ctx) : null;
            }
            if (flag is "-wd" || (flag.Length >= 3 && "-workingdirectory".StartsWith(flag, StringComparison.Ordinal)))
            {
                if (i + 1 >= args.Count) { return null; }
                var wd = args[++i];
                if (Expand(wd, cwd, ctx) is not { } wdText || Resolve(wdText, cwd, ctx.TempDir) is not { } full || !ctx.IsWritable(full))
                {
                    return $"'pwsh -WorkingDirectory {wd.Text}' leaves the run folder.";
                }
                cwd = full;
                continue;
            }
            if (flag is "-ex" or "-ep" or "-executionpolicy" or "-of" or "-outputformat" or "-if" or "-inputformat"
                or "-w" or "-windowstyle" or "-config" or "-configurationname" or "-settingsfile")
            {
                i++;
            }
        }
        return StdinCode("pwsh", simple, cwd, ctx);
    }

    private static string? DecodeUtf16Base64(string value)
    {
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(value)); }
        catch (FormatException ex)
        {
            _ = ex;
            return null;
        }
    }

    // NODE_OPTIONS='--require ./x.mjs': a preloaded script, the same rule as a script node runs.
    private static string? CheckNodeOptions(ShellWords.Word assignment, string? cwd, GuardContext ctx)
    {
        if (Expand(assignment, cwd, ctx) is not { } text) { return "NODE_OPTIONS is computed, so the code it preloads can't be checked."; }
        var value = text["NODE_OPTIONS=".Length..];
        var words = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => new ShellWords.Word(w, false)).ToList();
        foreach (var script in OptionValues(words, ScriptOptions))
        {
            if (cwd is null) { return "NODE_OPTIONS preloads a script from a computed folder."; }
            if (CheckScript("NODE_OPTIONS", script, cwd, ctx) is { } reason) { return reason; }
        }
        return null;
    }

    /// <summary>Whether <paramref name="script"/> — code <paramref name="program"/> is about to run — may run: a file the
    /// repository tracks (committed or changed: it ships in the pull request), or an ignored build output the run didn't
    /// write itself. A new untracked file, anything outside the run's folders, or an ignored file the run wrote is refused.</summary>
    private static string? CheckScript(string program, ShellWords.Word script, string cwd, GuardContext ctx)
    {
        if (Expand(script, cwd, ctx) is not { } text) { return $"'{program}' runs a computed script path ('{script.Text}')."; }
        if (Resolve(text, cwd, ctx.TempDir) is not { } full) { return $"'{program} {script.Text}': the path can't be resolved."; }
        if (!ctx.IsWritable(full)) { return $"'{program} {script.Text}' runs a script outside the run folder."; }
        if (IsCopyHelper(full, ctx)) { return null; }
        return ctx.ScriptStateOf(full) switch
        {
            ScriptState.Clean or ScriptState.Tracked => null,
            ScriptState.Untracked =>
                $"'{program} {script.Text}' runs a new file the repository doesn't track — a script written during the run can write "
                + $"anywhere. Commit it first (`git add {script.Text} && git commit -m \"…\"`) — it then ships in the pull request — "
                + "and run it again.",
            ScriptState.Ignored or ScriptState.Missing when ctx.WrittenByRun(full) =>
                $"'{program} {script.Text}' runs a file this run wrote where git ignores it (it would never ship). Put the script in "
                + "the repository and commit it, then run it again.",
            ScriptState.Ignored or ScriptState.Missing => null, // a build output (dist/, bin/, node_modules/) or not built yet
            _ => $"'{program} {script.Text}' runs a file that isn't part of a repository checkout — a script written during the "
                + "run can write anywhere. Commit it to the repository first, then run it again.",
        };
    }

    // The runner's own copy-in helper (RunFiles.CopyHelper) — rewritten by the runner every run.
    private static bool IsCopyHelper(string full, GuardContext ctx) =>
        string.Equals(full, Path.GetFullPath(Path.Join(ctx.RunDir, RunFiles.CopyHelper)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool LooksLikePath(string value) =>
        value.StartsWith('.') || value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal);

    private static bool IsAssignment(string word, bool powerShell)
    {
        if (powerShell) { return false; }
        var eq = word.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 && word[..eq].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') && !char.IsAsciiDigit(word[0]);
    }

    /// <summary>The program a word names: no folder, no Windows extension, lower case.</summary>
    internal static string ProgramName(string word)
    {
        var slash = word.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? word[(slash + 1)..] : word;
        foreach (var ext in ProgramExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        }
        return name.ToLowerInvariant();
    }

    // ── paths and variables ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The text of a word with the variables and command substitutions the guard can vouch for expanded; null
    /// when anything else is computed.</summary>
    internal static string? Expand(ShellWords.Word word, string? cwd, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(ctx);
        if (!word.Dynamic) { return word.Text; }
        var t = word.Text;
        var sb = new StringBuilder();
        var i = 0;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '`') { return null; }
            if (c != '$') { sb.Append(c); i++; continue; }
            if (i + 1 < t.Length && t[i + 1] == '(')
            {
                var end = ShellWords.FindSubstitutionEnd(t, i + 2, powerShell: false);
                if (end >= t.Length || KnownSubstitution(t[(i + 2)..end].Trim(), cwd, ctx) is not { } value) { return null; }
                sb.Append(value);
                i = end + 1;
                continue;
            }
            string name;
            if (i + 1 < t.Length && t[i + 1] == '{')
            {
                var close = t.IndexOf('}', i);
                if (close < 0) { return null; }
                name = t[(i + 2)..close];
                i = close + 1;
            }
            else
            {
                var j = i + 1;
                while (j < t.Length && (char.IsAsciiLetterOrDigit(t[j]) || t[j] is '_' or ':')) { j++; }
                name = t[(i + 1)..j];
                i = j;
            }
            if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) { name = name[4..]; }
            if (name.Length == 0 || ctx.Variable(name, cwd) is not { } v) { return null; }
            sb.Append(v);
        }
        return sb.ToString();
    }

    private static string? KnownSubstitution(string body, string? cwd, GuardContext ctx) => body switch
    {
        "pwd" => cwd,
        "git rev-parse --show-toplevel" => GitTopLevel(cwd, ctx),
        _ => null,
    };

    private static string? GitTopLevel(string? cwd, GuardContext ctx)
    {
        var dir = cwd;
        while (dir is not null && ctx.IsWritable(dir))
        {
            var git = Path.Join(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git)) { return dir; }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>The full path <paramref name="path"/> names from <paramref name="cwd"/>: <c>~</c> is the home folder, a Git
    /// Bash drive path (<c>/c/Users/…</c>) is the Windows one, and on Windows Git Bash's <c>/tmp</c> is the run's temp
    /// folder (<paramref name="tempDir"/> — the CLI's TEMP). Null when it can't be resolved.</summary>
    internal static string? Resolve(string path, string cwd, string? tempDir = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(cwd);
        if (path.Length == 0) { return null; }
        var p = IsHomePath(path) ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..])
            : OperatingSystem.IsWindows() && tempDir is not null && IsGitBashTmp(path) ? Path.Join(tempDir, path[4..])
            : FromGitBash(path);
        try
        {
            var baseDir = FromGitBash(cwd);
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Join(baseDir, p));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = ex;
            return null;
        }
    }

    private static bool IsHomePath(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal);

    private static bool IsGitBashTmp(string path) => path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal);

    /// <summary>Windows: a Git Bash drive path ("/c" or "/c/Users/…") is "C:\…". Anything else is returned as it is.</summary>
    internal static string FromGitBash(string path) =>
        OperatingSystem.IsWindows() && IsGitBashDrivePath(path)
            ? path[1] + ":\\" + (path.Length > 3 ? path[3..] : string.Empty)
            : path;

    private static bool IsGitBashDrivePath(string path)
    {
        if (path.Length < 2 || path[0] != '/' || !char.IsAsciiLetter(path[1])) { return false; }
        return path.Length == 2 || path[2] == '/';
    }

    /// <summary>Whether <paramref name="fullPath"/> is <paramref name="root"/> or inside it (case-insensitive on Windows).</summary>
    internal static bool IsInside(string fullPath, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.GetFullPath(FromGitBash(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(FromGitBash(fullPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, r, comparison)
            || (full.Length > r.Length && full.StartsWith(r, comparison) && full[r.Length] is '/' or '\\');
    }

    /// <summary>What git says about a file: <c>git status --porcelain --ignored -- &lt;file&gt;</c> from its folder.</summary>
    internal static ScriptState ScriptStateOf(string fullPath)
    {
        if (!File.Exists(fullPath)) { return ScriptState.Missing; }
        if (Path.GetDirectoryName(fullPath) is not { } dir) { return ScriptState.NotInRepo; }
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in GitStatusArgs) { psi.ArgumentList.Add(a); }
        psi.ArgumentList.Add(Path.GetFileName(fullPath));
        try
        {
            using var git = Process.Start(psi);
            if (git is null) { return ScriptState.NotInRepo; }
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(10_000)) { git.Kill(); return ScriptState.NotInRepo; }
            _ = stderr.Result;
            if (git.ExitCode != 0) { return ScriptState.NotInRepo; }
            var line = output.Trim();
            if (line.Length == 0) { return ScriptState.Clean; }
            return line.StartsWith("??", StringComparison.Ordinal) ? ScriptState.Untracked
                : line.StartsWith("!!", StringComparison.Ordinal) ? ScriptState.Ignored
                : ScriptState.Tracked;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _ = ex;
            return ScriptState.NotInRepo;
        }
    }

    // [System.IO.File]::…, [IO.Directory]::…, a StreamWriter — not [System.IO.Path]:: (string work only).
    [GeneratedRegex(@"\[(?:System\.)?IO\.(?!Path\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetFileApi();

    // MSBuild properties that name a folder or a file (OutputPath, OutDir, BaseIntermediateOutputPath, PublishDir…).
    [GeneratedRegex("(?:path|dir|directory|file|output)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathProperty();

    // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The guard applies to claude (codex has its own workspace-write sandbox).</summary>
    internal static bool AppliesTo(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command[(command.LastIndexOfAny(['/', '\\']) + 1)..]);
        return !string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hook command line: this runner's own executable with <c>guard</c> (via <c>dotnet &lt;dll&gt;</c> when the
    /// runner runs under the dotnet host). Forward slashes and double quotes work in every shell claude runs hooks with.</summary>
    internal static string GuardCommand(string? processPath, string? entryAssemblyPath)
    {
        static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";
        var exe = processPath ?? "AgentOs.RemoteAgent";
        // Either separator: a Windows path must read the same on any OS (Path only splits on '/' off Windows).
        var name = Path.GetFileNameWithoutExtension(exe[(exe.LastIndexOfAny(['/', '\\']) + 1)..]);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) && entryAssemblyPath is { Length: > 0 }
            ? Quote(exe) + " " + Quote(entryAssemblyPath) + " guard"
            : Quote(exe) + " guard";
    }

    /// <summary>The guard command for this running runner.</summary>
    internal static string CurrentGuardCommand() => GuardCommand(Environment.ProcessPath, Assembly.GetEntryAssembly()?.Location);

    /// <summary>Whether a run gets the guard (and with it the skill-write approver): a scoped claude run with the runner's
    /// own flags — never an answer-only run, YOLO (<c>REMOTE_AGENT_YOLO</c>), custom flags (<c>REMOTE_AGENT_ARGS</c>) or
    /// codex.</summary>
    internal static bool Guards(bool scoped, bool yolo, bool customArgs, string command) =>
        scoped && !yolo && !customArgs && AppliesTo(command);

    /// <summary>The <c>--settings</c> JSON that installs the guard as a PreToolUse hook, and as the PermissionRequest hook
    /// that approves writes to the project's skills (the only protected path a run may change).</summary>
    internal static string HookSettingsJson(string guardCommand)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("hooks");
            WriteHook(w, "PreToolUse", Matcher, guardCommand);
            WriteHook(w, PermissionRequestEvent, PermissionMatcher, guardCommand);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());

        static void WriteHook(Utf8JsonWriter w, string hookEvent, string matcher, string command)
        {
            w.WriteStartArray(hookEvent);
            w.WriteStartObject();
            w.WriteString("matcher", matcher);
            w.WriteStartArray("hooks");
            w.WriteStartObject();
            w.WriteString("type", "command");
            w.WriteString("command", command);
            w.WriteNumber("timeout", 30);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
        }
    }

    /// <summary>Write the run's settings file (outside the run folder, so it is not part of the checkout) and return its
    /// path. The caller deletes it when the run ends.</summary>
    internal static string WriteSettings(string folder, string runDir, string guardCommand)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, Path.GetFileName(runDir.TrimEnd('/', '\\')) + ".settings.json");
        File.WriteAllText(path, HookSettingsJson(guardCommand));
        return path;
    }

    /// <summary>The run's ledger of written paths, next to its settings file (outside the run folder).</summary>
    internal static string LedgerPath(string folder, string runDir) =>
        Path.Join(folder, Path.GetFileName(runDir.TrimEnd('/', '\\')) + ".ledger");

    /// <summary>The run's own temp folder: beside the run folder (the agent clones into the run folder, which must start
    /// empty), removed with it.</summary>
    internal static string TempFolderFor(string runDir) => runDir.TrimEnd('/', '\\') + RunsRootWatch.TempSuffix;

    /// <summary>Where the per-run settings files live.</summary>
    internal static string SettingsFolder() => Path.Join(Path.GetTempPath(), "agentos-net-runner");

    /// <summary>The paths earlier calls of the run wrote — one per line, appended by each allowed call.</summary>
    private sealed class Ledger
    {
        private readonly string? _path;
        private readonly List<string> _entries;
        private readonly bool _full;

        private Ledger(string? path, List<string> entries, bool full)
        {
            _path = path;
            _entries = entries;
            _full = full;
        }

        public static Ledger Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return new Ledger(path, [], false); }
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            return new Ledger(path, lines, lines.Count >= MaxLedgerLines);
        }

        // Past the cap the ledger can't tell — every ignored script counts as written by the run.
        public bool Contains(string fullPath) => _full || _entries.Exists(e => IsInside(fullPath, e));

        public void Append(List<string> written)
        {
            if (_path is null || written.Count == 0 || _full) { return; }
            File.AppendAllLines(_path, written.Distinct(StringComparer.Ordinal).Take(MaxLedgerLines - _entries.Count));
        }
    }
}

/// <summary>A small shell tokenizer — enough to find the commands, their words, redirection targets and command
/// substitutions of a Bash or PowerShell command line. Quotes are honoured; a word that holds a variable or a command
/// substitution is marked <see cref="Word.Dynamic"/>; heredoc bodies are skipped (they are text, not commands).</summary>
internal static class ShellWords
{
    /// <summary>One word of a command, unquoted. <see cref="Dynamic"/>: its value is only known when the shell runs;
    /// <see cref="Quoted"/>: it started with a quote (in PowerShell a quoted string on its own is a value, not a command).</summary>
    internal sealed record Word(string Text, bool Dynamic, bool Quoted = false);

    /// <summary>One simple command: its words, where its output goes (<paramref name="Redirects"/>), the files it reads
    /// (<paramref name="Inputs"/>), the command substitutions it runs first, whether a pipe feeds it, and whether a heredoc
    /// or here-string does.</summary>
    internal sealed record Simple(IReadOnlyList<Word> Words, IReadOnlyList<Word> Redirects, IReadOnlyList<Word> Inputs,
        IReadOnlyList<string> Substitutions, bool PipedIn, bool StdinText);

    /// <summary>The simple commands of <paramref name="text"/>, split at <c>&amp;&amp; || ; | &amp;</c>, newlines and
    /// (PowerShell) braces / parentheses.</summary>
    internal static IReadOnlyList<Simple> Commands(string text, bool powerShell) => new Parser(text, powerShell).Run();

    private enum Target { None, Output, Input, Discard }

    private sealed class Parser(string text, bool powerShell)
    {
        private readonly List<Simple> _result = [];
        private readonly List<Word> _words = [];
        private readonly List<Word> _redirects = [];
        private readonly List<Word> _inputs = [];
        private readonly List<string> _subs = [];
        private readonly List<string> _heredocs = [];
        private readonly StringBuilder _current = new();
        private bool _inWord;
        private bool _dynamic;
        private bool _pipedIn;
        private bool _stdinText;
        private bool _quoted;
        private Target _target;

        private char Escape => powerShell ? '`' : '\\';

        public List<Simple> Run()
        {
            var i = 0;
            while (i < text.Length) { i = Step(i); }
            EndCommand(pipeAfter: false);
            return _result;
        }

        private bool At(int i, char c) => i < text.Length && text[i] == c;

        private int Step(int i)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\n':
                    EndCommand(pipeAfter: false);
                    return _heredocs.Count > 0 ? SkipHeredocs(text, i + 1, _heredocs) : i + 1;
                case ' ' or '\t' or '\r':
                    EndWord();
                    return i + 1;
                case ';':
                    EndCommand(pipeAfter: false);
                    return i + 1;
                case '|':
                    if (At(i + 1, '|')) { EndCommand(pipeAfter: false); return i + 2; }
                    EndCommand(pipeAfter: true);
                    return At(i + 1, '&') ? i + 2 : i + 1;
                case '&':
                    if (!powerShell && At(i + 1, '>')) { EndWord(); return Redirect(i + 1); } // &> / &>>
                    EndCommand(pipeAfter: false);
                    return At(i + 1, '&') ? i + 2 : i + 1;
                case '>':
                    // "2>" / "*>": the stream number before it is not a word.
                    if (_inWord && IsStreamNumber(_current.ToString())) { ResetWord(); }
                    EndWord();
                    return Redirect(i);
                case '<':
                    return LessThan(i);
                case '\'':
                    _quoted |= !_inWord;
                    return SingleQuoted(i + 1);
                case '"':
                    _quoted |= !_inWord;
                    return DoubleQuoted(i + 1);
                case '$':
                    _inWord = true;
                    return Dollar(i);
                default:
                    break;
            }
            if (powerShell && ch is '{' or '}' or '(' or ')') { EndCommand(pipeAfter: false); return i + 1; }
            if (!powerShell && ch is '(' or ')' && !_inWord) { EndCommand(pipeAfter: false); return i + 1; }
            if (!powerShell && ch == '`') { _inWord = true; return Backticks(i); }
            if (ch == Escape && i + 1 < text.Length)
            {
                _inWord = true;
                if (text[i + 1] != '\n') { _current.Append(text[i + 1]); }
                return i + 2;
            }
            _inWord = true;
            _current.Append(ch);
            return i + 1;
        }

        private void ResetWord()
        {
            _current.Clear();
            _inWord = false;
            _dynamic = false;
            _quoted = false;
        }

        private void EndWord()
        {
            if (!_inWord) { return; }
            // PowerShell takes either separator on every OS; elsewhere a backslash would be part of a file name.
            var value = powerShell && !OperatingSystem.IsWindows() ? _current.ToString().Replace('\\', '/') : _current.ToString();
            var word = new Word(value, _dynamic, _quoted);
            switch (_target)
            {
                case Target.Output: _redirects.Add(word); break;
                case Target.Input: _inputs.Add(word); break;
                case Target.Discard: break;
                default: _words.Add(word); break;
            }
            _target = Target.None;
            ResetWord();
        }

        private void EndCommand(bool pipeAfter)
        {
            EndWord();
            if (_words.Count > 0 || _redirects.Count > 0 || _inputs.Count > 0 || _subs.Count > 0)
            {
                _result.Add(new Simple([.. _words], [.. _redirects], [.. _inputs], [.. _subs], _pipedIn, _stdinText));
            }
            _words.Clear();
            _redirects.Clear();
            _inputs.Clear();
            _subs.Clear();
            _pipedIn = pipeAfter;
            _stdinText = false;
            _target = Target.None;
        }

        // At a '>' (or the '>' after '&'): consume the operator; "&N" / "&-" duplicates a stream (no file); ">&file" is a file.
        private int Redirect(int i)
        {
            i++; // '>'
            if (At(i, '>') || At(i, '|')) { i++; }
            if (At(i, '&'))
            {
                i++;
                if (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '-'))
                {
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '-')) { i++; }
                    return i;
                }
            }
            _target = Target.Output;
            return i;
        }

        private int LessThan(int i)
        {
            EndWord();
            if (At(i + 1, '>')) { _target = Target.Output; return i + 2; } // <> opens the file for writing too
            if (!powerShell && At(i + 1, '<'))
            {
                _stdinText = true;
                if (At(i + 2, '<')) { _target = Target.Discard; return i + 3; } // <<< here-string
                return ReadHeredocDelimiter(text, i + 2, _heredocs);
            }
            _target = Target.Input;
            return i + 1;
        }

        private int SingleQuoted(int i)
        {
            _inWord = true;
            while (i < text.Length)
            {
                if (text[i] == '\'')
                {
                    if (powerShell && At(i + 1, '\'')) { _current.Append('\''); i += 2; continue; }
                    return i + 1;
                }
                _current.Append(text[i++]);
            }
            return i;
        }

        private int DoubleQuoted(int i)
        {
            _inWord = true;
            while (i < text.Length && text[i] != '"')
            {
                // Bash keeps a backslash inside double quotes unless it escapes $ ` " \ or a newline ("C:\Users" stays).
                if (text[i] == Escape && i + 1 < text.Length && (powerShell || text[i + 1] is '$' or '`' or '"' or '\\' or '\n'))
                {
                    _current.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                if (text[i] == '$') { i = Dollar(i); continue; }
                if (!powerShell && text[i] == '`') { i = Backticks(i); continue; }
                _current.Append(text[i++]);
            }
            return i + 1;
        }

        // "$(…)" — a command substitution (checked as a command of its own); "$((…))" arithmetic; "${…}" / "$name".
        private int Dollar(int i)
        {
            _dynamic = true;
            if (At(i + 1, '('))
            {
                var end = FindSubstitutionEnd(text, i + 2, powerShell);
                var body = text[(i + 2)..Math.Min(end, text.Length)];
                if (!body.StartsWith('(')) { _subs.Add(body); } // not arithmetic
                _current.Append("$(").Append(body).Append(')');
                return Math.Min(end + 1, text.Length);
            }
            if (At(i + 1, '{'))
            {
                var close = text.IndexOf('}', i);
                var end = close < 0 ? text.Length : close + 1;
                _current.Append(text, i, end - i);
                return end;
            }
            _current.Append('$');
            return i + 1;
        }

        private int Backticks(int i)
        {
            _dynamic = true;
            var close = i + 1;
            while (close < text.Length && text[close] != '`')
            {
                close += text[close] == '\\' ? 2 : 1;
            }
            var end = Math.Min(close, text.Length);
            _subs.Add(text[(i + 1)..end]);
            _current.Append(text, i, Math.Min(end + 1, text.Length) - i);
            return Math.Min(end + 1, text.Length);
        }
    }

    // "2" in "2>" / "*" in PowerShell's "*>": the stream being redirected, not a word.
    private static bool IsStreamNumber(string word) => word == "*" || (word.Length > 0 && word.All(char.IsAsciiDigit));

    /// <summary>The index of the ')' closing a "$(" whose body starts at <paramref name="start"/> (text length when it is
    /// never closed). Quotes, nested substitutions, backticks and heredoc bodies are skipped, so a ")" in a commit
    /// message's heredoc ("1) fixed …") doesn't end it.</summary>
    internal static int FindSubstitutionEnd(string text, int start, bool powerShell)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = 1;
        var heredocs = new List<string>();
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n' && heredocs.Count > 0) { i = SkipHeredocs(text, i + 1, heredocs); continue; }
            if (c == (powerShell ? '`' : '\\')) { i += 2; continue; }
            switch (c)
            {
                case '\'':
                    var q = text.IndexOf('\'', i + 1);
                    i = q < 0 ? text.Length : q + 1;
                    continue;
                case '"':
                    i = SkipDoubleQuoted(text, i + 1, powerShell);
                    continue;
                case '`' when !powerShell:
                    var b = text.IndexOf('`', i + 1);
                    i = b < 0 ? text.Length : b + 1;
                    continue;
                case '<' when !powerShell && i + 1 < text.Length && text[i + 1] == '<' && (i + 2 >= text.Length || text[i + 2] != '<'):
                    i = ReadHeredocDelimiter(text, i + 2, heredocs);
                    continue;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0) { return i; }
                    break;
                default:
                    break;
            }
            i++;
        }
        return text.Length;
    }

    // From just inside a '"' to just past its closing '"' (nested "$(…)" followed).
    private static int SkipDoubleQuoted(string text, int i, bool powerShell)
    {
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == (powerShell ? '`' : '\\')) { i += 2; continue; }
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                i = FindSubstitutionEnd(text, i + 2, powerShell) + 1;
                continue;
            }
            i++;
        }
        return i + 1;
    }

    // After "<<" (or "<<-"): the delimiter word, quotes removed; its body starts at the next line.
    private static int ReadHeredocDelimiter(string text, int i, List<string> heredocs)
    {
        if (i < text.Length && text[i] == '-') { i++; }
        while (i < text.Length && text[i] is ' ' or '\t') { i++; }
        var sb = new StringBuilder();
        while (i < text.Length && text[i] is not (' ' or '\t' or '\n' or ';' or '|' or '&' or ')'))
        {
            if (text[i] is not ('\'' or '"' or '\\')) { sb.Append(text[i]); }
            i++;
        }
        if (sb.Length > 0) { heredocs.Add(sb.ToString()); }
        return i;
    }

    // Skip each pending heredoc body: lines up to (and including) its delimiter line.
    private static int SkipHeredocs(string text, int i, List<string> heredocs)
    {
        foreach (var delimiter in heredocs)
        {
            while (i < text.Length)
            {
                var end = text.IndexOf('\n', i);
                var line = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end + 1;
                if (line == delimiter) { break; }
            }
        }
        heredocs.Clear();
        return i;
    }
}
