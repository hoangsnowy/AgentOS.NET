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
// `cd`s inside the command — and blocks the call (exit 2, the reason goes back to the agent) when one is outside the run
// folder (AGENTOS_RUN_DIR, set on the CLI's environment):
//   - the file tools' file_path / notebook_path;
//   - `cd` / `pushd` / Set-Location out of the run folder, and any command run from a folder outside it;
//   - redirections (`>`, `>>`, `2>`, `&>`, `*>`) — except /dev/null, NUL, $null;
//   - file-writing commands (mkdir, touch, cp, mv, rm, ln, tar, unzip, tee, New-Item, Copy-Item, Set-Content, Out-File…):
//     every argument that isn't an option;
//   - git: `-C <dir>`, `--git-dir` / `--work-tree`, `worktree add <path>`, `clone <repo> <dir>`, `init <dir>`,
//     `archive -o`, `format-patch -o`, `bundle create`; gh: `repo clone <repo> <dir>`;
//   - dotnet / npm / pnpm / yarn / curl / wget output options (`-o`, `--output`, `--prefix`, `--tool-path`…) and global
//     installs (`-g`, `--global`);
//   - scripts run by an interpreter (node, deno, bun, tsx, ts-node, python): the script must be inside the run folder AND
//     a file of the repository that is committed and unmodified (`git status --porcelain --ignored` says nothing about
//     it) — a helper the agent just wrote is refused; inline code (`node -e`, `python -c`) is refused;
//   - `sh -c` / `bash -c` / `pwsh -Command` strings are checked the same way; a path that is computed (`$VAR`, `$(…)`)
//     where a write goes is refused (it can't be checked).
// What the guard can NOT see is what a build tool does with the repository's own code (an npm script, an MSBuild target):
// that runs with the member's rights, as before — the end-of-run sweep (RunsRootWatch in RunCleanup.cs) removes what such
// code leaves next to the run folders, and an OS sandbox remains the real boundary (docs/adr/0018).

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AgentOs.RemoteAgent;

/// <summary>The PreToolUse guard that keeps a scoped run's writes inside its run folder.</summary>
internal static class ScopedWriteGuard
{
    /// <summary>The CLI environment variable naming the run folder (the hook reads it).</summary>
    internal const string RunDirVariable = "AGENTOS_RUN_DIR";

    /// <summary>How every block starts — CliProgress recognises it in a tool result and records the call as refused.</summary>
    internal const string BlockedPrefix = "Blocked by the AgentOS.NET runner:";

    /// <summary>The tools the hook is asked about.</summary>
    internal const string Matcher = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit";

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

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "deno", "bun", "tsx", "ts-node", "python", "python3", "py",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "sh", "bash", "zsh", "dash" };

    private static readonly HashSet<string> PowerShells = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell" };

    // Prefixes that run the next word as the command.
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "env", "nohup", "time", "command", "exec", "nice" };

    // Options of the package / build / download tools whose value is where they write.
    private static readonly HashSet<string> OutputOptions = new(StringComparer.Ordinal)
    {
        "-o", "--output", "--output-dir", "--output-directory", "--prefix", "--tool-path", "--directory-prefix", "-P",
        "-O", "--output-document", "--modules-folder", "--global-folder", "--cache-folder", "--results-directory",
    };

    private static readonly HashSet<string> OutputTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "pnpm", "yarn", "npx", "curl", "wget",
    };

    private static readonly HashSet<string> NullTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/dev/null", "nul", "$null", "/dev/stdout", "/dev/stderr",
    };

    private static readonly string[] ProgramExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    private static readonly string[] GitStatusArgs = ["status", "--porcelain", "--ignored", "--"];

    // Files inside the run folder that are still off limits: git's own folder (a hook there runs on the next commit) and
    // claude's project settings (a follow-up run resumed in this folder would load their permissions and hooks).
    private static readonly string[] ProtectedFiles = ["settings.json", "settings.local.json"];

    // ── the hook ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>AgentOs.RemoteAgent guard</c>: read the hook's JSON from <paramref name="input"/>; 0 = allow, 2 = block
    /// (the reason on <paramref name="error"/> goes back to the agent). Fails closed: anything unexpected blocks.</summary>
    internal static int Run(TextReader input, TextWriter error, string? runDir)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);
        if (string.IsNullOrWhiteSpace(runDir)) { return 0; } // not a scoped run: nothing to confine
        try
        {
            if (CheckHookInput(input.ReadToEnd(), runDir) is not { } reason) { return 0; }
            error.WriteLine(BlockedPrefix + " " + reason
                + " This run may only write inside its own folder (" + runDir + ").");
            return 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error.WriteLine(BlockedPrefix + " the call could not be checked (" + ex.GetType().Name + ").");
            return 2;
        }
    }

    /// <summary>Why the tool call in <paramref name="json"/> (a PreToolUse hook input) must not run, or null.</summary>
    internal static string? CheckHookInput(string json, string runDir, Func<string, bool>? isCleanRepoFile = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tool = root.TryGetProperty("tool_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var cwd = root.TryGetProperty("cwd", out var c) && c.GetString() is { Length: > 0 } dir ? dir : runDir;
        if (!root.TryGetProperty("tool_input", out var toolInput) || toolInput.ValueKind != JsonValueKind.Object) { return null; }
        string? Field(string name) => toolInput.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            "Bash" => Field("command") is { } bash ? CheckCommand(bash, cwd, runDir, powerShell: false, isCleanRepoFile) : null,
            "PowerShell" => Field("command") is { } ps ? CheckCommand(ps, cwd, runDir, powerShell: true, isCleanRepoFile) : null,
            "Write" or "Edit" or "MultiEdit" => CheckFileTool(Field("file_path"), cwd, runDir),
            "NotebookEdit" => CheckFileTool(Field("notebook_path"), cwd, runDir),
            _ => null,
        };
    }

    /// <summary>A file tool's target must resolve inside the run folder.</summary>
    internal static string? CheckFileTool(string? path, string cwd, string runDir)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        return Resolve(path, cwd) is { } full ? WriteProblem(full, path, runDir) : $"'{path}' can't be resolved.";
    }

    /// <summary>Why a write to <paramref name="full"/> is refused (outside the run folder, or one of its protected files),
    /// or null.</summary>
    internal static string? WriteProblem(string full, string shown, string runDir)
    {
        if (!IsInside(full, runDir)) { return $"'{shown}' is outside the run folder."; }
        var relative = Path.GetRelativePath(Path.GetFullPath(NormalizeCwd(runDir)), full).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && Array.Exists(segments[..^1], seg => seg.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{shown}' is inside git's own folder (.git) — use git commands.";
        }
        if (segments.Length >= 2 && segments[^2].Equals(".claude", StringComparison.OrdinalIgnoreCase)
            && ProtectedFiles.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return $"'{shown}' is Claude Code's settings file — a run may not change its own permissions.";
        }
        return null;
    }

    // ── shell commands ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Why <paramref name="command"/> (as the Bash or PowerShell tool would run it from <paramref name="cwd"/>)
    /// must not run, or null.</summary>
    internal static string? CheckCommand(string command, string cwd, string runDir, bool powerShell, Func<string, bool>? isCleanRepoFile = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        isCleanRepoFile ??= IsCleanRepoFile;
        if (powerShell && (command.Contains("[System.IO.", StringComparison.OrdinalIgnoreCase) || command.Contains("[IO.", StringComparison.OrdinalIgnoreCase)))
        {
            return "direct .NET file calls ([System.IO.…]) can't be checked.";
        }
        string? here = cwd;
        foreach (var simple in ShellWords.Commands(command, powerShell))
        {
            if (CheckSimple(simple, ref here, runDir, powerShell, isCleanRepoFile) is { } reason) { return reason; }
        }
        return null;
    }

    private static string? CheckSimple(ShellWords.Simple simple, ref string? here, string runDir, bool powerShell, Func<string, bool> isCleanRepoFile)
    {
        var cwd = here;
        foreach (var target in simple.Redirects)
        {
            if (target.Text.Length == 0 || NullTargets.Contains(target.Text)) { continue; }
            if (target.Dynamic) { return $"the redirection target '{target.Text}' is computed, so it can't be checked."; }
            if (cwd is null || Resolve(target.Text, cwd) is not { } full)
            {
                return $"the redirection to '{target.Text}' can't be resolved.";
            }
            if (WriteProblem(full, target.Text, runDir) is { } problem) { return "the redirection is refused: " + problem; }
        }

        var words = simple.Words;
        var start = 0;
        while (start < words.Count && (IsAssignment(words[start].Text, powerShell) || Wrappers.Contains(ProgramName(words[start].Text))))
        {
            start++;
        }
        if (start >= words.Count) { return null; }
        var program = ProgramName(words[start].Text);
        var args = words.Skip(start + 1).ToList();

        if (CdCommands.Contains(program))
        {
            var target = args.FirstOrDefault(a => !a.Text.StartsWith('-'));
            if (target is null) { return "'" + program + "' without a folder leaves the run folder."; }
            if (target.Dynamic) { here = null; return null; } // computed: later writes in this command can't be checked
            if (cwd is null || Resolve(target.Text, cwd) is not { } dir || !IsInside(dir, runDir))
            {
                return $"'{program} {target.Text}' leaves the run folder.";
            }
            here = dir;
            return null;
        }

        var guarded = WriteCommands.Contains(program) || program.Equals("git", StringComparison.OrdinalIgnoreCase)
            || program.Equals("gh", StringComparison.OrdinalIgnoreCase) || OutputTools.Contains(program)
            || Interpreters.Contains(program) || Shells.Contains(program) || PowerShells.Contains(program)
            || program.Equals("xargs", StringComparison.OrdinalIgnoreCase);
        if (!guarded) { return cwd is not null && !IsInside(cwd, runDir) ? "it runs from a folder outside the run folder." : null; }
        if (cwd is null) { return "it runs after a 'cd' to a computed folder, so its paths can't be checked."; }
        if (!IsInside(cwd, runDir)) { return "it runs from a folder outside the run folder."; }

        if (WriteCommands.Contains(program)) { return CheckPaths(program, Operands(args), cwd, runDir); }
        return program.ToLowerInvariant() switch
        {
            "git" => CheckGit(args, cwd, runDir),
            "gh" => CheckGh(args, cwd, runDir),
            "xargs" => args.FirstOrDefault(a => !a.Text.StartsWith('-')) is { } next && WriteCommands.Contains(ProgramName(next.Text))
                ? $"'xargs {next.Text}' writes to paths that can't be checked."
                : null,
            _ when Interpreters.Contains(program) => CheckInterpreter(program, args, cwd, runDir, isCleanRepoFile),
            _ when Shells.Contains(program) => NestedScript(args, "-c", cwd, runDir, powerShell: false, isCleanRepoFile),
            _ when PowerShells.Contains(program) => NestedScript(args, "-c", cwd, runDir, powerShell: true, isCleanRepoFile)
                ?? NestedScript(args, "-command", cwd, runDir, powerShell: true, isCleanRepoFile),
            _ => CheckOutputs(program, args, cwd, runDir),
        };
    }

    private static string? CheckPaths(string what, IEnumerable<ShellWords.Word> paths, string cwd, string runDir)
    {
        foreach (var p in paths)
        {
            if (p.Dynamic) { return $"'{what}' gets a computed path ('{p.Text}'), so it can't be checked."; }
            if (Resolve(p.Text, cwd) is not { } full) { return $"'{what} {p.Text}': the path can't be resolved."; }
            if (WriteProblem(full, p.Text, runDir) is { } problem) { return $"'{what} {p.Text}' is refused: " + problem; }
        }
        return null;
    }

    // Arguments that aren't options; "--opt=value" contributes its value; after "--" everything is an operand.
    private static List<ShellWords.Word> Operands(List<ShellWords.Word> args)
    {
        var result = new List<ShellWords.Word>();
        var endOfOptions = false;
        foreach (var a in args)
        {
            if (!endOfOptions && a.Text == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !a.Text.StartsWith('-')) { result.Add(a); continue; }
            var eq = a.Text.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && eq < a.Text.Length - 1) { result.Add(a with { Text = a.Text[(eq + 1)..] }); }
        }
        return result;
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
            if (endOfOptions || !text.StartsWith('-')) { result.Add(list[i]); continue; }
            if (!text.Contains('=', StringComparison.Ordinal) && valueOptions.Contains(text)) { i++; }
        }
        return result;
    }

    // The value of option `name` ("--name value" or "--name=value"), every occurrence.
    private static IEnumerable<ShellWords.Word> OptionValues(List<ShellWords.Word> args, HashSet<string> names)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var text = args[i].Text;
            if (text == "--") { yield break; }
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && text.StartsWith("--", StringComparison.Ordinal) && names.Contains(text[..eq]))
            {
                yield return args[i] with { Text = text[(eq + 1)..] };
            }
            else if (names.Contains(text) && i + 1 < args.Count)
            {
                yield return args[++i];
            }
        }
    }

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

    private static string? CheckGit(List<ShellWords.Word> args, string cwd, string runDir)
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
                if (CheckPaths("git " + name, [value], gitCwd, runDir) is { } reason) { return reason; }
                if (name == "-C") { gitCwd = Resolve(value.Text, gitCwd) ?? gitCwd; }
            }
            else if (eq < 0 && GitGlobalWithValue.Contains(name)) { i++; }
            i++;
        }
        if (i >= args.Count) { return null; }
        var sub = args[i].Text;
        var rest = args.Skip(i + 1).ToList();
        if (CheckPaths("git " + sub, OptionValues(rest, GitOutputOptions), gitCwd, runDir) is { } output) { return output; }
        switch (sub)
        {
            case "worktree" when rest.Count > 0 && rest[0].Text is "add" or "move":
                var paths = Positionals(rest.Skip(1), WorktreeAddValueOptions);
                var target = rest[0].Text == "add" ? paths.FirstOrDefault() : paths.ElementAtOrDefault(1);
                return target is null ? null : CheckPaths("git worktree " + rest[0].Text, [target], gitCwd, runDir);
            case "clone":
                var dest = Positionals(rest, CloneValueOptions).ElementAtOrDefault(1);
                return dest is null ? null : CheckPaths("git clone", [dest], gitCwd, runDir);
            case "init":
                var initDir = Positionals(rest, CloneValueOptions).FirstOrDefault();
                return initDir is null ? null : CheckPaths("git init", [initDir], gitCwd, runDir);
            case "bundle" when rest.Count > 1 && rest[0].Text == "create":
                return CheckPaths("git bundle create", [rest[1]], gitCwd, runDir);
            default:
                return null;
        }
    }

    private static readonly HashSet<string> NoValueOptions = new(StringComparer.Ordinal);

    private static string? CheckGh(List<ShellWords.Word> args, string cwd, string runDir)
    {
        var positional = Positionals(args, NoValueOptions);
        if (positional.Count >= 4 && positional[0].Text == "repo" && positional[1].Text == "clone")
        {
            return CheckPaths("gh repo clone", [positional[3]], cwd, runDir);
        }
        return null;
    }

    private static string? CheckOutputs(string program, List<ShellWords.Word> args, string cwd, string runDir)
    {
        var isPackageManager = program is "npm" or "pnpm" or "yarn" or "dotnet";
        if (isPackageManager && args.Exists(a => a.Text is "-g" or "--global" or "--location=global"))
        {
            return $"'{program} … {(args.Exists(a => a.Text == "-g") ? "-g" : "--global")}' installs outside the run folder.";
        }
        var names = program is "curl" ? CurlOutputs : OutputOptions;
        return CheckPaths(program, OptionValues(args, names), cwd, runDir);
    }

    // curl: -O / -P mean something else there (remote name / port).
    private static readonly HashSet<string> CurlOutputs = new(StringComparer.Ordinal) { "-o", "--output", "--output-dir", "-D", "--dump-header", "-c", "--cookie-jar" };

    private static readonly HashSet<string> InlineCode = new(StringComparer.Ordinal) { "-e", "--eval", "-p", "--print", "-c" };
    private static readonly HashSet<string> ScriptOptions = new(StringComparer.Ordinal) { "-r", "--require", "--import", "--loader", "--experimental-loader" };
    private static readonly HashSet<string> InterpreterValueOptions = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "-C", "--conditions", "--input-type", "--env-file",
        "--title", "-m",
    };

    private static string? CheckInterpreter(string program, List<ShellWords.Word> args, string cwd, string runDir, Func<string, bool> isCleanRepoFile)
    {
        if (args.Exists(a => InlineCode.Contains(a.Text) || a.Text.StartsWith("--eval=", StringComparison.Ordinal)))
        {
            return $"'{program}' with inline code can write anywhere — run one of the repository's own scripts instead.";
        }
        if (args.Exists(a => a.Text == "-m")) { return null; } // python -m <module>: installed code, not a file of the run
        var scripts = OptionValues(args, ScriptOptions).Where(v => LooksLikePath(v.Text)).ToList();
        if (Positionals(args, InterpreterValueOptions).FirstOrDefault() is { } script) { scripts.Add(script); }
        foreach (var s in scripts)
        {
            if (s.Dynamic) { return $"'{program}' runs a computed script path ('{s.Text}')."; }
            if (Resolve(s.Text, cwd) is not { } full || !IsInside(full, runDir)) { return $"'{program} {s.Text}' runs a script outside the run folder."; }
            if (!isCleanRepoFile(full))
            {
                return $"'{program} {s.Text}' runs a script that isn't a committed, unchanged file of the repository (a script written "
                    + "during this run can write anywhere). Use the repository's own scripts, or the file tools.";
            }
        }
        return null;
    }

    private static string? NestedScript(List<ShellWords.Word> args, string flag, string cwd, string runDir, bool powerShell, Func<string, bool> isCleanRepoFile)
    {
        var at = args.FindIndex(a => a.Text.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Count) { return null; }
        return args[at + 1].Dynamic
            ? "a nested shell runs a computed command."
            : CheckCommand(args[at + 1].Text, cwd, runDir, powerShell, isCleanRepoFile);
    }

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

    // ── paths ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The full path <paramref name="path"/> names from <paramref name="cwd"/>: <c>~</c> is the home folder, a Git
    /// Bash drive path (<c>/c/Users/…</c>) is the Windows one. Null when it can't be resolved.</summary>
    internal static string? Resolve(string path, string cwd)
    {
        if (path.Length == 0) { return null; }
        var p = IsHomePath(path)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..])
            : NormalizeCwd(path);
        try
        {
            var baseDir = NormalizeCwd(cwd);
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

    // Windows: a Git Bash drive path ("/c" or "/c/Users/…") is "C:\…". Anything else is returned as it is.
    private static string NormalizeCwd(string path) =>
        OperatingSystem.IsWindows() && IsGitBashDrivePath(path)
            ? path[1] + ":\\" + (path.Length > 3 ? path[3..] : string.Empty)
            : path;

    private static bool IsGitBashDrivePath(string path)
    {
        if (path.Length < 2 || path[0] != '/' || !char.IsAsciiLetter(path[1])) { return false; }
        return path.Length == 2 || path[2] == '/';
    }

    /// <summary>Whether <paramref name="fullPath"/> is the run folder or inside it (case-insensitive on Windows).</summary>
    internal static bool IsInside(string fullPath, string runDir)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(NormalizeCwd(runDir)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(NormalizeCwd(fullPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, comparison)
            || (full.Length > root.Length && full.StartsWith(root, comparison) && full[root.Length] is '/' or '\\');
    }

    /// <summary>Whether a file is committed in its repository and unchanged: <c>git status --porcelain --ignored</c> lists
    /// nothing for it (an untracked, ignored, added or modified file is listed). Not in a repository = no.</summary>
    internal static bool IsCleanRepoFile(string fullPath)
    {
        if (!File.Exists(fullPath) || Path.GetDirectoryName(fullPath) is not { } dir) { return false; }
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
            if (git is null) { return false; }
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(10_000)) { git.Kill(); return false; }
            _ = stderr.Result;
            return git.ExitCode == 0 && output.Trim().Length == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _ = ex;
            return false;
        }
    }

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

    /// <summary>The <c>--settings</c> JSON that installs the guard as a PreToolUse hook.</summary>
    internal static string HookSettingsJson(string guardCommand)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("hooks");
            w.WriteStartArray("PreToolUse");
            w.WriteStartObject();
            w.WriteString("matcher", Matcher);
            w.WriteStartArray("hooks");
            w.WriteStartObject();
            w.WriteString("type", "command");
            w.WriteString("command", guardCommand);
            w.WriteNumber("timeout", 30);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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

    /// <summary>Where the per-run settings files live.</summary>
    internal static string SettingsFolder() => Path.Join(Path.GetTempPath(), "agentos-net-runner");
}

/// <summary>A small shell tokenizer — enough to find the commands, their words and redirection targets of a Bash or
/// PowerShell command line. Quotes are honoured; a word that holds a variable or a command substitution is marked
/// <see cref="Word.Dynamic"/>; heredoc bodies are skipped.</summary>
internal static class ShellWords
{
    /// <summary>One word of a command, unquoted. <see cref="Dynamic"/>: its value is only known when the shell runs.</summary>
    internal sealed record Word(string Text, bool Dynamic);

    /// <summary>One simple command: its words and where its output is redirected to.</summary>
    internal sealed record Simple(IReadOnlyList<Word> Words, IReadOnlyList<Word> Redirects);

    /// <summary>The simple commands of <paramref name="text"/>, split at <c>&amp;&amp; || ; | &amp;</c>, newlines and
    /// (PowerShell) braces / parentheses.</summary>
    internal static IReadOnlyList<Simple> Commands(string text, bool powerShell)
    {
        var result = new List<Simple>();
        var words = new List<Word>();
        var redirects = new List<Word>();
        var current = new StringBuilder();
        var inWord = false;
        var dynamic = false;
        var redirectNext = false;
        var heredocs = new List<string>();
        var escape = powerShell ? '`' : '\\';

        void EndWord()
        {
            if (!inWord) { return; }
            // PowerShell takes either separator on every OS; elsewhere a backslash would be part of a file name.
            var text = powerShell && !OperatingSystem.IsWindows() ? current.ToString().Replace('\\', '/') : current.ToString();
            var word = new Word(text, dynamic);
            if (redirectNext) { redirects.Add(word); redirectNext = false; }
            else { words.Add(word); }
            current.Clear();
            inWord = false;
            dynamic = false;
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0 || redirects.Count > 0) { result.Add(new Simple([.. words], [.. redirects])); }
            words.Clear();
            redirects.Clear();
            redirectNext = false;
        }

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\n')
            {
                EndCommand();
                i++;
                if (heredocs.Count > 0) { i = SkipHeredocs(text, i, heredocs); }
                continue;
            }
            if (ch is ' ' or '\t' or '\r') { EndWord(); i++; continue; }
            if (ch == ';') { EndCommand(); i++; continue; }
            if (ch == '|') { EndCommand(); i += i + 1 < text.Length && text[i + 1] is '|' or '&' ? 2 : 1; continue; }
            if (ch == '&')
            {
                if (!powerShell && i + 1 < text.Length && text[i + 1] == '>')
                {
                    EndWord();
                    i = Redirect(text, i + 1, ref redirectNext);
                    continue;
                }
                EndCommand();
                i += i + 1 < text.Length && text[i + 1] == '&' ? 2 : 1;
                continue;
            }
            if (powerShell && ch is '{' or '}' or '(' or ')') { EndCommand(); i++; continue; }
            if (!powerShell && ch is '(' or ')' && !inWord) { EndCommand(); i++; continue; }
            if (ch == '>')
            {
                // "2>" / "*>": the digits or star before it are the stream, not a word.
                if (inWord && IsStreamNumber(current.ToString()))
                {
                    current.Clear();
                    inWord = false;
                }
                EndWord();
                i = Redirect(text, i, ref redirectNext);
                continue;
            }
            if (ch == '<')
            {
                EndWord();
                if (!powerShell && IsHeredoc(text, i))
                {
                    i = ReadHeredocDelimiter(text, i + 2, heredocs);
                    continue;
                }
                i++;
                // An input redirection reads; its source is not a write target — read it as a throwaway word.
                i = SkipWord(text, i);
                continue;
            }
            if (ch == '\'')
            {
                inWord = true;
                var close = i + 1;
                var sb = new StringBuilder();
                while (close < text.Length)
                {
                    if (text[close] == '\'')
                    {
                        if (powerShell && close + 1 < text.Length && text[close + 1] == '\'') { sb.Append('\''); close += 2; continue; }
                        break;
                    }
                    sb.Append(text[close++]);
                }
                current.Append(sb);
                i = close + 1;
                continue;
            }
            if (ch == '"')
            {
                inWord = true;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    // Bash keeps a backslash inside double quotes unless it escapes $ ` " \ or a newline ("C:\Users" stays).
                    if (text[i] == escape && i + 1 < text.Length && (powerShell || text[i + 1] is '$' or '`' or '"' or '\\' or '\n'))
                    {
                        current.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (text[i] == '$') { dynamic = true; i = DollarPart(text, i, current); continue; }
                    if (!powerShell && text[i] == '`') { dynamic = true; i = Backticks(text, i, current); continue; }
                    current.Append(text[i++]);
                }
                i++;
                continue;
            }
            if (ch == '$') { inWord = true; dynamic = true; i = DollarPart(text, i, current); continue; }
            if (!powerShell && ch == '`') { inWord = true; dynamic = true; i = Backticks(text, i, current); continue; }
            if (ch == escape && i + 1 < text.Length)
            {
                inWord = true;
                if (text[i + 1] != '\n') { current.Append(text[i + 1]); }
                i += 2;
                continue;
            }
            inWord = true;
            current.Append(ch);
            i++;
        }
        EndCommand();
        return result;
    }

    // "2" in "2>" / "*" in PowerShell's "*>": the stream being redirected, not a word.
    private static bool IsStreamNumber(string word) => word == "*" || (word.Length > 0 && word.All(char.IsAsciiDigit));

    // At a '<': "<<" (or "<<-") starts a heredoc; "<<<" is a here-string (one word, read as input).
    private static bool IsHeredoc(string text, int i)
    {
        if (i + 1 >= text.Length || text[i + 1] != '<') { return false; }
        return i + 2 >= text.Length || text[i + 2] != '<';
    }

    // At a '>' (or the '>' after '&'): consume the operator; "&N" / "&-" duplicates a stream (no file).
    private static int Redirect(string text, int i, ref bool redirectNext)
    {
        i++; // '>'
        if (i < text.Length && text[i] is '>' or '|') { i++; }
        if (i < text.Length && text[i] == '&')
        {
            i++;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '-')) { i++; }
            return i;
        }
        redirectNext = true;
        return i;
    }

    private static int SkipWord(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t') { i++; }
        while (i < text.Length && text[i] is not (' ' or '\t' or '\n' or ';' or '|' or '&')) { i++; }
        return i;
    }

    // "$(…)" / "${…}" / "$name": copied into the word as written (it is dynamic anyway); nesting is followed.
    private static int DollarPart(string text, int i, StringBuilder into)
    {
        into.Append('$');
        i++;
        if (i < text.Length && text[i] is '(' or '{')
        {
            var open = text[i];
            var close = open == '(' ? ')' : '}';
            var depth = 0;
            while (i < text.Length)
            {
                var c = text[i];
                into.Append(c);
                i++;
                if (c == open) { depth++; }
                else if (c == close && --depth == 0) { break; }
            }
        }
        return i;
    }

    private static int Backticks(string text, int i, StringBuilder into)
    {
        var close = text.IndexOf('`', i + 1);
        var end = close < 0 ? text.Length : close + 1;
        into.Append(text, i, end - i);
        return end;
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
