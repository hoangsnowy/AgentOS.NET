// E1.8 — text in and out of the trackers. Azure DevOps stores a work item's description and (older) comments as HTML;
// Jira Cloud's REST v3 speaks Atlassian Document Format (ADF), a JSON tree. The agents read plain text and AgentOS.NET
// writes Markdown, so this file holds the two small converters each way. Pure functions — unit-tested on fixtures.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOs.Modules.Integration.Sources;

internal static partial class TrackerText
{
    /// <summary>The longest ticket text kept from a tracker (a description pasted from a document can be huge).</summary>
    public const int MaxChars = 60_000;

    // Block-level tags end a line; <br> is a line break; <li> starts a bullet.
    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<\s*/\s*(?:p|div|h[1-6]|tr|blockquote|pre|ul|ol|table)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ManyNewlines();

    /// <summary>Plain text of an Azure DevOps HTML field (description, acceptance criteria, an HTML comment).</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) { return string.Empty; }
        var text = html.Length > MaxChars * 2 ? html[..(MaxChars * 2)] : html;
        text = LineBreak().Replace(text, "\n");
        text = BlockEnd().Replace(text, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(" ", " ", StringComparison.Ordinal).ReplaceLineEndings("\n");
        text = ManyNewlines().Replace(text, "\n\n").Trim();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    /// <summary>Plain text as Azure DevOps HTML (for a description field): escaped, line breaks kept.</summary>
    public static string TextToHtml(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : "<div>" + WebUtility.HtmlEncode(text.ReplaceLineEndings("\n")).Replace("\n", "<br/>", StringComparison.Ordinal) + "</div>";

    /// <summary>Plain text of an ADF document (Jira REST v3 <c>description</c> / comment <c>body</c>).</summary>
    public static string AdfToText(JsonElement adf)
    {
        if (adf.ValueKind == JsonValueKind.String) { return adf.GetString() ?? string.Empty; }   // REST v2-style plain text
        if (adf.ValueKind != JsonValueKind.Object) { return string.Empty; }
        var sb = new StringBuilder();
        Walk(adf, sb, depth: 0, inList: false);
        var text = ManyNewlines().Replace(sb.ToString().ReplaceLineEndings("\n"), "\n\n").Trim();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    private static void Walk(JsonElement node, StringBuilder sb, int depth, bool inList)
    {
        if (depth > 64 || sb.Length > MaxChars) { return; }   // a hostile / broken tree can't recurse forever
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "text":
                sb.Append(node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
                return;
            case "hardBreak":
                sb.Append('\n');
                return;
            case "mention" or "emoji" or "status" or "date":
                if (node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                {
                    sb.Append(attrs.TryGetProperty("text", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null);
                }
                return;
            case "inlineCard" or "blockCard":
                if (node.TryGetProperty("attrs", out var card) && card.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    sb.Append(url.GetString());
                }
                return;
            case "listItem":
                sb.Append("- ");
                break;
            case "codeBlock":
                sb.Append("```\n");
                break;
        }
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                Walk(child, sb, depth + 1, inList || type == "listItem");
            }
        }
        switch (type)
        {
            case "codeBlock":
                sb.Append("\n```\n");
                break;
            // Blocks are separated by a blank line (as written), a list item's paragraph by a line break.
            case "paragraph" or "heading" or "blockquote" or "rule" or "tableRow" or "panel":
                sb.Append(inList ? "\n" : "\n\n");
                break;
            case "listItem":
                if (sb.Length == 0 || sb[^1] != '\n') { sb.Append('\n'); }
                break;
            case "bulletList" or "orderedList":
                sb.Append('\n');
                break;
        }
    }

    /// <summary>Plain / Markdown text as an ADF document: a paragraph per blank-line-separated block, line breaks kept.
    /// (Markdown isn't rendered — Jira shows it as typed, which stays readable.)</summary>
    public static JsonObject TextToAdf(string? text)
    {
        var paragraphs = new JsonArray();
        foreach (var block in (text ?? string.Empty).ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inline = new JsonArray();
            var lines = block.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) { inline.Add(new JsonObject { ["type"] = "hardBreak" }); }
                if (lines[i].Length > 0) { inline.Add(new JsonObject { ["type"] = "text", ["text"] = lines[i] }); }
            }
            if (inline.Count > 0) { paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = inline }); }
        }
        if (paragraphs.Count == 0)
        {
            paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = " " }) });
        }
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = paragraphs };
    }

    /// <summary>A value quoted for WIQL / JQL: wrapped in <paramref name="quote"/>, the quote doubled (WIQL) or escaped (JQL).</summary>
    public static string Quote(string value, char quote, bool backslashEscape)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2).Append(quote);
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) { continue; }
            if (backslashEscape && ch is '\\' or '"' or '\'') { sb.Append('\\'); }
            else if (!backslashEscape && ch == quote) { sb.Append(quote); }
            sb.Append(ch);
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Tags / labels split from a tracker's list (ADO <c>"a; b"</c>), trimmed, empty dropped.</summary>
    public static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
