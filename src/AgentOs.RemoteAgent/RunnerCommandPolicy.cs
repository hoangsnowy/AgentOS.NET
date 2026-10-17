// Enterprise review E0.5 — what the paired runner will execute on the developer's machine for a server-driven
// `shell` tool call. Every segment of a command chain (&&, ||, ;, |, newlines) must start with an allowlisted
// program; command substitution ($(…), backticks) and output redirection to absolute paths are refused because
// they hide or widen what actually runs. Anything else needs the person at the runner console to approve it
// (once, or for the rest of the session); with no one there it is denied.
//
//   REMOTE_AGENT_ALLOW   comma list replacing the default allowlist; "+name" entries ADD to it; "*" allows all
//                        (explicit opt-out, e.g. a throwaway VM).

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.RemoteAgent;

/// <summary>Allowlist decision for a shell command.</summary>
internal sealed class RunnerCommandPolicy
{
    /// <summary>Build / test / VCS / read-only tools a coding agent needs; nothing that deletes or escalates.</summary>
    internal static readonly string[] DefaultAllowed =
    [
        "git", "gh", "dotnet", "msbuild", "nuget", "npm", "npx", "pnpm", "yarn", "node", "python", "python3", "pip", "pytest",
        "go", "cargo", "mvn", "gradle", "make", "cmake",
        "ls", "dir", "cat", "type", "head", "tail", "wc", "grep", "rg", "find", "findstr", "tree", "pwd", "echo", "cd",
        "mkdir", "touch", "cp", "copy", "mv", "move", "diff", "sort", "uniq",
    ];

    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    private readonly HashSet<string> _allowed;

    private RunnerCommandPolicy(HashSet<string> allowed, bool allowAll)
    {
        _allowed = allowed;
        AllowAll = allowAll;
    }

    /// <summary>True when the operator opted out of the allowlist ("*").</summary>
    public bool AllowAll { get; }

    /// <summary>The policy from <c>REMOTE_AGENT_ALLOW</c> (null/blank = defaults).</summary>
    public static RunnerCommandPolicy FromSetting(string? setting)
    {
        var allowed = new HashSet<string>(DefaultAllowed, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(setting))
        {
            return new RunnerCommandPolicy(allowed, allowAll: false);
        }
        var entries = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Contains("*", StringComparer.Ordinal))
        {
            return new RunnerCommandPolicy(allowed, allowAll: true);
        }
        if (entries.Any(e => !e.StartsWith('+')))
        {
            allowed.Clear();   // a plain list REPLACES the defaults
        }
        foreach (var e in entries)
        {
            allowed.Add(e.TrimStart('+'));
        }
        return new RunnerCommandPolicy(allowed, allowAll: false);
    }

    /// <summary>Null when <paramref name="command"/> may run unattended; otherwise why it needs approval.</summary>
    public string? Check(string command)
    {
        if (AllowAll) { return null; }
        if (string.IsNullOrWhiteSpace(command)) { return "empty command"; }
        if (command.Contains("$(", StringComparison.Ordinal) || command.Contains('`', StringComparison.Ordinal))
        {
            return "command substitution hides what runs";
        }
        var refused = Segments(command).Select(Program).FirstOrDefault(p => p.Length > 0 && !_allowed.Contains(p));
        return refused is null ? null : $"'{refused}' is not on the runner allowlist";
    }

    /// <summary>Split on shell chaining operators and newlines (quotes are not interpreted — conservative).</summary>
    internal static IEnumerable<string> Segments(string command)
        => command.Split(["&&", "||", ";", "|", "\r", "\n", "&"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The program a segment runs: first word, without a path or a Windows extension, minus leading
    /// environment assignments (<c>FOO=1 dotnet test</c>).</summary>
    internal static string Program(string segment)
    {
        var rest = segment.TrimStart();
        // Leading environment assignments (FOO=1 BAR=2 cmd …).
        while (rest.Length > 0 && rest[0] is not ('"' or '\'' or '-'))
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            var first = space < 0 ? rest : rest[..space];
            if (!first.Contains('=', StringComparison.Ordinal)) { break; }
            rest = space < 0 ? string.Empty : rest[(space + 1)..].TrimStart();
        }
        if (rest.Length == 0) { return string.Empty; }
        string word;
        if (rest[0] is '"' or '\'')
        {
            // A quoted program path may contain spaces ("C:\Program Files\nodejs\npm.cmd").
            var close = rest.IndexOf(rest[0], 1);
            word = close < 0 ? rest[1..] : rest[1..close];
        }
        else
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            word = space < 0 ? rest : rest[..space];
        }
        var slash = word.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) { word = word[(slash + 1)..]; }
        foreach (var ext in WindowsExtensions)
        {
            if (word.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { return word[..^ext.Length]; }
        }
        return word;
    }
}
