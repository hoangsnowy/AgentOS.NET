// A skill must not widen the run's own permissions (docs/adr/0019, threat model R-12). Claude Code honours a skill's
// `allowed-tools` frontmatter: the tools it lists are pre-approved for the turn that invokes the skill — in `claude -p`
// that turn is the whole run — and skill edits are picked up within the session. Agents may edit the project's skills
// (.claude/skills/**, ADR-0018 addendum), so without a check a run (or a prompt-injected ticket) could write a skill with
// `allowed-tools: Bash(curl *)`, invoke it, and get commands pre-approved that the runner's allow list deliberately
// leaves out. A skill's `hooks` frontmatter is the same kind of grant (a command that runs on a tool event, unprompted).
//
// So the guard refuses, in depth:
//   - a Write / Edit / MultiEdit under .claude/skills/** whose RESULTING file declares a grant (Edit/MultiEdit: the edits
//     applied to the current file; unreadable → refused when the new text names a grant) — removing one is fine;
//   - the ticket-files helper (`sh .agentos/bin/copy-in <file> <dst>`) copying a grant-declaring file into a skill (the
//     helper script checks it too, in case the hook can't run);
//   - a shell command that puts content into a skill (a heredoc, echo / printf, sed -i, tee …) when its text names a
//     grant, or copies (cp / mv / install / Copy-Item) a file or folder that declares one — shell edits without a grant
//     keep working, the lab writes skills that way too;
//   - invoking (the Skill tool) a project skill that declares a grant and was changed by the run (not committed, or
//     written by it): covers a file a build script or the repository's own code wrote. A committed, unchanged skill
//     came with the repository and is reviewed like its code.
// The PermissionRequest approval of a skill write applies the same content rule.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOs.RemoteAgent;

internal static partial class ScopedWriteGuard
{
    /// <summary>Ends every skill-grant refusal (so <see cref="Run"/> doesn't add the run-folder sentence).</summary>
    internal const string SkillGrantNote = " Skills may not pre-approve tools (or install hooks) on AgentOS.NET runners: "
        + "drop that line and say in the skill's instructions what it needs instead.";

    private const int MaxSkillFiles = 500;

    // ── what a skill file declares ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The grant a skill file's frontmatter declares — its key as written (<c>allowed-tools</c>,
    /// <c>Allowed_Tools</c>, <c>hooks</c>…) — or null. Any case, <c>-</c> / <c>_</c> / no separator, quoted keys, YAML
    /// escapes, block or flow mappings; a key the scan can't read (an alias or a complex key) counts as a grant.</summary>
    internal static string? SkillGrant(string? content)
    {
        if (string.IsNullOrEmpty(content) || Frontmatter(content) is not { } frontmatter) { return null; }
        var text = DecodeYamlEscapes(frontmatter);
        if (AllowedToolsToken().Match(text) is { Success: true } allowed) { return allowed.Value; }
        if (HooksKey().Match(text) is { Success: true } hooks) { return hooks.Groups["key"].Value; }
        if (OpaqueYamlKey().Match(text) is { Success: true } opaque) { return opaque.Value.Trim(); }
        return null;
    }

    /// <summary>Whether a piece of text (an edit's new text, when the file can't be read) names a grant at all.</summary>
    internal static bool MentionsGrant(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        var decoded = DecodeYamlEscapes(text);
        return AllowedToolsToken().IsMatch(decoded) || LooseHooksKey().IsMatch(decoded);
    }

    // The YAML frontmatter: from a first "---" line (after a BOM / blank lines) to the next "---" or "..." line; an
    // unclosed one runs to the end. No frontmatter → null.
    private static string? Frontmatter(string content)
    {
        var lines = content.TrimStart('﻿').Split('\n');
        var i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) { i++; }
        if (i >= lines.Length || lines[i].Trim() != "---") { return null; }
        var sb = new StringBuilder();
        for (i++; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() is "---" or "...") { break; }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    // \uXXXX / \UXXXXXXXX / \xXX in double-quoted scalars, and a backslash line continuation ("allowed-\⏎tools").
    private static string DecodeYamlEscapes(string text) =>
        YamlEscape().Replace(YamlContinuation().Replace(text, string.Empty), m =>
        {
            var hex = m.Groups["hex"].Value;
            var code = Convert.ToInt32(hex, 16);
            return code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : m.Value;
        });

    // ── the file tools ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Why a Write / Edit / MultiEdit of a project skill file is refused — the file it would leave declares a
    /// grant — or null (not a skill file, or no grant).</summary>
    internal static string? CheckSkillGrant(string tool, JsonElement toolInput, string? path, string cwd, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.IsNullOrWhiteSpace(path) || Resolve(path, cwd, ctx.TempDir) is not { } full || !IsProjectSkill(full, ctx.RunDir))
        {
            return null;
        }
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var edits = new List<(string Old, string New, bool All)>();
        switch (tool)
        {
            case "Write":
                return SkillGrant(Str(toolInput, "content")) is { } key ? SkillGrantRefusal(path, key) : null;
            case "Edit":
                edits.Add((Str(toolInput, "old_string") ?? string.Empty, Str(toolInput, "new_string") ?? string.Empty, ReplaceAll(toolInput)));
                break;
            case "MultiEdit":
                if (toolInput.TryGetProperty("edits", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    edits.AddRange(list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                        .Select(e => (Str(e, "old_string") ?? string.Empty, Str(e, "new_string") ?? string.Empty, ReplaceAll(e))));
                }
                break;
            default:
                return null;
        }
        // The file as the edits would leave it; when it can't be read (or an edit doesn't apply), the new text alone decides.
        var current = ReadSkillFile(full);
        var result = current;
        foreach (var (oldText, newText, all) in edits)
        {
            if (result is null) { break; }
            if (oldText.Length == 0) { result = result.Length == 0 ? newText : null; continue; }
            var at = result.IndexOf(oldText, StringComparison.Ordinal);
            if (at < 0) { result = null; continue; }
            result = all ? result.Replace(oldText, newText, StringComparison.Ordinal)
                : string.Concat(result.AsSpan(0, at), newText, result.AsSpan(at + oldText.Length));
        }
        if (result is not null)
        {
            return SkillGrant(result) is { } key ? SkillGrantRefusal(path, key) : null;
        }
        return edits.Exists(e => MentionsGrant(e.New))
            ? SkillGrantRefusal(path, "allowed-tools") + " (The skill file couldn't be read, so the edit's new text decided.)"
            : null;

        static bool ReplaceAll(JsonElement e) => e.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True;
    }

    // The file's text; "" when it doesn't exist yet; null when it can't be read.
    private static string? ReadSkillFile(string full)
    {
        try
        {
            return File.Exists(full) ? File.ReadAllText(full) : Directory.Exists(full) ? null : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    private static string SkillGrantRefusal(string shown, string key) =>
        $"'{shown}' would declare '{key}' in a project skill's frontmatter — a skill can't pre-approve tools for the run "
        + $"that invokes it.{SkillGrantNote}";

    // ── shell writes and the copy-in helper ──────────────────────────────────────────────────────────────────────

    /// <summary>A shell command that puts content into a project skill is refused when its text names a grant (a heredoc,
    /// echo, sed -i …): the content can't be told apart from the command, so the command decides.</summary>
    internal static string? ShellSkillWrite(string full, string shown, GuardContext ctx) =>
        ctx.CommandMentionsGrant && InSkillsTree(full, ctx.RunDir)
            ? $"'{shown}' is a project skill and the command writes 'allowed-tools' (or hooks) into it." + SkillGrantNote
            : null;

    private static readonly HashSet<string> CopyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cp", "copy", "mv", "move", "install", "copy-item", "cpi", "move-item", "mi",
    };

    /// <summary>cp / mv / install / Copy-Item into a project skill: refused when a source file (or a file in a source
    /// folder) declares a grant.</summary>
    private static string? CopyIntoSkill(string program, List<ShellWords.Word> operands, string cwd, GuardContext ctx)
    {
        if (!CopyCommands.Contains(program)) { return null; }
        var resolved = operands.Select(o => Expand(o, cwd, ctx) is { } t ? Resolve(t, cwd, ctx.TempDir) : null).ToList();
        if (!resolved.Exists(r => r is not null && InSkillsTree(r, ctx.RunDir))) { return null; }
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i] is not { } source || InSkillsTree(source, ctx.RunDir)) { continue; }
            if (GrantIn(source) is { } key)
            {
                return $"'{program} {operands[i].Text}': it declares '{key}' and can't go into a project skill.{SkillGrantNote}";
            }
        }
        return null;
    }

    // The grant a file — or any text file below a folder (bounded) — declares.
    private static string? GrantIn(string path)
    {
        if (File.Exists(path)) { return ReadSkillFile(path) is { } content ? SkillGrant(content) : null; }
        if (!Directory.Exists(path)) { return null; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options).Take(MaxSkillFiles))
            {
                if (new FileInfo(file).Length <= 1_000_000 && ReadSkillFile(file) is { } content && SkillGrant(content) is { } key) { return key; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return "allowed-tools";
        }
        return null;
    }

    // The skills folder itself or anything below it (`tar -C .claude/skills` writes below it).
    private static bool InSkillsTree(string full, string runDir)
    {
        var segments = RelativeSegments(full, runDir);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals(ClaudeFolder, StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals(SkillsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary><c>sh .agentos/bin/copy-in &lt;file&gt; &lt;destination&gt;</c>: refuse copying a grant-declaring file into a
    /// project skill (or a file that can't be read; computed paths can't be checked at all).</summary>
    internal static string? CheckCopyIn(IReadOnlyList<ShellWords.Word> args, string cwd, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(ctx);
        if (args.Count != 2) { return null; } // the helper itself refuses
        if (Expand(args[0], cwd, ctx) is not { } src || Expand(args[1], cwd, ctx) is not { } dst)
        {
            return "copy-in gets a computed path, so what it copies can't be checked — name the file and the destination.";
        }
        if (Resolve(dst, cwd, ctx.TempDir) is not { } dstFull || !IsProjectSkill(dstFull, ctx.RunDir)) { return null; }
        if (Resolve(src, cwd, ctx.TempDir) is not { } srcFull || ReadSkillFile(srcFull) is not { } content)
        {
            return $"copy-in: '{args[0].Text}' can't be read, so what it would put into the skill can't be checked.";
        }
        return SkillGrant(content) is { } key
            ? $"copy-in: '{args[0].Text}' declares '{key}' in its frontmatter and can't be copied into a project skill "
              + $"('{args[1].Text}').{SkillGrantNote}"
            : null;
    }

    // ── invoking a skill ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Skill tool: refuse invoking a project skill that declares a grant and was changed by this run (git
    /// doesn't see it committed and unchanged, or the run wrote it).</summary>
    internal static string? CheckSkillUse(string? name, string cwd, GuardContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        var wanted = name.Trim().TrimStart('/');
        if (wanted.Contains(':', StringComparison.Ordinal)) { return null; } // a plugin's skill, not the project's
        foreach (var file in ProjectSkillFiles(cwd, ctx))
        {
            if (ReadSkillFile(file) is not { Length: > 0 } content) { continue; }
            var folder = Path.GetFileName(Path.GetDirectoryName(file));
            if (!string.Equals(folder, wanted, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(SkillName(content), wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (SkillGrant(content) is not { } key) { continue; }
            if (ctx.ScriptStateOf(file) is ScriptState.Clean && !ctx.WrittenByRun(file)) { continue; }
            return $"the skill '{wanted}' declares '{key}' and was changed in this run — a skill can't pre-approve tools for "
                + $"the run that invokes it.{SkillGrantNote}";
        }
        return null;
    }

    private static string? SkillName(string content) =>
        Frontmatter(content) is { } fm && SkillNameLine().Match(fm) is { Success: true } m ? m.Groups["name"].Value.Trim() : null;

    // SKILL.md files below the .claude/skills folders that apply here: those of cwd and its parents (inside the run folder)
    // and those of the run folder's own subfolders (the checkout). Links are followed — Claude Code follows them too.
    private static IEnumerable<string> ProjectSkillFiles(string cwd, GuardContext ctx)
    {
        var roots = new List<string>();
        var here = Resolve(cwd, ctx.RunDir, ctx.TempDir);
        while (here is not null && IsInside(here, ctx.RunDir))
        {
            roots.Add(here);
            here = Path.GetDirectoryName(here);
        }
        try
        {
            roots.AddRange(Directory.EnumerateDirectories(ctx.RunDir).Take(64));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = 0, IgnoreInaccessible = true,
        };
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var skills = Path.Join(root, ClaudeFolder, SkillsFolder);
            if (!Directory.Exists(skills)) { continue; }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(skills, "SKILL.md", options).Take(MaxSkillFiles).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                continue;
            }
            foreach (var f in files.Where(seen.Add)) { yield return f; }
        }
    }

    // "allowed-tools" / "allowed_tools" / "allowedTools", any case, anywhere in the frontmatter.
    [GeneratedRegex(@"allowed[-_]?tools", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedToolsToken();

    // A "hooks" key: at a line start, after "{" / "," (flow mapping) or "? " (complex key); quoted or not.
    [GeneratedRegex(@"(?:^|[{,?])\s*(?<key>[""']?hooks[""']?)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex HooksKey();

    // "hooks:" anywhere (a command's text: echo 'hooks: …' >> SKILL.md).
    [GeneratedRegex(@"\bhooks[""'\\]*\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooseHooksKey();

    // A key the scan can't read: an alias used as a key (*a: …), a merge key (<<: *a) or a complex key (? …).
    [GeneratedRegex(@"(?:^|[{,])\s*(?:\*[^\s:,{}]+\s*:|<<\s*:|\?(?:\s|$))", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex OpaqueYamlKey();

    [GeneratedRegex(@"\\(?:u(?<hex>[0-9A-Fa-f]{4})|U(?<hex>[0-9A-Fa-f]{8})|x(?<hex>[0-9A-Fa-f]{2}))", RegexOptions.CultureInvariant)]
    private static partial Regex YamlEscape();

    [GeneratedRegex(@"\\\r?\n[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex YamlContinuation();

    [GeneratedRegex(@"^\s*[""']?name[""']?\s*:\s*[""']?(?<name>[^""'\r\n#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex SkillNameLine();
}
