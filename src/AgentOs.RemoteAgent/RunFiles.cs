// Ticket files (ADR-0015), runner side: the only way files reach an agent on this computer. A run's dispatch lists the
// files a person attached to its ticket (path, size, SHA-256); BEFORE the CLI starts, the runner fetches each one from
// the server in pieces (ReadRunFile) and writes it at <run dir>/.agentos/files/<path>. The agent itself can't download
// anything (WebFetch is denied, and there is no curl to the internet) — this is how a logo, art, a sample data file or
// a skill folder to vendor gets to it.
//
// Safety, re-checked here whatever the server said: the path must pass the same TicketFilePath rules (linked from
// Domain), the resolved target must stay inside the files folder, a file is never written twice or over something that
// exists, the bytes must match the announced size and SHA-256, and the total is capped. A file is written, never
// executed. On any refusal the run fails before the CLI starts (and the partial folder goes with the run directory).

using System.Security.Cryptography;

namespace AgentOs.RemoteAgent;

/// <summary>Writes a run's ticket files into its working folder.</summary>
internal static class RunFiles
{
    /// <summary>The folder under a run directory (matches <c>RunFile.Folder</c> on the server).</summary>
    internal const string Folder = ".agentos/files";

    /// <summary>The most one run may carry, whatever the server announces.</summary>
    internal const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>A file the dispatch announces (the server's <c>RunFileEntry</c>).</summary>
    internal sealed record Entry(string Path, long Size, string Sha256);

    /// <summary>A fetched piece (the server's <c>RunFileChunk</c>).</summary>
    internal sealed record Chunk(byte[] Data, bool Last);

    /// <summary>Where <paramref name="path"/> lands under <paramref name="runDir"/>, or null when it would not stay inside
    /// the files folder (or fails the path rules).</summary>
    internal static string? Target(string runDir, string path)
    {
        if (!AgentOs.Domain.Files.TicketFilePath.TryNormalize(path, out var normalized, out _)
            || !string.Equals(normalized, path, StringComparison.Ordinal))
        {
            return null;
        }
        var root = FilesRoot(runDir);
        var full = Path.GetFullPath(Path.Join(root, normalized));
        return full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }

    /// <summary>The absolute files folder of a run directory.</summary>
    internal static string FilesRoot(string runDir) => Path.GetFullPath(Path.Join(runDir, ".agentos", "files"));

    /// <summary>
    /// Fetch and write every announced file. <paramref name="read"/>(index, offset) asks the server for the next piece
    /// (null = refused). Returns null when all were written, else the reason nothing more was.
    /// </summary>
    internal static async Task<string?> WriteAllAsync(string runDir, IReadOnlyList<Entry> files, Func<int, long, Task<Chunk?>> read,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        if (files.Count == 0) { return null; }
        long total = 0;
        foreach (var f in files)
        {
            if (f.Size < 0) { return $"\"{f.Path}\" has a negative size."; }
            total += f.Size;
        }
        if (total > MaxTotalBytes) { return $"The ticket's files are {total} bytes — more than this computer takes for one run ({MaxTotalBytes})."; }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (Target(runDir, f.Path) is not { } target)
            {
                return $"Refused \"{f.Path}\": a ticket file must stay inside {Folder}.";
            }
            if (!seen.Add(target) || File.Exists(target) || Directory.Exists(target))
            {
                return $"Refused \"{f.Path}\": it would overwrite another file.";
            }
            // A link (symlink / junction) anywhere between the run directory and the file would carry the write
            // elsewhere: checked on what exists before the folders are made, and again on the whole chain after.
            var parent = Path.GetDirectoryName(target)!;
            if (LinkOnTheWay(runDir, parent) is { } link) { return $"Refused \"{f.Path}\": \"{link}\" is a link."; }
            Directory.CreateDirectory(parent);
            if (LinkOnTheWay(runDir, parent) is { } after) { return $"Refused \"{f.Path}\": \"{after}\" is a link."; }
            if (await WriteOneAsync(target, i, f, read, ct).ConfigureAwait(false) is { } problem)
            {
                TryDelete(target);
                return problem;
            }
        }
        return null;
    }

    /// <summary>The first existing folder from <paramref name="runDir"/>'s <c>.agentos</c> down to <paramref name="dir"/>
    /// that is a reparse point (symlink, junction), or null when there is none.</summary>
    internal static string? LinkOnTheWay(string runDir, string dir)
    {
        var top = Path.GetFullPath(Path.Join(runDir, ".agentos"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length >= top.Length && current.StartsWith(top, comparison))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)) { return current; }
            if (string.Equals(current, top, comparison)) { break; }
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
        return null;
    }

    // ── copy-in: the one way the agent copies a ticket file into its checkout ─────────────────────────────────────

    /// <summary>Where the helper lives, relative to the run directory (the scoped allow rule names exactly this).</summary>
    internal const string CopyHelper = ".agentos/bin/copy-in";

    /// <summary>
    /// The helper the agent may run (<c>sh .agentos/bin/copy-in &lt;src&gt; &lt;dst&gt;</c> — the only copy its scoped
    /// rules allow; a bare <c>cp</c> rule would let it copy anywhere, e.g. onto <c>~/.ssh/authorized_keys</c>). A
    /// constant of the runner, rewritten on every delivery — never content from the server or the ticket. It copies one
    /// regular file from <c>.agentos/files/</c> to a destination inside the run directory (not under <c>.agentos/</c>),
    /// refuses <c>.</c> / <c>..</c> segments, links on the way, and overwriting a file outside the cloned repository,
    /// and makes missing folders inside the run directory only.
    /// </summary>
    internal const string CopyHelperScript = """
        #!/bin/sh
        # AgentOS.NET copy-in: copy one file attached to the ticket into this run's checkout.
        # Written by the AgentOS.NET runner for this run; usage: sh .agentos/bin/copy-in <file> <destination>
        set -eu
        die() { echo "copy-in: $*" >&2; exit 1; }
        [ "$#" -eq 2 ] || die "usage: sh .agentos/bin/copy-in .agentos/files/<file> <destination file inside the run folder>"
        run=$(cd -P -- "$(dirname -- "$0")/../.." && pwd) || die "can't find the run folder"
        files="$run/.agentos/files"
        src=$1
        dst=$2
        case "$src" in ./*) src=${src#./} ;; esac
        case "$dst" in ./*) dst=${dst#./} ;; esac
        case "/$src/" in */../*|*/./*) die "refused: no . or .. in the file's path" ;; esac
        case "/$dst/" in */../*|*/./*) die "refused: no . or .. in the destination" ;; esac
        case "$dst" in ''|*/) die "refused: name the destination file, not a folder" ;; esac
        # The source: a regular file (not a link) under .agentos/files.
        [ -L "$src" ] && die "refused: $src is a link"
        [ -f "$src" ] || die "refused: $src is not a file"
        srcdir=$(cd -P -- "$(dirname -- "$src")" && pwd) || die "refused: $src"
        case "$srcdir/" in "$files"/*) ;; *) die "refused: $src is not one of the ticket's files (.agentos/files)" ;; esac
        # The destination and every existing folder above it: no links.
        q=$dst
        while :; do
          [ -L "$q" ] && die "refused: $q is a link"
          p=$(dirname -- "$q")
          [ "$p" = "$q" ] && break
          q=$p
        done
        [ -d "$dst" ] && die "refused: $dst is a folder — name the destination file"
        # The nearest existing folder must be inside the run folder, and not under .agentos.
        p=$(dirname -- "$dst")
        while [ ! -d "$p" ]; do p=$(dirname -- "$p"); done
        base=$(cd -P -- "$p" && pwd) || die "refused: $dst"
        case "$base/" in "$run"/*) ;; *) die "refused: $dst is outside the run folder" ;; esac
        case "$base/" in "$run/.agentos/"*) die "refused: $dst is inside .agentos" ;; esac
        # An existing file may be replaced only inside the cloned repository (a folder with .git, below the run folder).
        if [ -e "$dst" ]; then
          r=$base
          inrepo=no
          while [ "$r" != "$run" ]; do
            if [ -e "$r/.git" ]; then inrepo=yes; break; fi
            r=$(dirname -- "$r")
          done
          [ "$inrepo" = yes ] || die "refused: $dst exists outside the cloned repository"
        fi
        mkdir -p -- "$(dirname -- "$dst")"
        final=$(cd -P -- "$(dirname -- "$dst")" && pwd) || die "refused: $dst"
        case "$final/" in "$run"/*) ;; *) die "refused: $dst is outside the run folder" ;; esac
        case "$final/" in "$run/.agentos/"*) die "refused: $dst is inside .agentos" ;; esac
        cp -- "$src" "$dst"
        echo "copied $src -> $dst"
        """;

    /// <summary>(Re)write the copy-in helper of a run (LF line endings, never anything from the server).</summary>
    internal static string WriteCopyHelper(string runDir)
    {
        var path = Path.GetFullPath(Path.Join(runDir, ".agentos", "bin", "copy-in"));
        var bin = Path.GetDirectoryName(path)!;
        if (LinkOnTheWay(runDir, bin) is { } link) { throw new IOException($"\"{link}\" is a link."); }
        Directory.CreateDirectory(bin);
        if (LinkOnTheWay(runDir, bin) is { } after) { throw new IOException($"\"{after}\" is a link."); }
        if (File.Exists(path)) { File.Delete(path); }
        File.WriteAllText(path, CopyHelperScript.ReplaceLineEndings("\n") + "\n", new System.Text.UTF8Encoding(false));
        return path;
    }

    private static async Task<string?> WriteOneAsync(string target, int index, Entry f, Func<int, long, Task<Chunk?>> read, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        long written = 0;
        while (true)
        {
            var chunk = await read(index, written).ConfigureAwait(false);
            if (chunk is null) { return $"The server didn't send \"{f.Path}\"."; }
            if (written + chunk.Data.LongLength > f.Size) { return $"\"{f.Path}\" is bigger than announced."; }
            hash.AppendData(chunk.Data);
            await output.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
            written += chunk.Data.LongLength;
            if (chunk.Last) { break; }
            if (chunk.Data.Length == 0) { return $"The server sent an empty piece of \"{f.Path}\"."; }
        }
        if (written != f.Size) { return $"\"{f.Path}\" is {written} bytes, not the {f.Size} announced."; }
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        return string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase) ? null : $"\"{f.Path}\" doesn't match its SHA-256.";
    }

    /// <summary>The sentence the runner logs (paths are the person's, sizes in bytes).</summary>
    internal static string Describe(IReadOnlyList<Entry> files) =>
        $"{files.Count} ticket file(s), {files.Sum(f => f.Size)} bytes → {Folder}";

    /// <summary>Delete leftover folders of server-driven deliveries (<c>*-files-*</c> under the work root) older than
    /// <paramref name="age"/>. Best-effort; never throws.</summary>
    internal static void CleanupDeliveries(string root, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(root)) { return; }
            foreach (var dir in Directory.EnumerateDirectories(root, "*-files-*"))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > age) { ScopedAutonomy.TryDelete(dir); }
            }
        }
        catch (IOException) { /* next start tries again */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { /* the run directory goes anyway */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
