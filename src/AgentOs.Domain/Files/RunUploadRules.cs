// AgentOs.Domain/Files/RunUploadRules.cs
// Design gate (ADR-0016) — the one rule for a file a member's computer sends BACK to the server after a run: the
// Testing agent's Playwright screenshots. The runner picks the files the agent listed (paths inside its run folder),
// re-checks each one here, and uploads it in pieces; the server checks every piece again with this same code before a
// byte reaches the tenant's file store. The runner links this file (BCL-only), exactly like TicketFilePath.
//
// Rules: a relative path that passes TicketFilePath, an image extension (png / jpg / jpeg / webp) whose first bytes
// really are that image, never one of the ticket's own files (.agentos/files), and the caps below — per file, per run,
// and per piece. Screenshots are data: stored, shown, handed to the Designer agent — never executed, never committed.

using System;
using System.Collections.Frozen;

namespace AgentOs.Domain.Files;

/// <summary>What a run may send back, and how big.</summary>
/// <param name="MaxFiles">How many files one run may send.</param>
/// <param name="MaxFileBytes">The largest single file.</param>
/// <param name="MaxTotalBytes">All files of one run together.</param>
public sealed record RunCollect(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)
{
    /// <summary>The design gate's caps: 16 screenshots (4 screens × phone/desktop × light/dark), 5 MB each, 40 MB a run.</summary>
    public static RunCollect Screenshots { get; } = new(RunUploadRules.MaxFiles, RunUploadRules.MaxFileBytes, RunUploadRules.MaxTotalBytes);
}

/// <summary>One piece of a file a runner sends back (<c>UploadRunFile</c> on the hub).</summary>
/// <param name="Path">The file's path relative to the run folder (checked with <see cref="RunUploadRules.TryCheck"/>).</param>
/// <param name="Offset">Where this piece starts; pieces arrive in order.</param>
/// <param name="Data">At most <see cref="RunUploadRules.ChunkBytes"/> bytes.</param>
/// <param name="Last">True on the file's last piece.</param>
public sealed record RunUploadChunk(string Path, long Offset, byte[] Data, bool Last);

/// <summary>The path, size and content rules for files a run sends back.</summary>
public static class RunUploadRules
{
    /// <summary>Where the Testing agent is asked to save its screenshots, relative to its run folder.</summary>
    public const string Folder = ".agentos/screenshots";

    /// <summary>The most files one run may send back.</summary>
    public const int MaxFiles = 16;

    /// <summary>The largest file (bytes).</summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;

    /// <summary>All files of one run together (bytes).</summary>
    public const long MaxTotalBytes = 40L * 1024 * 1024;

    /// <summary>The largest piece (bytes) — well under SignalR's comfortable message size once base64-encoded.</summary>
    public const int ChunkBytes = 256 * 1024;

    // The ticket's own files (RunFile.Folder) — literal here: the runner links this file without the rest of Domain.
    private const string TicketFilesFolder = ".agentos/files/";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly FrozenDictionary<string, string> Types = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise and check the path of a file a run sends back. False, with a plain-language <paramref name="error"/>, for a
    /// path that fails <see cref="TicketFilePath"/>, isn't an image, or points into the ticket's own files.
    /// </summary>
    public static bool TryCheck(string? path, out string normalized, out string? error)
    {
        if (!TicketFilePath.TryNormalize(path, out normalized, out error)) { return false; }
        if (normalized.StartsWith(TicketFilesFolder, StringComparison.OrdinalIgnoreCase))
        {
            error = "A ticket's own files aren't sent back.";
            normalized = string.Empty;
            return false;
        }
        if (ContentType(normalized) is null)
        {
            error = "Only screenshots (.png, .jpg, .jpeg, .webp) are sent back.";
            normalized = string.Empty;
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>The media type of an allowed image path, or null.</summary>
    public static string? ContentType(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var dot = path.LastIndexOf('.');
        return dot >= 0 && Types.TryGetValue(path[dot..], out var type) ? type : null;
    }

    /// <summary>True when <paramref name="content"/> starts like the image its <paramref name="path"/>'s extension names
    /// (PNG signature, JPEG SOI marker, RIFF…WEBP) — a text file renamed .png is refused.</summary>
    public static bool LooksLikeImage(string path, ReadOnlySpan<byte> content) => ContentType(path) switch
    {
        "image/png" => content.Length >= 8 && content[..8].SequenceEqual(PngSignature),
        "image/jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
        "image/webp" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };
}
