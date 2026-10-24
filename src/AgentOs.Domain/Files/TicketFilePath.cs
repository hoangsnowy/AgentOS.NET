// AgentOs.Domain/Files/TicketFilePath.cs
// Ticket files (ADR-0015) — the one rule for a stored file's path. A person names the path (a file's name, or its place
// in an uploaded folder); the server stores it, and the runner on a member's computer writes the file at
// <run dir>/.agentos/files/<path>. Both sides apply THIS function, so a path the server accepted is the path the runner
// re-checks (the runner links this file, BCL-only). Relative, forward slashes, no "." / ".." segment, no drive, no
// colon (drive letters, NTFS streams), no characters Windows refuses, no reserved device names, ≤ 200 characters.

using System;

namespace AgentOs.Domain.Files;

/// <summary>Normalises and validates the relative path of a ticket file.</summary>
public static class TicketFilePath
{
    /// <summary>The longest normalised path (characters).</summary>
    public const int MaxLength = 200;

    /// <summary>The longest single folder or file name (characters).</summary>
    public const int MaxSegmentLength = 120;

    // Windows refuses these in a name; ':' also covers drive letters and alternate data streams.
    private static readonly System.Buffers.SearchValues<char> Forbidden = System.Buffers.SearchValues.Create("<>:\"|?*");

    // Reserved device names on Windows (with or without an extension: "con.txt" is the console too).
    private static readonly System.Collections.Frozen.FrozenSet<string> Reserved = System.Collections.Frozen.FrozenSet.ToFrozenSet(
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise <paramref name="raw"/> (backslashes → '/', surrounding whitespace trimmed, repeated slashes collapsed)
    /// and validate it. Returns false with a plain-language <paramref name="error"/> when the path can't be used.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The file needs a name.";
            return false;
        }
        var text = raw.Trim().Replace('\\', '/');
        if (text.StartsWith('/') || text.StartsWith('~'))
        {
            error = "Use a path inside the ticket's files, not one that starts at the top of a disk or your home folder.";
            return false;
        }
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "The file needs a name.";
            return false;
        }
        foreach (var segment in segments)
        {
            if (SegmentProblem(segment) is { } problem)
            {
                error = problem;
                return false;
            }
        }
        var joined = string.Join('/', segments);
        if (joined.Length > MaxLength)
        {
            error = $"The path is too long ({joined.Length} characters; at most {MaxLength}).";
            return false;
        }
        normalized = joined;
        error = null;
        return true;
    }

    /// <summary>
    /// The uniqueness key of a normalised path: invariant lower case. "Logo.png" and "LOGO.PNG" are one file on Windows
    /// and macOS, so a scope holds at most one of them (the unique index is on this key, not on the path).
    /// </summary>
    public static string Key(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
#pragma warning disable CA1308 // a storage key, compared only with itself — lower case by decision (ADR-0015), never shown
        return normalizedPath.ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>The normalised path, or null when it can't be used.</summary>
    public static string? Normalize(string? raw) => TryNormalize(raw, out var path, out _) ? path : null;

    private static string? SegmentProblem(string segment)
    {
        if (segment is "." or "..")
        {
            return "A path can't contain \".\" or \"..\" — it must stay inside the ticket's files.";
        }
        if (segment.Length > MaxSegmentLength)
        {
            return $"A folder or file name is too long (at most {MaxSegmentLength} characters).";
        }
        if (segment.Trim().Length == 0 || segment[^1] is '.' or ' ' || segment[0] == ' ')
        {
            return "A folder or file name can't start with a space or end with a space or a dot.";
        }
        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                return "A path can't contain control characters.";
            }
        }
        if (segment.AsSpan().IndexOfAny(Forbidden) >= 0)
        {
            return "A path can't contain any of these characters: < > : \" | ? *";
        }
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? segment : segment[..dot];
        if (Reserved.Contains(stem.TrimEnd()))
        {
            return $"\"{segment}\" is a reserved name on Windows — rename it.";
        }
        return null;
    }
}
