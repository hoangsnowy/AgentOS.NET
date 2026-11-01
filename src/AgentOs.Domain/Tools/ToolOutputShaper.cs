// 2.7 — Tool-output shaping. Every tool result passes through DefaultToolGateway on its way back to the model;
// this is the step that keeps the signal and drops the bulk before it costs input tokens on the next turn:
//   1. Build/test logs (MSBuild, dotnet build/test, VSTest, xUnit / Microsoft.Testing.Platform) keep their
//      diagnostics (`error CS…`, analyzer ids), failed-test blocks with the first stack frames, summary lines and
//      a short head/tail window; restore/progress noise is dropped, repeated lines collapse into one with a count.
//      Logs wrapped in a JSON tool result (build_verifier's "output" field, MCP payloads) are shaped in place.
//   2. Anything still above the cap (Tools:MaxOutputTokens, ≈ chars/4) is cut head + tail around an explicit
//      `[… N lines omitted …]` marker, on line boundaries when possible and never between a surrogate pair.
// Single pass over the input (span line enumeration + SearchValues<string> marker scans, no regex), BCL-only so
// the remote-session gateway shapes identically. The evidence keeps the FULL output; only the model sees this.

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
/// <param name="LogShaped">True when build/test-log shaping removed lines.</param>
/// <param name="Capped">True when the size cap cut the output.</param>
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

    // JSON string fields shorter than this are never worth re-serialising the payload for.
    private const int MinJsonFieldChars = 1024;

    // Compiler / analyzer / MSBuild / NuGet / SDK diagnostics. MSBuild's canonical form is
    // `file(l,c): error CS1002: ; expected [proj]`; `CSC : error …` and `MSBUILD : error …` carry the same infix.
    private static readonly SearchValues<string> DiagnosticMarkers = SearchValues.Create(
        [": error ", ": warning ", "error CS", "warning CS", "error MSB", "warning MSB", "error NU", "warning NU",
         "error NETSDK", "warning NETSDK", "error IDE", "warning IDE", "error CA", "warning CA", "error RS", "warning RS"],
        StringComparison.Ordinal);

    // Build/test summary lines (MSBuild, VSTest, xUnit v3 / Microsoft.Testing.Platform).
    private static readonly SearchValues<string> SummaryMarkers = SearchValues.Create(
        ["build succeeded", "build failed", "warning(s)", "error(s)", "time elapsed", "passed!", "failed!",
         "total tests", "test run successful", "test run failed", "test run summary", "tests passed", "tests failed",
         "total:", "failed:", "succeeded:", "skipped:", "duration:"],
        StringComparison.OrdinalIgnoreCase);

    // Restore / progress / banner noise (prefix match on the trimmed line).
    private static readonly string[] NoisePrefixes =
    [
        "Determining projects to restore", "Restored ", "Restoring ", "Restore complete", "All projects are up-to-date",
        "MSBuild version", "Microsoft (R)", "Copyright (C)", "Build started", "Welcome to .NET", "Starting test execution",
        "A total of ", "Test run for ", "VSTest version", "Discovering", "Discovered", "Running tests from", "Passed ",
        "passed ", "Waiting for ", "Workload updates",
    ];

    // Signals that a text is a build/test log at all (only then does log shaping run).
    private static readonly SearchValues<string> LogSignals = SearchValues.Create(
        ["Build succeeded", "Build FAILED", "error CS", "warning CS", "error MSB", "Determining projects to restore",
         "Total tests:", "Test Run Failed", "Test Run Successful", "Test run summary", "[xUnit.net", "Failed!", "Passed!",
         "Stack Trace:"],
        StringComparison.Ordinal);

    private static readonly CompositeFormat OmittedFormat = CompositeFormat.Parse("[… {0} {1} omitted …]");
    private static readonly CompositeFormat RepeatFormat = CompositeFormat.Parse(" (×{0})");
    private static readonly CompositeFormat FramesFormat = CompositeFormat.Parse("    [… {0} more stack {1} …]");

    /// <summary>Clamp a configured token budget into the accepted range.</summary>
    public static int ClampTokens(int tokens) => Math.Clamp(tokens, MinMaxOutputTokens, MaxMaxOutputTokens);

    /// <summary>Shape <paramref name="output"/> for the model with a budget of <paramref name="maxTokens"/> (clamped).</summary>
    public static ShapedToolOutput Shape(string? output, int maxTokens)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new ShapedToolOutput(output ?? string.Empty, 0, LogShaped: false, Capped: false);
        }

        var maxChars = ClampTokens(maxTokens) * CharsPerToken;
        var text = output;
        var logShaped = false;

        if (output.Length >= MinJsonFieldChars && LooksLikeJsonObject(output))
        {
            if (TryShapeJson(output, maxChars, out var json))
            {
                text = json;
                logShaped = true;
            }
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
    /// Log shaping (step 1). Returns false — and leaves the text alone — when it is not a build/test log, is
    /// short enough to need no shaping, or shaping would not make it smaller.
    /// </summary>
    internal static bool TryShapeLog(string text, out string shaped)
    {
        shaped = text;
        var span = text.AsSpan();
        if (span.IndexOfAny(LogSignals) < 0)
        {
            return false;
        }

        // Vectorised newline count: the tail window needs to know where the last lines start.
        var totalLines = span.Count('\n') + (span.EndsWith("\n") ? 0 : 1);
        if (totalLines <= HeadLines + TailLines)
        {
            return false;
        }

        var tailStart = totalLines - TailLines;
        var kept = new List<KeptLine>(64);
        var diagnosticIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var diagnosticLookup = diagnosticIndex.GetAlternateLookup<ReadOnlySpan<char>>();

        var inFailure = false;
        var failureLines = 0;
        var framesKept = 0;
        var framesDropped = 0;
        var lineNo = -1;
        var offset = 0;

        foreach (var line in span.EnumerateLines())
        {
            lineNo++;
            var start = offset;
            offset += line.Length + 1;
            if (lineNo >= totalLines)
            {
                break; // trailing empty segment after the final newline
            }

            var trimmed = line.TrimStart();
            var window = lineNo < HeadLines || lineNo >= tailStart;

            // A failed-test block runs until the next test result, a summary, or a blank line.
            if (inFailure && (trimmed.IsEmpty || IsTestResultStart(trimmed) || trimmed.IndexOfAny(SummaryMarkers) >= 0))
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

            if (window || trimmed.IndexOfAny(SummaryMarkers) >= 0)
            {
                Add(kept, start, line.Length, lineNo, collapse: true, span);
            }
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
    /// Size cap (step 2): keep ~60% of the budget from the head and ~40% from the tail, cut on line boundaries when
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

    // JSON tool results (build_verifier, MCP): shape each long string field that is a log, in place, and give each
    // a share of the cap so the payload stays valid JSON. False when no field changed.
    private static bool TryShapeJson(string text, int maxChars, out string shaped)
    {
        shaped = text;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }
        if (root is not JsonObject obj)
        {
            return false;
        }

        var fields = new List<(string Name, string Value)>();
        var otherChars = text.Length;
        foreach (var (name, node) in obj)
        {
            if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String
                && v.GetValue<string>() is { Length: >= MinJsonFieldChars } s && s.Contains('\n', StringComparison.Ordinal))
            {
                fields.Add((name, s));
                otherChars -= s.Length;
            }
        }
        if (fields.Count == 0)
        {
            return false;
        }

        // JSON escaping grows newlines/quotes, so the per-field share leaves headroom; the whole-text cap still
        // backstops the result.
        var share = Math.Max(MinMaxOutputTokens, (maxChars - Math.Max(0, otherChars)) * 4 / 5 / fields.Count);
        var changed = false;
        foreach (var (name, value) in fields)
        {
            var next = TryShapeLog(value, out var log) ? log : value;
            if (next.Length > share)
            {
                next = Cap(next, share);
            }
            if (!ReferenceEquals(next, value))
            {
                obj[name] = next;
                changed = true;
            }
        }
        if (!changed)
        {
            return false;
        }

        shaped = obj.ToJsonString(JsonOptions);
        return shaped.Length < text.Length;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Keep the model-facing text readable (no \u escapes for '…', '×', non-ASCII paths).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool LooksLikeJsonObject(string text)
    {
        var t = text.AsSpan().Trim();
        return t.Length > 1 && t[0] == '{' && t[^1] == '}';
    }

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

    private static bool IsNoise(ReadOnlySpan<char> trimmed)
    {
        foreach (var prefix in NoisePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        // "  AgentOs.Domain -> /src/bin/Release/net10.0/AgentOs.Domain.dll" (project output line).
        return trimmed.Contains(" -> ", StringComparison.Ordinal);
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
}
