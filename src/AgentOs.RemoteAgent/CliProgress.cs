// What the AI is doing right now, in words a member understands — read from the CLI's own event stream.
//
// A CLI run on the member's computer was a black box: the Board showed "Working…" for 10–20 minutes and then a
// result. `claude -p --output-format stream-json --verbose` prints one JSON event per line, including every tool the
// agent calls; this maps those tool calls onto a handful of plain steps (Getting the code → Reading → Writing →
// Checking it builds → Running the tests → Saving → Sending), and pulls the final answer + usage out of the
// closing "result" event. Pure, so the mapping is unit-tested against real event lines.

using System;
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

    /// <summary>The final event of a stream-json run.</summary>
    internal sealed record Result(bool IsError, string Text, JsonElement? Usage, decimal? CostUsd);

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
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') { return (null, null); }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "result")
            {
                var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : string.Empty;
                var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                JsonElement? usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u.Clone() : null;
                decimal? cost = root.TryGetProperty("total_cost_usd", out var cu) && cu.ValueKind == JsonValueKind.Number && cu.TryGetDecimal(out var d) ? d : null;
                return (null, new Result(isError, text, usage, cost));
            }
            if (type != "assistant" || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }
            string? step = null;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "tool_use"
                    && part.TryGetProperty("name", out var name) && name.GetString() is { } tool)
                {
                    step = StepFor(tool, part.TryGetProperty("input", out var input) ? input : default) ?? step;
                }
            }
            return (step, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
