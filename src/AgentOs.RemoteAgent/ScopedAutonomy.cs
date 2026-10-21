// "Run on my machine" without YOLO. A CLI agent launched by the runner used to get one of two things: the
// profile's default flags (claude -p — every tool needs approval, which nobody can give non-interactively, so
// an issue-work run can't even clone), or REMOTE_AGENT_YOLO (--dangerously-skip-permissions — anything, anywhere
// on the box). Scoped autonomy is the middle: every CLI run gets its OWN empty working directory, file edits
// are auto-approved only inside it, and the shell is limited to the build / test / VCS commands a coding agent
// needs, with history-rewriting and credential-touching git commands denied outright.
//
//   The server asks for it per request (RemoteExecRequest.Autonomy = "scoped") only for a Board "Run on my
//   machine" session; every other request runs answer-only.
//   REMOTE_AGENT_AUTONOMY   "off" refuses scoped runs on this machine (default: honour them).
//                           REMOTE_AGENT_YOLO / REMOTE_AGENT_ARGS still win when set.
//   REMOTE_AGENT_WORKROOT   where the per-run directories live (default %LOCALAPPDATA%/AgentOS.NET/runs on
//                           Windows, ~/.cache/agentos-net/runs elsewhere)
//   REMOTE_AGENT_KEEP_RUNS  "1" keeps each run directory afterwards (debugging); default deletes it

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentOs.RemoteAgent;

/// <summary>The scoped-autonomy CLI flags and the per-run working directory.</summary>
internal static class ScopedAutonomy
{
    /// <summary>Shell commands claude may run without asking. File tools are deliberately NOT listed: a bare
    /// <c>Write</c>/<c>Edit</c>/<c>Read</c> allow rule applies to ANY path (a smoke run wrote a file into the home
    /// directory), whereas <c>--permission-mode acceptEdits</c> auto-approves edits inside the working directory only
    /// and reads there need no approval — anything outside needs one nobody can give, so it is denied. Reads outside
    /// it are NOT gated by the mode, hence the credential-store deny rules below, and no generic file-reading shell
    /// commands (cat/grep/find) or ad-hoc interpreters (node -e, npx) here: the Read/Grep/Glob tools cover the repo.
    /// The build tools themselves (dotnet, npm) run the repository's own code — this scopes the agent, it is not a
    /// sandbox for the repository.</summary>
    internal static readonly string[] ClaudeAllowed =
    [
        "Bash(git clone *)", "Bash(git checkout *)", "Bash(git switch *)", "Bash(git status*)", "Bash(git diff*)",
        "Bash(git log*)", "Bash(git show*)", "Bash(git add *)", "Bash(git commit *)", "Bash(git push -u origin *)",
        "Bash(git push origin *)", "Bash(git fetch*)", "Bash(git pull*)", "Bash(git rev-parse*)", "Bash(git branch*)",
        "Bash(gh repo clone *)", "Bash(gh pr create *)", "Bash(gh pr view*)", "Bash(gh issue view *)",
        "Bash(dotnet *)", "Bash(npm *)", "Bash(pnpm *)", "Bash(yarn *)",
        // The repository's own scripts (a reference decoder, codegen) — a path inside the checkout, never node -e. A live
        // run had to work out a reference decoder's verdicts by hand because node was not allowed at all.
        "Bash(node tools/*)", "Bash(node scripts/*)", "Bash(node ./*)",
        // Agentic Testing: end-to-end specs, waiting for a local app to answer, and its own background shells.
        "Bash(npx playwright *)", "Bash(npm exec playwright *)", "Bash(curl * http://localhost*)", "Bash(curl * https://localhost*)",
        "Bash(curl * http://127.0.0.1*)", "BashOutput", "KillShell",
        "Bash(cd *)", "Bash(ls*)", "Bash(pwd)", "Bash(wc *)", "Bash(mkdir *)",
    ];

    /// <summary>Denied even when an allow rule would match — deny rules take precedence in claude.</summary>
    internal static readonly string[] ClaudeDenied =
    [
        "Bash(git push --force*)", "Bash(git push -f*)", "Bash(git push * --force*)", "Bash(git push * -f*)",
        // A refspec can push onto the default branch or delete a remote branch. (claude reads a trailing ":*" as
        // its legacy prefix syntax, so the colon rules name the target instead of ending in ":*".)
        "Bash(git push --delete*)", "Bash(git push * --delete*)", "Bash(git push -d*)", "Bash(git push * -d *)",
        "Bash(git push *:main*)", "Bash(git push *:master*)", "Bash(git push *:refs/*)", "Bash(git push * :issue*)",
        "Bash(git push * --mirror*)", "Bash(git push * --all*)",
        "Bash(git config *)", "Bash(git remote *)", "Bash(git credential*)", "Bash(git reset --hard*)",
        "Bash(gh auth *)", "Bash(gh secret *)", "Bash(gh repo delete*)", "Bash(gh api *)",
        "WebFetch", "WebSearch",
        // Credential and secret stores. Reading outside the working directory needs no approval in claude, so these
        // are denied by path (a leading ~/ is the home directory). Not the whole of AppData: the run directories
        // live under AppData/Local, and a smoke run with that rule could not read its own checkout.
        "Read(~/.ssh/**)", "Read(~/.aws/**)", "Read(~/.azure/**)", "Read(~/.kube/**)", "Read(~/.docker/**)",
        "Read(~/.config/**)", "Read(~/.claude/**)", "Read(~/.claude.json)", "Read(~/.git-credentials)", "Read(~/.gitconfig)",
        "Read(~/.npmrc)", "Read(~/.nuget/NuGet/**)", "Read(~/.microsoft/**)", "Read(~/.local/share/**)",
        "Read(~/AppData/Roaming/**)", "Read(~/AppData/Local/Microsoft/**)", "Read(~/AppData/Local/Google/**)",
        "Read(~/AppData/Local/Packages/**)", "Read(~/AppData/Local/BraveSoftware/**)",
        "Read(~/.gnupg/**)", "Read(**/.env)", "Read(**/secrets.json)",
    ];

    /// <summary>Whether this run gets scoped autonomy: the request asked for it, and the machine's owner did not
    /// switch it off (<c>REMOTE_AGENT_AUTONOMY=off</c>). <c>REMOTE_AGENT_AUTONOMY=scoped</c> grants it to every
    /// request, including answer-only ones (the old opt-in, kept for scripts).</summary>
    internal static bool Grants(string? requested, string? machineSetting)
    {
        var setting = machineSetting?.Trim();
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase)) { return false; }
        if (string.Equals(setting, "scoped", StringComparison.OrdinalIgnoreCase)) { return true; }
        return string.Equals(requested?.Trim(), "scoped", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The flags appended to the profile's defaults: claude gets an explicit allow/deny list, codex
    /// its own workspace-write sandbox (it already confines writes to the working directory).</summary>
    internal static string[] ArgsFor(string command)
    {
        // Either separator: a Windows path must resolve the same on any OS (Path only splits on '/' off Windows).
        var name = Path.GetFileNameWithoutExtension(command[(command.LastIndexOfAny(['/', '\\']) + 1)..]);
        if (string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase))
        {
            // workspace-write blocks the network by default, and the run must clone and push. Not yet exercised
            // live on a Codex machine (the lab pairs Claude Code).
            return ["--sandbox", "workspace-write", "-c", "sandbox_workspace_write.network_access=true"];
        }
        return
        [
            "--permission-mode", "acceptEdits",
            "--allowedTools", .. WithPowerShell(ClaudeAllowed),
            "--disallowedTools", .. WithPowerShell(ClaudeDenied),
        ];
    }

    // On Windows claude also has a PowerShell tool; with Bash-only rules every PowerShell call was denied and the
    // agent retried each step through Bash (seen in a live stream). Mirror every shell rule onto it.
    internal static string[] WithPowerShell(string[] rules) =>
        [.. rules, .. rules.Where(r => r.StartsWith("Bash(", StringComparison.Ordinal)).Select(r => "PowerShell(" + r["Bash(".Length..])];

    /// <summary>Default root for run directories: <c>%LOCALAPPDATA%/AgentOS.NET/runs</c> on Windows, the XDG cache
    /// (<c>~/.cache/agentos-net/runs</c>) elsewhere — NOT LocalApplicationData there, which is ~/.local/share, a
    /// credential store the Read deny rules cover.</summary>
    internal static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS.NET", "runs");
        }
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Join(string.IsNullOrWhiteSpace(cache)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : cache, "agentos-net", "runs");
    }

    /// <summary>Create a fresh, empty directory for one run. The name comes from the server's request id only
    /// when it is a plain token (letters, digits, '-'), so a crafted id can't climb out of <paramref name="root"/>.</summary>
    internal static string CreateRunDirectory(string root, string requestId)
    {
        var safe = requestId.Length is > 0 and <= 64 && requestId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? requestId
            : Guid.NewGuid().ToString("N");
        var dir = Path.Join(Path.GetFullPath(root), $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Best-effort delete of a run directory. Git marks pack files read-only, which makes a plain
    /// recursive delete fail on Windows — clear the attribute first. Never throws.</summary>
    internal static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) { return; }
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* a process still holds a file — leave it for the next cleanup */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}

/// <summary>
/// Test data on this computer the agents may read (a corpus, fixtures too big or too private for the repo):
/// <c>REMOTE_AGENT_DATA="CHESS_LIBRARY_PATH=D:\Chess;OTHER=/data/x"</c>. Each entry becomes an environment variable of
/// the CLI run, and the agent is told the names (never the paths). Reading outside the run directory needs no approval
/// in claude; editing there does, which nobody can give — so the data is read-only to the agent.
/// </summary>
internal static class TestData
{
    internal static IReadOnlyList<(string Name, string Path)> Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) { return []; }
        var result = new List<(string, string)>();
        foreach (var entry in setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) { continue; }
            var name = entry[..eq].Trim();
            var path = entry[(eq + 1)..].Trim();
            // Only plain variable names and existing absolute folders/files — never PATH-like or relative entries.
            var validName = name.Length <= 64 && (char.IsAsciiLetterUpper(name[0]) || name[0] == '_')
                && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
                && name is not ("PATH" or "HOME" or "USERPROFILE" or "APPDATA" or "TEMP" or "TMP");
            if (validName && Path.IsPathRooted(path) && (Directory.Exists(path) || File.Exists(path)))
            {
                result.Add((name, path));
            }
        }
        return result;
    }

    /// <summary>The sentence added to the agent's instructions (names only).</summary>
    internal static string PromptNote(IReadOnlyList<(string Name, string Path)> data) => data.Count == 0
        ? string.Empty
        : "\n\nTest data available on this computer (read-only, as environment variables): "
          + string.Join(", ", data.Select(d => d.Name)) + ". Use them for the data checks the acceptance criteria ask for.";
}
