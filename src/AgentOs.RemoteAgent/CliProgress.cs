// What the AI is doing right now, in words a member understands — read from the CLI's own event stream.
//
// A CLI run on the member's computer was a black box: the Board showed "Working…" for 10–20 minutes and then a
// result. `claude -p --output-format stream-json --verbose` prints one JSON event per line, including every tool the
// agent calls; this maps those tool calls onto a handful of plain steps (Getting the code → Reading → Writing →
// Checking it builds → Running the tests → Saving → Sending), and pulls the final answer + usage out of the
// closing "result" event. Pure, so the mapping is unit-tested against real event lines.
//
// Governance: the same single pass also reads every tool call (tool_use), its outcome (tool_result, is_error, a
// permission refusal) and the closing result's permission_denials, which CliToolCalls turns into evidence rows the
// server records (Settings › Activity log › On a member's computer). Only a bounded, redacted summary of each call's
// input leaves the computer (SecretRedactor) — never file contents or tool output.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AgentOs.RemoteAgent;

/// <summary>Maps CLI stream events to plain progress steps.</summary>
internal static class CliProgress
{
    /// <summary>The steps, in the order a coding task normally passes through them (keys travel over the wire).</summary>
    internal static readonly (string Key, string Label)[] Steps =
    [
        ("getting-code", "Getting the code"),
        ("reading", "Reading the code"),
        ("writing", "Writing the code"),
        ("building", "Checking it builds"),
        ("testing", "Running the tests"),
        ("saving", "Saving the change"),
        ("sending", "Sending the change for review"),
    ];

    /// <summary>A tool call the CLI started: its id, the tool and a bounded, redacted summary of its input.</summary>
    internal sealed record ToolUse(string Id, string Tool, string Summary);

    /// <summary>The outcome of a tool call: an error, or refused by the CLI's permissions (it never ran).</summary>
    internal sealed record ToolOutcome(string ToolUseId, bool IsError, bool Denied);

    /// <summary>Everything one stream-json line carries.</summary>
    internal sealed record Event(string? Step, Result? Final, IReadOnlyList<ToolUse> ToolUses, IReadOnlyList<ToolOutcome> Outcomes)
    {
        internal static readonly Event None = new(null, null, [], []);
    }

    /// <summary>The final event of a stream-json run. <paramref name="PermissionDenials"/> lists the tool calls the CLI's
    /// permissions refused during the run (its <c>permission_denials</c>).</summary>
    internal sealed record Result(bool IsError, string Text, JsonElement? Usage, decimal? CostUsd, IReadOnlyList<ToolUse>? PermissionDenials = null)
    {
        /// <summary>The run's real token usage, normalised so cache reads/writes are a subset of the input (Claude's
        /// usage reports input_tokens WITHOUT them). Null when the CLI reported none.</summary>
        public (int Input, int Output, int CacheRead, int CacheWrite)? Tokens()
        {
            if (Usage is not { } u) { return null; }
            int Get(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            var fresh = Get("input_tokens");
            var read = Get("cache_read_input_tokens");
            var write = Get("cache_creation_input_tokens");
            var output = Get("output_tokens");
            return fresh + read + write + output == 0 ? null : (fresh + read + write, output, read, write);
        }
    }

    /// <summary>The step a tool call belongs to, or null when it doesn't move the task along (e.g. a `pwd`).</summary>
    internal static string? StepFor(string tool, JsonElement input)
    {
        switch (tool)
        {
            case "Read" or "Glob" or "Grep" or "LS":
                return "reading";
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit":
                return "writing";
            case "Bash" or "PowerShell":
                var command = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? string.Empty
                    : string.Empty;
                return StepForCommand(command);
            default:
                return null;
        }
    }

    /// <summary>The step a shell command belongs to. Order matters: "dotnet test" also builds, "git push" after a commit.</summary>
    internal static string? StepForCommand(string command)
    {
        static bool Has(string s, string needle) => s.Contains(needle, StringComparison.OrdinalIgnoreCase);
        if (Has(command, "git clone") || Has(command, "gh repo clone")) { return "getting-code"; }
        if (Has(command, "git push") || Has(command, "gh pr create")) { return "sending"; }
        if (Has(command, "git commit")) { return "saving"; }
        if (Has(command, "dotnet test") || Has(command, "npm test") || Has(command, "npm run test") || Has(command, "vitest")
            || Has(command, "playwright test") || Has(command, "pytest") || Has(command, "go test") || Has(command, "cargo test"))
        {
            return "testing";
        }
        if (Has(command, "dotnet build") || Has(command, "npm run build") || Has(command, "npm run lint") || Has(command, "tsc")
            || Has(command, "dotnet restore") || Has(command, "npm ci") || Has(command, "npm install") || Has(command, "cargo build"))
        {
            return "building";
        }
        return null;
    }

    /// <summary>The plain label of a step key (unknown keys pass through, so an older server still shows something).</summary>
    internal static string Label(string key)
    {
        foreach (var (k, label) in Steps)
        {
            if (k == key) { return label; }
        }
        return key;
    }

    /// <summary>Read one stream-json line: the step of each tool call it carries (last one wins), or its final result.</summary>
    internal static (string? Step, Result? Final) Parse(string line)
    {
        var e = Read(line);
        return (e.Step, e.Final);
    }

    /// <summary>Read one stream-json line in one pass: its plain step, its final result, the tool calls it starts
    /// (assistant <c>tool_use</c>) and the outcomes it reports (user <c>tool_result</c>).</summary>
    internal static Event Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') { return Event.None; }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return Event.None; }
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type == "result")
            {
                var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : string.Empty;
                var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                JsonElement? usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u.Clone() : null;
                decimal? cost = root.TryGetProperty("total_cost_usd", out var cu) && cu.ValueKind == JsonValueKind.Number && cu.TryGetDecimal(out var d) ? d : null;
                return new Event(null, new Result(isError, text, usage, cost, PermissionDenials(root)), [], []);
            }
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return Event.None;
            }
            if (type == "assistant")
            {
                // The last tool call of the event that moves the task along wins.
                string? step = null;
                var uses = new List<ToolUse>();
                foreach (var part in content.EnumerateArray())
                {
                    if (!IsPart(part, "tool_use") || !part.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } tool)
                    {
                        continue;
                    }
                    var input = part.TryGetProperty("input", out var i) ? i : default;
                    step = StepFor(tool, input) ?? step;
                    if (part.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } useId)
                    {
                        uses.Add(new ToolUse(useId, tool, Summarize(tool, input)));
                    }
                }
                return new Event(step, null, uses, []);
            }
            if (type == "user")
            {
                var outcomes = new List<ToolOutcome>();
                foreach (var part in content.EnumerateArray())
                {
                    if (!IsPart(part, "tool_result") || !part.TryGetProperty("tool_use_id", out var id) || id.GetString() is not { Length: > 0 } useId)
                    {
                        continue;
                    }
                    var isError = part.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    outcomes.Add(new ToolOutcome(useId, isError, isError && IsPermissionRefusal(part)));
                }
                return new Event(null, null, [], outcomes);
            }
            return Event.None;
        }
        catch (JsonException)
        {
            return Event.None;
        }
    }

    private static bool IsPart(JsonElement part, string type) =>
        part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var pt) && pt.ValueKind == JsonValueKind.String && pt.GetString() == type;

    // The closing result's permission_denials: [{"tool_name":"Bash","tool_use_id":"toolu_…","tool_input":{…}}].
    private static List<ToolUse>? PermissionDenials(JsonElement root)
    {
        if (!root.TryGetProperty("permission_denials", out var list) || list.ValueKind != JsonValueKind.Array) { return null; }
        var denials = new List<ToolUse>();
        foreach (var d in list.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) { continue; }
            var tool = d.TryGetProperty("tool_name", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() ?? "unknown" : "unknown";
            var id = d.TryGetProperty("tool_use_id", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() ?? string.Empty : string.Empty;
            denials.Add(new ToolUse(id, tool, Summarize(tool, d.TryGetProperty("tool_input", out var input) ? input : default)));
        }
        return denials;
    }

    // A tool_result the CLI's permissions produced instead of running the tool: "Claude requested permissions to use
    // Bash, but you haven't granted it yet." (no allow rule matched) or "Permission to use Bash with command … has been
    // denied." (a deny rule). A tool's own "Permission denied" (a file mode) is an ordinary error, not a refusal.
    internal static bool IsPermissionRefusal(JsonElement toolResult)
    {
        var text = ResultText(toolResult);
        return text.StartsWith("Claude requested permissions to use", StringComparison.Ordinal)
            || text.Contains("haven't granted it yet", StringComparison.Ordinal)
            || (text.StartsWith("Permission to use ", StringComparison.Ordinal) && text.Contains("has been denied", StringComparison.Ordinal));
    }

    // The leading text of a tool_result: a string, or the first text block of an array. Only read here — never sent.
    private static string ResultText(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var c)) { return string.Empty; }
        if (c.ValueKind == JsonValueKind.String) { return (c.GetString() ?? string.Empty).TrimStart(); }
        if (c.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in c.EnumerateArray())
            {
                if (IsPart(block, "text") && block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    return (tx.GetString() ?? string.Empty).TrimStart();
                }
            }
        }
        return string.Empty;
    }

    /// <summary>What a tool call did, in one bounded, redacted line: the shell command, the file's path, the search
    /// pattern — never a file's contents (Write's <c>content</c>, Edit's strings) or a tool's output. An unknown tool
    /// (an MCP server's) is summarised by its input's field NAMES only.</summary>
    internal static string Summarize(string tool, JsonElement input)
    {
        string Str(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
        static string With(string main, string label, string extra) => extra.Length == 0 ? main : $"{main} {label} {extra}";
        var text = tool switch
        {
            "Bash" or "PowerShell" => Str("command"),
            "Read" or "Write" or "Edit" or "MultiEdit" => Str("file_path"),
            "NotebookEdit" or "NotebookRead" => Str("notebook_path"),
            "Glob" => With(Str("pattern"), "in", Str("path")),
            "Grep" => With(With(Str("pattern"), "in", Str("path")), "files", Str("glob")),
            "LS" => Str("path"),
            "WebFetch" => Str("url"),
            "WebSearch" => Str("query"),
            "Task" or "Agent" => With(Str("description"), "·", Str("subagent_type")),
            "BashOutput" => Str("bash_id"),
            "KillShell" or "KillBash" => Str("shell_id"),
            "TodoWrite" => input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array
                ? $"{todos.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)} to-do item(s)"
                : string.Empty,
            _ => input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any()
                ? "fields: " + string.Join(", ", input.EnumerateObject().Take(10).Select(p => p.Name))
                : string.Empty,
        };
        return AgentOs.Domain.Tools.SecretRedactor.Summary(text);
    }
}
