// Design gate (ADR-0016), runner side: after a Testing run that was asked for screenshots, the runner sends the image
// files the agent listed in its JSON reply back to the server (UploadRunFile, in pieces) BEFORE it completes the request
// and deletes the run folder. Nothing else ever leaves this computer this way.
//
// Safety, checked here whatever the agent wrote: each path must pass RunUploadRules (linked from Domain — the server
// checks every piece again), resolve inside the run folder, have no link (symlink / junction) on the way and not be a
// link itself, be a regular file that really is the image its name says, and fit the caps the dispatch named (count, per
// file, per run). A path the agent invented outside the run folder ("../../.ssh/id_rsa.png", "C:/x.png") is refused.
// When the reply lists none, the runner falls back to the image files directly in .agentos/screenshots/.

using System.Text.Json;
using AgentOs.Domain.Files;

namespace AgentOs.RemoteAgent;

/// <summary>Sends a run's screenshots back to the server.</summary>
internal static class RunUploads
{
    /// <summary>The caps a dispatch names (the server's <c>RunCollect</c>).</summary>
    internal sealed record Limits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes);

    /// <summary>A file chosen to go back: its normalised path (relative to the run folder) and where it is.</summary>
    internal sealed record Pick(string Path, string FullPath, long Size);

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

    /// <summary>
    /// The files to send back: the listed paths that pass every check, in order, within the caps — or, when nothing is
    /// listed, the images directly in <c>.agentos/screenshots/</c>. <paramref name="refused"/> says why the others weren't.
    /// </summary>
    internal static IReadOnlyList<Pick> Select(string runDir, IReadOnlyList<string> listed, Limits limits, out IReadOnlyList<string> refused)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(limits);
        var why = new List<string>();
        var candidates = listed.Count > 0 ? listed : Fallback(runDir);
        var picks = new List<Pick>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var raw in candidates)
        {
            if (Check(runDir, raw) is not { } pick)
            {
                why.Add($"\"{raw}\": not an image file inside the run folder");
                continue;
            }
            if (!seen.Add(pick.Path)) { continue; }
            if (picks.Count >= limits.MaxFiles) { why.Add($"\"{pick.Path}\": more than {limits.MaxFiles} files"); continue; }
            if (pick.Size > limits.MaxFileBytes) { why.Add($"\"{pick.Path}\": bigger than {limits.MaxFileBytes} bytes"); continue; }
            if (total + pick.Size > limits.MaxTotalBytes) { why.Add($"\"{pick.Path}\": over the run's {limits.MaxTotalBytes} bytes"); continue; }
            total += pick.Size;
            picks.Add(pick);
        }
        refused = why;
        return picks;
    }

    /// <summary>The file <paramref name="raw"/> names, when it may go back: allowed path, inside the run folder, no link on
    /// the way, a regular non-link file that starts like its image type. Null otherwise.</summary>
    internal static Pick? Check(string runDir, string? raw)
    {
        if (!RunUploadRules.TryCheck(raw, out var path, out _)) { return null; }
        var root = Path.GetFullPath(runDir).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Join(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) { return null; }
        if (LinkBetween(root, Path.GetDirectoryName(full)!) is not null) { return null; }
        var info = new FileInfo(full);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null) { return null; }
        Span<byte> head = stackalloc byte[16];
        int read;
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        return RunUploadRules.LooksLikeImage(path, head[..read]) ? new Pick(path, full, info.Length) : null;
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

    // Nothing listed: the images directly in .agentos/screenshots (no recursion), by name.
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

    /// <summary>
    /// Send every picked file back in pieces of at most <see cref="RunUploadRules.ChunkBytes"/>. <paramref name="send"/>
    /// returns null when the server accepted the piece, else why not (that file is then skipped). Returns a line per file
    /// that didn't make it (empty when all did).
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<Pick> picks, Func<RunUploadChunk, Task<string?>> send, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(send);
        var problems = new List<string>();
        var buffer = new byte[RunUploadRules.ChunkBytes];
        foreach (var pick in picks)
        {
            await using var stream = new FileStream(pick.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            long offset = 0;
            while (true)
            {
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                var last = read < buffer.Length || stream.Position >= stream.Length;
                if (await send(new RunUploadChunk(pick.Path, offset, buffer.AsSpan(0, read).ToArray(), last)).ConfigureAwait(false) is { } refused)
                {
                    problems.Add(refused);
                    break;
                }
                offset += read;
                if (last) { break; }
            }
        }
        return problems;
    }
}
