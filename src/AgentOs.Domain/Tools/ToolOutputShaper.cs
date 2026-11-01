// 2.7 — Tool-output shaping. A tool result headed back to the MODEL (the LLM tool loop, AIToolFunction) passes
// through here so a huge result keeps its signal and drops its bulk before it costs input tokens on the next turn.
// Data consumers (a workflow Tool node feeding ${…}, ExtractJson, Transform) always get the full output.
//   0. A result that fits the budget (Tools:MaxOutputTokens, ≈ chars/4) is returned unchanged — shaping never
//      rewrites a small result (a source file that mentions "Build FAILED", a short log the model asked for).
//   1. Build/test logs (MSBuild, dotnet build/test, VSTest, xUnit / Microsoft.Testing.Platform) — recognised by
//      line-anchored structure, not a keyword anywhere — keep their diagnostics (`error CS…`, analyzer ids),
//      failed-test blocks with the first stack frames, summary lines and a short head/tail window; restore/progress
//      noise is dropped, repeated lines collapse into one with a count.
//   2. JSON results (build_verifier, MCP payloads — objects or arrays, at any depth) are shaped in place: long
//      string values get the log pass and a share of the budget, over-long arrays lose trailing elements behind a
//      "[… N more items omitted …]" marker element, and the result stays valid JSON whenever that fits.
//   3. Anything still above the budget is cut as text, head + tail around an explicit `[… N lines omitted …]`
//      marker, on line boundaries when possible and never between a surrogate pair. (JSON that could not be fitted
//      structurally — e.g. an object with thousands of keys — is cut this way too and is then no longer valid JSON.)
// Lines end at '\n' only (one trailing '\r' is part of a CRLF terminator); a lone '\r' (progress redraws), U+2028/9,
// U+0085 or '\f' is an ordinary character, so offsets and line counts never drift apart.
// Single pass over a log (span slicing + SearchValues<string> marker scans, no regex), BCL-only so every gateway
// shapes identically. The evidence keeps the FULL output; only the model sees this.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOs.Domain.Tools;

/// <summary>What <see cref="ToolOutputShaper.Shape"/> did to one tool output.</summary>
/// <param name="Text">The text the model receives.</param>
/// <param name="OriginalChars">Length of the full tool output.</param>
/// <param name="LogShaped">True when the structural pass (build/test log or JSON) changed the text before the final cap.</param>
/// <param name="Capped">True when the final text cap cut the output.</param>
public readonly record struct ShapedToolOutput(string Text, int OriginalChars, bool LogShaped, bool Capped)
{
    /// <summary>Length of <see cref="Text"/>.</summary>
    public int ShapedChars => Text.Length;

    /// <summary>Characters kept away from the model (never negative).</summary>
    public int SavedChars => Math.Max(0, OriginalChars - ShapedChars);
}

/// <summary>Shapes tool output before it reaches the model (see file header).</summary>
public static class ToolOutputShaper
{
    /// <summary>Setting key: the per-call budget for a tool result, in tokens (≈ chars / 4).</summary>
    public const string MaxOutputTokensKey = "Tools:MaxOutputTokens";

    /// <summary>Default <see cref="MaxOutputTokensKey"/>.</summary>
    public const int DefaultMaxOutputTokens = 4000;

    /// <summary>Lowest accepted <see cref="MaxOutputTokensKey"/>.</summary>
    public const int MinMaxOutputTokens = 500;

    /// <summary>Highest accepted <see cref="MaxOutputTokensKey"/>.</summary>
    public const int MaxMaxOutputTokens = 50000;

    /// <summary>Chars per token used to approximate the budget (the usual English/code average).</summary>
    public const int CharsPerToken = 4;

    /// <summary>Raw lines always kept at the start of a log.</summary>
    internal const int HeadLines = 8;

    /// <summary>Raw lines always kept at the end of a log.</summary>
    internal const int TailLines = 12;

    /// <summary>Stack frames kept per failed test.</summary>
    internal const int StackFramesPerFailure = 5;

    /// <summary>Lines kept per failed-test block (message + frames) before the rest is dropped.</summary>
    internal const int MaxFailureBlockLines = 40;

    // JSON string values shorter than this are left alone (arrays of them are trimmed as arrays instead).
    private const int MinJsonFieldChars = 1024;

    // Smallest budget share (in chars) a long JSON string value is cut down to.
    private const int MinJsonFieldShareChars = 256;

    // Array-trimming rounds before a JSON result that still does not fit falls back to the text cap.
    private const int MaxJsonArrayTrims = 4;

    // Compiler / analyzer / MSBuild / NuGet / SDK diagnostics. MSBuild's canonical form is
    // `file(l,c): error CS1002: ; expected [proj]`; `CSC : error …` and `MSBUILD : error …` carry the same infix.
    private static readonly SearchValues<string> DiagnosticMarkers = SearchValues.Create(
        [": error ", ": warning ", "error CS", "warning CS", "error MSB", "warning MSB", "error NU", "warning NU",
         "error NETSDK", "warning NETSDK", "error IDE", "warning IDE", "error CA", "warning CA", "error RS", "warning RS"],
        StringComparison.Ordinal);

    // Diagnostics in a shape only a build tool prints (a source location or a tool origin before the severity):
    // structural evidence that the text is a log, unlike ": error " in prose.
    private static readonly SearchValues<string> CanonicalDiagnosticMarkers = SearchValues.Create(
        ["): error ", "): warning ", "CSC : error ", "CSC : warning ", "MSBUILD : error ", "MSBUILD : warning "],
        StringComparison.Ordinal);

    // Cheap pre-filter: a text containing none of these anywhere is not a build/test log.
    private static readonly SearchValues<string> LogSignals = SearchValues.Create(
        ["Build succeeded", "Build FAILED", "error CS", "warning CS", "error MSB", "Determining projects to restore",
         "Total tests:", "Test Run Failed", "Test Run Successful", "Test run summary", "[xUnit.net", "Failed!", "Passed!",
         "Stack Trace:", "): error ", "): warning "],
        StringComparison.Ordinal);

    // Structural evidence: a (trimmed) line that STARTS with one of these is a build/test tool talking.
    private static readonly string[] LogLinePrefixes =
    [
        "Build succeeded", "Build FAILED", "Build failed", "Determining projects to restore", "Total tests:",
        "Test Run Failed", "Test Run Successful", "Test Run Aborted", "Test run summary:", "[xUnit.net", "Failed!",
        "Passed!", "Time Elapsed", "All projects are up-to-date",
    ];

    // Build/test summary lines (MSBuild, VSTest, xUnit v3 / Microsoft.Testing.Platform), matched at the start of the
    // trimmed line so an assertion message such as "Expected total: 3" neither ends a failed-test block nor is kept
    // as a summary. "    5 Error(s)" / "    2 Warning(s)" are matched at the end.
    private static readonly string[] SummaryPrefixes =
    [
        "build succeeded", "build failed", "time elapsed", "passed!", "failed!", "total tests", "total time",
        "test run successful", "test run failed", "test run aborted", "test run summary", "tests passed", "tests failed",
        "total:", "failed:", "passed:", "succeeded:", "skipped:", "duration:",
    ];

    // Restore / progress / banner noise (prefix match on the trimmed line).
    private static readonly string[] NoisePrefixes =
    [
        "Determining projects to restore", "Restored ", "Restoring ", "Restore complete", "All projects are up-to-date",
        "MSBuild version", "Microsoft (R)", "Copyright (C)", "Build started", "Welcome to .NET", "Starting test execution",
        "A total of ", "Test run for ", "VSTest version", "Discovering", "Discovered", "Running tests from", "Passed ",
        "passed ", "Waiting for ", "Workload updates",
    ];

    private static readonly CompositeFormat OmittedFormat = CompositeFormat.Parse("[… {0} {1} omitted …]");
    private static readonly CompositeFormat RepeatFormat = CompositeFormat.Parse(" (×{0})");
    private static readonly CompositeFormat FramesFormat = CompositeFormat.Parse("    [… {0} more stack {1} …]");
    private static readonly CompositeFormat ItemsFormat = CompositeFormat.Parse("[… {0} more {1} omitted …]");

    /// <summary>Clamp a configured token budget into the accepted range.</summary>
    public static int ClampTokens(int tokens) => Math.Clamp(tokens, MinMaxOutputTokens, MaxMaxOutputTokens);

    /// <summary>The budget in chars for <paramref name="maxTokens"/> (clamped).</summary>
    public static int MaxCharsFor(int maxTokens) => ClampTokens(maxTokens) * CharsPerToken;

    /// <summary>Shape <paramref name="output"/> for the model with a budget of <paramref name="maxTokens"/> (clamped).</summary>
    public static ShapedToolOutput Shape(string? output, int maxTokens)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new ShapedToolOutput(output ?? string.Empty, 0, LogShaped: false, Capped: false);
        }

        var maxChars = MaxCharsFor(maxTokens);
        if (output.Length <= maxChars)
        {
            // Within budget: nothing to save, so nothing is rewritten.
            return new ShapedToolOutput(output, output.Length, LogShaped: false, Capped: false);
        }

        var text = output;
        var logShaped = false;
        if (LooksLikeJson(output) && TryShapeJson(output, maxChars, out var json))
        {
            text = json;
            logShaped = true;
        }
        else if (TryShapeLog(output, out var log))
        {
            text = log;
            logShaped = true;
        }

        var capped = false;
        if (text.Length > maxChars)
        {
            text = Cap(text, maxChars);
            capped = true;
        }

        return new ShapedToolOutput(text, output.Length, logShaped, capped);
    }

    /// <summary>
    /// The plain size cap alone (step 3) — the fallback when the structural passes cannot run. Never throws for a
    /// non-null input.
    /// </summary>
    public static ShapedToolOutput CapOnly(string? output, int maxTokens)
    {
        var text = output ?? string.Empty;
        var maxChars = MaxCharsFor(maxTokens);
        return text.Length <= maxChars
            ? new ShapedToolOutput(text, text.Length, LogShaped: false, Capped: false)
            : new ShapedToolOutput(Cap(text, maxChars), text.Length, LogShaped: false, Capped: true);
    }

    /// <summary>
    /// Log shaping (step 1). Returns false — and leaves the text alone — when it is not a build/test log (no
    /// line-anchored structure), is short enough to need no shaping, or shaping would not make it smaller.
    /// </summary>
    internal static bool TryShapeLog(string text, out string shaped)
    {
        shaped = text;
        var span = text.AsSpan();
        if (span.IndexOfAny(LogSignals) < 0)
        {
            return false;
        }

        // Lines end at '\n' (the loop below splits the same way), so this vectorised count is the exact number of
        // segments the loop visits: the tail window needs to know where the last lines start.
        var totalLines = span.Count('\n') + (span.EndsWith("\n") ? 0 : 1);
        if (totalLines <= HeadLines + TailLines)
        {
            return false;
        }

        var tailStart = totalLines - TailLines;
        var kept = new List<KeptLine>(64);
        var diagnosticIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var diagnosticLookup = diagnosticIndex.GetAlternateLookup<ReadOnlySpan<char>>();

        var structural = false;
        var inFailure = false;
        var failureLines = 0;
        var framesKept = 0;
        var framesDropped = 0;
        var lineNo = -1;
        var pos = 0;

        while (pos < span.Length)
        {
            var start = pos;
            var rest = span[pos..];
            var nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            pos += nl < 0 ? rest.Length : nl + 1;
            lineNo++;
            // CRLF: the '\r' belongs to the terminator. Every other control/separator char stays in the line.
            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var trimmed = line.TrimStart();
            var window = lineNo < HeadLines || lineNo >= tailStart;
            structural = structural || IsStructuralLogLine(trimmed, line);

            // A failed-test block runs until the next test result, a summary, or a blank line.
            if (inFailure && (trimmed.IsEmpty || IsTestResultStart(trimmed) || IsSummaryLine(trimmed)))
            {
                CloseFailure(kept, ref framesDropped, lineNo);
                inFailure = false;
            }

            if (IsFailedTestStart(trimmed))
            {
                inFailure = true;
                failureLines = 0;
                framesKept = 0;
                framesDropped = 0;
                Add(kept, start, line.Length, lineNo);
                continue;
            }

            if (inFailure)
            {
                if (trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    if (framesKept < StackFramesPerFailure)
                    {
                        framesKept++;
                        Add(kept, start, line.Length, lineNo);
                    }
                    else
                    {
                        framesDropped++;
                    }
                }
                else if (++failureLines <= MaxFailureBlockLines)
                {
                    Add(kept, start, line.Length, lineNo);
                }
                continue;
            }

            if (line.IndexOfAny(DiagnosticMarkers) >= 0)
            {
                // MSBuild repeats every diagnostic in its closing summary: keep the first, count the rest.
                if (diagnosticLookup.TryGetValue(trimmed, out var at))
                {
                    kept[at] = kept[at] with { Count = kept[at].Count + 1 };
                    continue;
                }
                diagnosticLookup[trimmed] = kept.Count;
                Add(kept, start, line.Length, lineNo);
                continue;
            }

            // Restore/progress noise (and blank lines) go even inside the head/tail window — that is what a build
            // log opens with.
            if (trimmed.IsEmpty || IsNoise(trimmed))
            {
                continue;
            }

            if (window || IsSummaryLine(trimmed))
            {
                Add(kept, start, line.Length, lineNo, collapse: true, span);
            }
        }

        if (!structural)
        {
            return false; // a keyword somewhere, but no line a build/test tool would print
        }

        if (inFailure)
        {
            CloseFailure(kept, ref framesDropped, totalLines);
        }

        var sb = new StringBuilder(Math.Min(text.Length, 8192));
        var previous = -1;
        foreach (var k in kept)
        {
            if (k.Start < 0)
            {
                // Synthetic "more stack frames" line: it accounts for the dropped frames itself.
                sb.AppendFormat(CultureInfo.InvariantCulture, FramesFormat, k.Count, k.Count == 1 ? "frame" : "frames").Append('\n');
                previous = Math.Max(previous, k.LineNo);
                continue;
            }
            var gap = k.LineNo - previous - 1;
            if (gap > 0)
            {
                AppendOmitted(sb, gap).Append('\n');
            }
            sb.Append(span.Slice(k.Start, k.Length));
            if (k.Count > 1)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, RepeatFormat, k.Count);
            }
            sb.Append('\n');
            previous = k.LineNo + k.Lines - 1;
        }
        var trailingGap = totalLines - previous - 1;
        if (trailingGap > 0)
        {
            AppendOmitted(sb, trailingGap).Append('\n');
        }
        if (!span.EndsWith("\n") && sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
        }

        if (sb.Length >= text.Length)
        {
            return false;
        }
        shaped = sb.ToString();
        return true;
    }

    /// <summary>
    /// Size cap (step 3): keep ~60% of the budget from the head and ~40% from the tail, cut on line boundaries when
    /// one is near, and put an explicit marker in between. The result never exceeds <paramref name="maxChars"/> and
    /// never splits a surrogate pair.
    /// </summary>
    internal static string Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var span = text.AsSpan();
        const int markerReserve = 48; // "[… 9999999 lines omitted …]\n" + a newline after the head
        var budget = Math.Max(2, maxChars - markerReserve);
        var headBudget = budget * 3 / 5;
        var tailBudget = budget - headBudget;

        // Head: up to and including the last newline inside the budget, unless that throws away over half of it.
        var headEnd = headBudget;
        var nl = span[..headBudget].LastIndexOf('\n');
        if (nl >= headBudget / 2)
        {
            headEnd = nl + 1;
        }
        else if (char.IsHighSurrogate(span[headEnd - 1]))
        {
            headEnd--;
        }

        // Tail: from the first line start inside the budget, with the same "not more than half" rule.
        var tailStart = text.Length - tailBudget;
        var nextNl = span[tailStart..].IndexOf('\n');
        if (nextNl >= 0 && nextNl <= tailBudget / 2 && tailStart + nextNl + 1 < text.Length)
        {
            tailStart += nextNl + 1;
        }
        else if (char.IsLowSurrogate(span[tailStart]))
        {
            tailStart++;
        }

        var omitted = span[headEnd..tailStart];
        var omittedLines = omitted.Count('\n') + (omitted.EndsWith("\n") ? 0 : 1);

        var sb = new StringBuilder(maxChars);
        sb.Append(span[..headEnd]);
        if (headEnd > 0 && span[headEnd - 1] != '\n')
        {
            sb.Append('\n');
        }
        AppendOmitted(sb, omittedLines).Append('\n');
        sb.Append(span[tailStart..]);
        return sb.ToString();
    }

    // JSON results (build_verifier, MCP): shape in place so the payload stays valid JSON. False when the text is not
    // JSON System.Text.Json accepts (malformed, duplicate property names, too deep) or nothing got smaller — the
    // caller then treats it as plain text.
    private static bool TryShapeJson(string text, int maxChars, out string shaped)
    {
        shaped = text;
        try
        {
            if (JsonNode.Parse(text) is not { } root || root is JsonValue)
            {
                return false;
            }
            return TryShapeJsonTree(root, text, maxChars, out shaped);
        }
        // JsonException: malformed. ArgumentException: a duplicate property name, which System.Text.Json reports
        // only when the object materialises. InvalidOperationException: an unexpected node kind.
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            shaped = text;
            return false;
        }
    }

    private static bool TryShapeJsonTree(JsonNode root, string text, int maxChars, out string shaped)
    {
        var leaves = new List<JsonLeaf>();
        var arrays = new List<JsonArray>();
        CollectJson(root, leaves, arrays);

        // 1. Long string values get the log pass and an equal share of what the rest of the payload (measured by
        //    serialising it with those values emptied) leaves over. The share keeps headroom for JSON escaping;
        //    step 2 corrects once if that was not enough.
        var share = 0;
        string json;
        if (leaves.Count > 0)
        {
            for (var i = 0; i < leaves.Count; i++)
            {
                if (TryShapeLog(leaves[i].Value, out var log))
                {
                    leaves[i] = leaves[i] with { Value = log };
                }
            }
            var skeleton = ApplyLeaves(root, leaves, share: 0).Length;
            share = Math.Max(MinJsonFieldShareChars, (maxChars - skeleton) * 4 / 5 / leaves.Count);
            json = ApplyLeaves(root, leaves, share);
        }
        else
        {
            json = root.ToJsonString(JsonOptions);
        }

        // 2. Escaping grew the strings past their share: shrink the share once, in proportion.
        if (json.Length > maxChars && leaves.Count > 0)
        {
            share = (int)Math.Max(MinJsonFieldShareChars, (long)share * maxChars / json.Length * 9 / 10);
            json = ApplyLeaves(root, leaves, share);
        }

        // 3. Still too long: drop trailing elements of the largest arrays behind a marker element.
        for (var round = 0; json.Length > maxChars && round < MaxJsonArrayTrims; round++)
        {
            if (!TrimLargestArray(arrays, json.Length - maxChars))
            {
                break;
            }
            json = root.ToJsonString(JsonOptions);
        }

        shaped = json;
        return json.Length < text.Length;
    }

    // Depth-first over the tree (System.Text.Json caps parsing depth at 64, so the stack stays small).
    private static void CollectJson(JsonNode root, List<JsonLeaf> leaves, List<JsonArray> arrays)
    {
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj)
                    {
                        Visit(child, obj, name, -1, leaves, stack);
                    }
                    break;
                case JsonArray arr:
                    if (arr.Count > 1)
                    {
                        arrays.Add(arr);
                    }
                    for (var i = 0; i < arr.Count; i++)
                    {
                        Visit(arr[i], arr, null, i, leaves, stack);
                    }
                    break;
            }
        }
    }

    private static void Visit(JsonNode? child, JsonNode parent, string? name, int index, List<JsonLeaf> leaves, Stack<JsonNode> stack)
    {
        if (child is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: >= MinJsonFieldChars } s)
            {
                leaves.Add(new JsonLeaf(parent, name, index, s));
            }
        }
        else if (child is not null)
        {
            stack.Push(child);
        }
    }

    // Write every leaf back cut to `share` chars (0 = emptied, to measure the rest of the payload) and serialise.
    private static string ApplyLeaves(JsonNode root, List<JsonLeaf> leaves, int share)
    {
        foreach (var leaf in leaves)
        {
            var next = share <= 0 ? string.Empty : leaf.Value.Length > share ? Cap(leaf.Value, share) : leaf.Value;
            if (leaf.Parent is JsonObject obj)
            {
                obj[leaf.Name!] = next;
            }
            else if (leaf.Parent is JsonArray arr)
            {
                arr[leaf.Index] = next;
            }
        }
        return root.ToJsonString(JsonOptions);
    }

    // Trim the largest not-yet-trimmed array by at least `excess` chars (keeping its first element) and append a
    // "[… N more items omitted …]" string element. False when no array can be trimmed.
    private static bool TrimLargestArray(List<JsonArray> arrays, int excess)
    {
        JsonArray? largest = null;
        var largestChars = 0;
        var largestAt = -1;
        for (var i = 0; i < arrays.Count; i++)
        {
            var chars = arrays[i].ToJsonString(JsonOptions).Length;
            if (arrays[i].Count > 1 && chars > largestChars)
            {
                (largest, largestChars, largestAt) = (arrays[i], chars, i);
            }
        }
        if (largest is null)
        {
            return false;
        }
        arrays.RemoveAt(largestAt);

        const int markerChars = 40;
        var dropped = 0;
        var droppedChars = 0;
        while (largest.Count > 1 && droppedChars < excess + markerChars)
        {
            var last = largest[^1];
            droppedChars += (last?.ToJsonString(JsonOptions).Length ?? 4) + 1; // + the separating comma
            largest.RemoveAt(largest.Count - 1);
            dropped++;
        }
        largest.Add(string.Format(CultureInfo.InvariantCulture, ItemsFormat, dropped, dropped == 1 ? "item" : "items"));
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Keep the model-facing text readable (no \u escapes for '…', '×', non-ASCII paths).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool LooksLikeJson(string text)
    {
        var t = text.AsSpan().Trim();
        return t.Length > 1 && ((t[0] == '{' && t[^1] == '}') || (t[0] == '[' && t[^1] == ']'));
    }

    // Structural evidence that a line comes from a build/test tool.
    private static bool IsStructuralLogLine(ReadOnlySpan<char> trimmed, ReadOnlySpan<char> line) =>
        StartsWithAny(trimmed, LogLinePrefixes, StringComparison.Ordinal)
        || line.IndexOfAny(CanonicalDiagnosticMarkers) >= 0
        || IsTestResultStart(trimmed);

    private static bool IsSummaryLine(ReadOnlySpan<char> trimmed) =>
        StartsWithAny(trimmed, SummaryPrefixes, StringComparison.OrdinalIgnoreCase)
        || trimmed.EndsWith("Warning(s)", StringComparison.OrdinalIgnoreCase)
        || trimmed.EndsWith("Error(s)", StringComparison.OrdinalIgnoreCase);

    // VSTest "  Failed Ns.Class.Method [5 ms]", MTP "failed Ns.Class.Method (5ms)", xUnit console "… [FAIL]".
    private static bool IsFailedTestStart(ReadOnlySpan<char> trimmed) =>
        IsTestResult(trimmed, "Failed ", "failed ") || trimmed.EndsWith("[FAIL]", StringComparison.Ordinal);

    private static bool IsTestResultStart(ReadOnlySpan<char> trimmed) =>
        IsFailedTestStart(trimmed)
        || IsTestResult(trimmed, "Passed ", "passed ") || IsTestResult(trimmed, "Skipped ", "skipped ")
        || trimmed.EndsWith("[PASS]", StringComparison.Ordinal) || trimmed.EndsWith("[SKIP]", StringComparison.Ordinal);

    // "<Verb> <Fully.Qualified.Name…> [duration]" or "(duration)": the name is dotted and the line ends with the
    // duration, which rules out prose such as "Failed to load prune package data …" and the "Failed: 1" summaries.
    private static bool IsTestResult(ReadOnlySpan<char> trimmed, string verb, string lowerVerb)
    {
        if (!(trimmed.StartsWith(verb, StringComparison.Ordinal) || trimmed.StartsWith(lowerVerb, StringComparison.Ordinal))
            || trimmed.Length <= verb.Length || trimmed[^1] is not (']' or ')'))
        {
            return false;
        }
        var name = trimmed[verb.Length..];
        var end = name.IndexOfAny(' ', '(');
        var first = end < 0 ? name : name[..end];
        return first.Length > 2 && first.Contains('.') && first[0] != '.';
    }

    // "  AgentOs.Domain -> /src/bin/Release/net10.0/AgentOs.Domain.dll" (project output line) or a noise prefix.
    private static bool IsNoise(ReadOnlySpan<char> trimmed) =>
        StartsWithAny(trimmed, NoisePrefixes, StringComparison.Ordinal) || trimmed.Contains(" -> ", StringComparison.Ordinal);

    private static bool StartsWithAny(ReadOnlySpan<char> trimmed, string[] prefixes, StringComparison comparison)
    {
        for (var i = 0; i < prefixes.Length; i++)
        {
            if (trimmed.StartsWith(prefixes[i], comparison))
            {
                return true;
            }
        }
        return false;
    }

    private static void CloseFailure(List<KeptLine> kept, ref int framesDropped, int lineNo)
    {
        if (framesDropped > 0)
        {
            // Start = -1 marks a synthetic line; LineNo keeps ordering for gap accounting (it covers no input line).
            kept.Add(new KeptLine(-1, 0, lineNo - 1, framesDropped, Lines: 0));
            framesDropped = 0;
        }
    }

    private static void Add(List<KeptLine> kept, int start, int length, int lineNo) =>
        kept.Add(new KeptLine(start, length, lineNo, 1, Lines: 1));

    // Window/summary lines: an exact repeat of the previous kept line (progress spam) collapses into a count, and
    // the repeat's line numbers are absorbed so no "omitted" marker is emitted for them.
    private static void Add(List<KeptLine> kept, int start, int length, int lineNo, bool collapse, ReadOnlySpan<char> text)
    {
        if (collapse && kept.Count > 0)
        {
            var last = kept[^1];
            if (last.Start >= 0 && last.LineNo + last.Lines == lineNo
                && text.Slice(last.Start, last.Length).SequenceEqual(text.Slice(start, length)))
            {
                kept[^1] = last with { Count = last.Count + 1, Lines = last.Lines + 1 };
                return;
            }
        }
        Add(kept, start, length, lineNo);
    }

    private static StringBuilder AppendOmitted(StringBuilder sb, int lines) =>
        sb.AppendFormat(CultureInfo.InvariantCulture, OmittedFormat, lines, lines == 1 ? "line" : "lines");

    /// <summary>One kept input line (or a synthetic frames marker when <see cref="Start"/> is negative).</summary>
    private readonly record struct KeptLine(int Start, int Length, int LineNo, int Count, int Lines);

    /// <summary>A long JSON string value and where it lives (an object property or an array slot).</summary>
    private readonly record struct JsonLeaf(JsonNode Parent, string? Name, int Index, string Value);
}
