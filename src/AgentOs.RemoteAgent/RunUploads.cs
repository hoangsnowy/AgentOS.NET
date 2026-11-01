// Design gate (ADR-0016), runner side: after a Testing run that was asked for screenshots, the runner sends the image
// files the agent listed in its JSON reply back to the server (UploadRunFile, in pieces) BEFORE it completes the request
// and deletes the run folder. Nothing else ever leaves this computer this way.
//
// Safety, checked here whatever the agent wrote: each path must pass RunUploadRules (linked from Domain — the server
// checks every piece again), resolve inside the run folder, have no link (symlink / junction) on the way and not be a
// link itself, be a regular file that really is the image its name says, and fit the caps the dispatch named (count, per
// file, per run). A path the agent invented outside the run folder ("../../.ssh/id_rsa.png", "C:/x.png") is refused.
// Checked and sent from ONE open handle: the file is opened first, the checks run against what was opened (its length,
// its first bytes) and the path as it is now, and the bytes sent are read from that same handle — swapping the file for
// a link between a check and the read can't make the runner send something else.
// When the reply lists none, the runner falls back to the image files directly in .agentos/screenshots/.

using System.Text.Json;
using AgentOs.Domain.Files;

namespace AgentOs.RemoteAgent;

/// <summary>Sends a run's screenshots back to the server.</summary>
internal static class RunUploads
{
    /// <summary>The caps a dispatch names (the server's <c>RunCollect</c>).</summary>
    internal sealed record Limits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes);

    /// <summary>What a send did: how many files went, and a line for each one that didn't.</summary>
    internal sealed record Outcome(IReadOnlyList<string> Sent, IReadOnlyList<string> Problems);

    /// <summary>The paths the agent listed under "screenshots" in the last JSON object of its reply (objects with a
    /// "path", or bare strings); empty when there are none or the reply isn't JSON.</summary>
    internal static IReadOnlyList<string> ListedPaths(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) { return []; }
        var end = content.LastIndexOf('}');
        // The verdict object is the last one that opens with "verdict"; else try the first brace.
        foreach (var start in new[] { content.LastIndexOf("{\"verdict\"", StringComparison.Ordinal), content.IndexOf('{', StringComparison.Ordinal) })
        {
            if (start < 0 || end <= start) { continue; }
            try
            {
                using var doc = JsonDocument.Parse(content[start..(end + 1)]);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("screenshots", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                return list.EnumerateArray()
                    .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()
                        : i.ValueKind == JsonValueKind.Object && i.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null)
                    .OfType<string>()
                    .Take(256)
                    .ToList();
            }
            catch (JsonException)
            {
                // Not JSON from here — try the next start.
            }
        }
        return [];
    }

    /// <summary>The paths to try: the listed ones, or — when nothing is listed — the files directly in
    /// <c>.agentos/screenshots/</c>, by name.</summary>
    internal static IReadOnlyList<string> Candidates(string runDir, IReadOnlyList<string> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return listed.Count > 0 ? listed : Fallback(runDir);
    }

    /// <summary>
    /// Send every candidate that passes every check back, within <paramref name="limits"/>, in pieces of at most
    /// <see cref="RunUploadRules.ChunkBytes"/>. <paramref name="send"/> returns null when the server accepted a piece,
    /// else why not (that file is then given up). Each file is checked and read from one open handle.
    /// </summary>
    internal static async Task<Outcome> SendAsync(string runDir, IReadOnlyList<string> candidates, Limits limits,
        Func<RunUploadChunk, Task<string?>> send, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(send);
        var root = Path.GetFullPath(runDir).TrimEnd(Path.DirectorySeparatorChar);
        var sent = new List<string>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[RunUploadRules.ChunkBytes];
        long total = 0;
        foreach (var raw in candidates)
        {
            if (!RunUploadRules.TryCheck(raw, out var path, out _) || Inside(root, path) is not { } full)
            {
                problems.Add($"\"{raw}\": not an image file inside the run folder");
                continue;
            }
            if (!seen.Add(path)) { continue; }
            if (sent.Count >= limits.MaxFiles) { problems.Add($"\"{path}\": more than {limits.MaxFiles} files"); continue; }

            await using var stream = Open(full);
            if (stream is null) { problems.Add($"\"{path}\": not an image file inside the run folder"); continue; }
            if (Refusal(root, full, path, stream, limits, total) is { } why) { problems.Add(why); continue; }
            var size = stream.Length;
            stream.Position = 0;
            long offset = 0;
            string? refused;
            while (true)
            {
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                // Never more than the length the checks saw (a file growing meanwhile is cut where it was checked).
                if (offset + read > size) { read = (int)(size - offset); }
                var last = read < buffer.Length || offset + read >= size;
                refused = await send(new RunUploadChunk(path, offset, buffer.AsSpan(0, read).ToArray(), last)).ConfigureAwait(false);
                offset += read;
                if (refused is not null || last) { break; }
            }
            if (refused is not null) { problems.Add(refused); continue; }
            total += size;
            sent.Add(path);
        }
        return new Outcome(sent, problems);
    }

    // The file opened for reading (others may read it too, nobody may write or delete it meanwhile), or null.
    private static FileStream? Open(string full)
    {
        try { return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Why the opened file may not go back (plain words), or null: no link on the way or at the file (as the path is now,
    // after it was opened), within the caps (by the opened handle's length), and its first bytes are the image it claims.
    private static string? Refusal(string root, string full, string path, FileStream stream, Limits limits, long total)
    {
        var info = new FileInfo(full);
        if (LinkBetween(root, Path.GetDirectoryName(full)!) is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            return $"\"{path}\": not an image file inside the run folder";
        }
        var size = stream.Length;
        if (size > limits.MaxFileBytes) { return $"\"{path}\": bigger than {limits.MaxFileBytes} bytes"; }
        if (total + size > limits.MaxTotalBytes) { return $"\"{path}\": over the run's {limits.MaxTotalBytes} bytes"; }
        Span<byte> head = stackalloc byte[16];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return RunUploadRules.LooksLikeImage(path, head[..read]) ? null : $"\"{path}\": not an image file inside the run folder";
    }

    // The absolute path of a checked relative path, or null when it would leave the run folder.
    private static string? Inside(string root, string path)
    {
        var full = Path.GetFullPath(Path.Join(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison) ? full : null;
    }

    /// <summary>The first existing folder below <paramref name="root"/> down to <paramref name="dir"/> that is a link, or null.</summary>
    internal static string? LinkBetween(string root, string dir)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length > top.Length && current.StartsWith(top, comparison))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)) { return current; }
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
        return null;
    }

    // Nothing listed: the files directly in .agentos/screenshots (no recursion), by name.
    private static List<string> Fallback(string runDir)
    {
        var folder = Path.GetFullPath(Path.Join(runDir, RunUploadRules.Folder));
        if (!Directory.Exists(folder)) { return []; }
        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(f => RunUploadRules.Folder + "/" + Path.GetFileName(f))
                .Order(StringComparer.Ordinal)
                .Take(256)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
