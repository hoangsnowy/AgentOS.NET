// AgentOs.Infrastructure/Agents/JsonExtractor.cs
// Phase 4 — Extract JSON from an LLM response (handle markdown fence, prose wrapping).

using System;
using System.Buffers;
using System.Text.Json;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>
/// Helper that extracts + deserializes JSON from LLM raw text. The LLM often wraps JSON in a markdown fence
/// or includes prose before/after — this helper is tolerant of the 3 common forms.
/// </summary>
public static class JsonExtractor
{
    /// <summary>
    /// Default JsonSerializerOptions: camelCase, case-insensitive, allow trailing comma.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Deserialize <paramref name="rawText"/> into <typeparamref name="T"/>.
    /// Attempt order:
    /// <list type="number">
    ///   <item>Parse directly.</item>
    ///   <item>Strip the markdown fence <c>```json...```</c> if present.</item>
    ///   <item>Find from the first <c>'{'</c> to the last <c>'}'</c> and parse the substring.</item>
    /// </list>
    /// Throws <see cref="LlmException"/> if all 3 fail.
    /// </summary>
    public static T Deserialize<T>(string rawText, string agentName)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // 1. Direct parse
        if (MayBeJson(rawText) && TryParse<T>(rawText, out var direct))
        {
            return direct!;
        }

        // 2. Strip markdown fence
        var stripped = StripFence(rawText);
        if (stripped is not null && MayBeJson(stripped) && TryParse<T>(stripped, out var afterFence))
        {
            return afterFence!;
        }

        // 3. Substring { ... }
        var sub = ExtractBraced(rawText);
        if (sub is not null && TryParse<T>(sub, out var afterSub))
        {
            return afterSub!;
        }

        throw new LlmException(
            $"{agentName}: response is not parseable JSON. Raw (truncated 500 chars): {Truncate(rawText, 500)}",
            agentName);
    }

    /// <summary>
    /// Returns the "cleaned" JSON string (strip fence + braced) from <paramref name="rawText"/>
    /// — used for schema validation before deserialization. Throws <see cref="LlmException"/>
    /// if no valid JSON object can be found.
    /// </summary>
    public static string ExtractJson(string rawText, string agentName)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (MayBeJson(rawText) && TryParseJsonNode(rawText))
        {
            return rawText;
        }

        var stripped = StripFence(rawText);
        if (stripped is not null && MayBeJson(stripped) && TryParseJsonNode(stripped))
        {
            return stripped;
        }

        var sub = ExtractBraced(rawText);
        if (sub is not null && TryParseJsonNode(sub))
        {
            return sub;
        }

        throw new LlmException(
            $"{agentName}: response is not parseable JSON. Raw (truncated 500 chars): {Truncate(rawText, 500)}",
            agentName);
    }

    // Characters a JSON text can start with (after whitespace): a value, or a comment (Deserialize skips
    // comments). A ```json fence or prose never does, and parsing it anyway throws and catches a JsonException —
    // ~10–20 µs per attempt on a large reply — so those attempts are skipped. They could never have succeeded.
    private static readonly SearchValues<char> JsonStart = SearchValues.Create(
        ['{', '[', '"', '-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 't', 'f', 'n', '/']);

    private static bool MayBeJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return !trimmed.IsEmpty && JsonStart.Contains(trimmed[0]);
    }

    private static bool TryParseJsonNode(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParse<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, DefaultOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Strip a ```json ... ``` or ``` ... ``` fence. Returns null if no fence is found.</summary>
    private static string? StripFence(string raw)
    {
        var start = raw.IndexOf("```", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        // Skip the optional language tag after ```.
        var newline = raw.IndexOf('\n', start);
        if (newline < 0)
        {
            return null;
        }

        var end = raw.IndexOf("```", newline, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        return raw.Substring(newline + 1, end - newline - 1).Trim();
    }

    /// <summary>Extract the substring from the first <c>{</c> to the last <c>}</c>. Returns null if not found.</summary>
    private static string? ExtractBraced(string raw)
    {
        var first = raw.IndexOf('{');
        var last = raw.LastIndexOf('}');
        if (first < 0 || last <= first)
        {
            return null;
        }

        return raw.Substring(first, last - first + 1);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}
