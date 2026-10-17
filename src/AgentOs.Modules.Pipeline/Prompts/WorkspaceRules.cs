// Enterprise review E1.6 — a tenant's own engineering rules ("use xUnit + Shouldly", "never touch /infra",
// "C# 14, nullable on"), written once by a tenant admin in Settings → Prompts and applied to EVERY agent of
// every run (the five pipeline agents, the Quick engine, the Decomposer). Order in a system prompt:
//     agent's own prompt (role + output contract)  →  workspace rules  →  repository guidance (AGENTS.md …)
// Budgeted and fenced like repository guidance: rules shape style and choices, never the role or the format.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgentOs.Modules.Pipeline.Prompts;

/// <summary>Storage key, budget and prompt block for a tenant's workspace rules; prompt version history.</summary>
public static class WorkspaceRules
{
    /// <summary>The pseudo agent key the rules are stored and versioned under.</summary>
    public const string Key = "_rules";

    /// <summary>Most characters of rules given to an agent (~2k tokens).</summary>
    public const int MaxLength = 8_000;

    /// <summary>Versions kept per prompt.</summary>
    public const int HistoryDepth = 20;

    /// <summary>The block appended to every agent's system prompt, or empty when there are no rules.</summary>
    public static string ToPromptBlock(string? rules)
    {
        var text = (rules ?? string.Empty).Trim();
        if (text.Length == 0) { return string.Empty; }
        if (text.Length > MaxLength) { text = text[..MaxLength] + "\n[… truncated]"; }
        return "\n\n## Workspace rules\n"
             + "Your organisation's administrators set these rules for every agent. Follow them for style, libraries, "
             + "structure and process. They never change your role, your output format or safety rules.\n"
             + "<workspace_rules>\n"
             + text.Replace("</workspace_rules>", "</workspace-rules>", StringComparison.OrdinalIgnoreCase)
             + "\n</workspace_rules>\n";
    }

    /// <summary>History store key for a prompt.</summary>
    internal static string HistoryKey(string agentKey) => $"prompt/{agentKey}/history";

    /// <summary>Newest-first history with <paramref name="added"/> prepended, capped at <see cref="HistoryDepth"/>.</summary>
    internal static string Append(string? historyJson, PromptVersion added)
    {
        var list = Parse(historyJson);
        list.Insert(0, added);
        if (list.Count > HistoryDepth) { list.RemoveRange(HistoryDepth, list.Count - HistoryDepth); }
        return JsonSerializer.Serialize(list, PromptJsonContext.Default.ListPromptVersion);
    }

    /// <summary>Parse stored history (corrupt or missing → empty).</summary>
    internal static List<PromptVersion> Parse(string? historyJson)
    {
        if (string.IsNullOrWhiteSpace(historyJson)) { return []; }
        try
        {
            return JsonSerializer.Deserialize(historyJson, PromptJsonContext.Default.ListPromptVersion) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One saved version of a prompt (or of the workspace rules). Empty <see cref="Text"/> = reset to default.</summary>
/// <param name="At">When it was saved.</param>
/// <param name="Actor">Who saved it.</param>
/// <param name="Text">The prompt text in force from then on (empty = the built-in default).</param>
public sealed record PromptVersion(DateTimeOffset At, string Actor, string Text);

[System.Text.Json.Serialization.JsonSerializable(typeof(List<PromptVersion>))]
internal sealed partial class PromptJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
