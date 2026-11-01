// A "Try the app" preview outlives the runner that started it. Before, the app was a child of the runner inside its
// Job Object (KILL_ON_JOB_CLOSE), so every runner restart — a lab update, `lab.ps1 pair`, a crash — killed the app the
// person was looking at. Now:
//
//   - It starts in its OWN group: a session/process group of its own on Linux and macOS (`setsid`, so a Ctrl+C or a
//     closed terminal on the runner doesn't reach it), a named Job Object WITHOUT kill-on-close plus its own hidden
//     console on Windows. Its output goes to a log file, not to pipes the runner holds (a dead reader would kill it).
//   - Its state (pid, process start time, port, recipe, board/repo) is a small JSON file in the runner's data folder
//     (PreviewStore), so a restarted runner finds it, checks it is still the same process — the start time guards
//     against a recycled pid — and re-announces it to the server.
//   - Stop still kills the whole tree: the named job (Windows) or the process group (elsewhere), plus .NET's
//     process-tree kill for anything that left the group.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOs.RemoteAgent;

/// <summary>What the runner keeps on disk about a running preview (one file per preview).</summary>
internal sealed record PreviewRecord(
    string PreviewId, string Owner, string Repo, string Command, string Url, int Port, int ReadyTimeoutSeconds, int IdleMinutes,
    Guid BoardId, Guid RepoId, int Pid, long ProcessStartTicks, bool OwnGroup, DateTimeOffset StartedAt, bool Ready);

/// <summary>A preview this runner still runs, as it announces it on (re)connect — mirrors the server's record.</summary>
internal sealed record PreviewAnnouncement(string PreviewId, string Owner, string Repo, string Command, string Url,
    DateTimeOffset StartedAt, Guid BoardId, Guid RepoId, string State);

[JsonSerializable(typeof(PreviewRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PreviewJson : JsonSerializerContext;

/// <summary>The previews' state files: <c>&lt;previews folder&gt;/running/&lt;preview id&gt;.json</c> (+ its <c>.log</c>).</summary>
internal static class PreviewStore
{
    /// <summary>The folder holding the state files.</summary>
    public static string Folder(string root) => Path.Join(Path.GetFullPath(root), "running");

    /// <summary>A preview id that is safe as a file name (the server sends a Guid in "N" form).</summary>
    public static bool IsSafeId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>The app's output log.</summary>
    public static string LogPath(string root, string previewId) => Path.Join(Folder(root), previewId + ".log");

    /// <summary>Write (or replace) a preview's state — atomically, so a crash mid-write never leaves half a file.</summary>
    public static void Save(string root, PreviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsSafeId(record.PreviewId)) { throw new ArgumentException("Not a preview id.", nameof(record)); }
        var folder = Folder(root);
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, record.PreviewId + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, PreviewJson.Default.PreviewRecord));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Every preview state on disk. An unreadable file is deleted (it can't be re-attached anyway).</summary>
    public static IReadOnlyList<PreviewRecord> Load(string root)
    {
        var folder = Folder(root);
        if (!Directory.Exists(folder)) { return []; }
        var found = new List<PreviewRecord>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Take(64))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), PreviewJson.Default.PreviewRecord);
                if (record is not null && IsSafeId(record.PreviewId)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), record.PreviewId, StringComparison.Ordinal))
                {
                    found.Add(record);
                    continue;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _ = ex; // unreadable: dropped below
            }
            TryDelete(path);
        }
        return found;
    }

    /// <summary>Forget a preview (its state and log).</summary>
    public static void Delete(string root, string previewId)
    {
        if (!IsSafeId(previewId)) { return; }
        TryDelete(Path.Join(Folder(root), previewId + ".json"));
        TryDelete(LogPath(root, previewId));
    }

    /// <summary>The last <paramref name="lines"/> lines of the app's log (reads at most the last 16 KB).</summary>
    public static string Tail(string root, string previewId, int lines)
    {
        try
        {
            using var stream = new FileStream(LogPath(root, previewId), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 16 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join('\n', all.TakeLast(lines).Select(l => l.Length > 300 ? l[..300] : l));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex; // still held — the next sweep retries
        }
    }
}

/// <summary>Starts, recognises and kills a preview's process group, independent of the runner's lifetime.</summary>
internal static partial class PreviewProcess
{
    private const string LogVariable = "AGENTOS_PREVIEW_LOG";
    private const uint JobObjectTerminate = 0x0008;
    private static readonly string[] SetsidPaths = ["/usr/bin/setsid", "/bin/setsid"];

    /// <summary>The Windows job name of a preview (per user session: the <c>Local\</c> namespace).</summary>
    public static string JobName(string previewId) => "Local\\AgentOS.NET.Preview." + previewId;

    /// <summary>The OS shell running one recipe line in <paramref name="dir"/>, in a group of its own, its output appended
    /// to <paramref name="logPath"/> and its input empty — nothing tied to the runner process.</summary>
    internal static ProcessStartInfo ShellFor(string command, string dir, string logPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        var windows = OperatingSystem.IsWindows();
        var setsid = windows ? null : Array.Find(SetsidPaths, File.Exists);
        var psi = new ProcessStartInfo(windows ? "cmd.exe" : setsid ?? "/bin/sh")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true, // Windows: its own (hidden) console — closing the runner's window doesn't reach it
        };
        psi.Environment[LogVariable] = logPath;
        if (windows)
        {
            // /s: strip exactly the outer quotes; the group sends every command of the line to the log.
            psi.Arguments = "/d /s /c \"(" + command + ") >>\"%" + LogVariable + "%\" 2>&1 <NUL\"";
        }
        else
        {
            if (setsid is not null) { psi.ArgumentList.Add("/bin/sh"); }
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec >>\"$" + LogVariable + "\" 2>&1 </dev/null" + Environment.NewLine + command);
        }
        return psi;
    }

    /// <summary>True when <paramref name="psi"/> puts the app in a process group of its own (pid = group id).</summary>
    internal static bool StartsOwnGroup(ProcessStartInfo psi) =>
        !OperatingSystem.IsWindows() && psi.FileName.EndsWith("setsid", StringComparison.Ordinal);

    /// <summary>Windows: put a just-started preview into its named job (no kill-on-close: the job outlives the runner's
    /// handle while the app runs). Returns the handle to keep while the runner runs; elsewhere nothing.</summary>
    public static IDisposable? Group(Process process, string previewId)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) { return null; }
        var job = CreateJobObjectW(0, JobName(previewId));
        if (job == 0) { return null; }
        try { _ = AssignProcessToJobObject(job, process.Handle); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { _ = ex; }
        return new JobHandle(job);
    }

    /// <summary>The process start time as UTC ticks, or null when <paramref name="pid"/> isn't running (or can't be read).</summary>
    public static long? StartTicks(int pid)
    {
        if (pid <= 0) { return null; }
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited ? null : p.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>True when <paramref name="pid"/> is still the process that started at <paramref name="startTicks"/> — a pid
    /// the OS recycled for another program never matches. (Linux derives the start time from the boot clock; a tolerance
    /// of one second absorbs its rounding, far below any realistic pid reuse.)</summary>
    public static bool IsSame(int pid, long startTicks) =>
        StartTicks(pid) is { } now && Math.Abs(now - startTicks) < TimeSpan.TicksPerSecond;

    /// <summary>Kill a preview's whole tree: the root (only when it is still the same process), its process group or
    /// named job, and every descendant .NET can find.</summary>
    public static void Kill(PreviewRecord record, Process? root = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var same = root is not null ? !HasExited(root) : IsSame(record.Pid, record.ProcessStartTicks);
        if (same)
        {
            try
            {
                using var owned = root is null ? Process.GetProcessById(record.Pid) : null;
                (root ?? owned!).Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                _ = ex; // gone meanwhile
            }
        }
        if (OperatingSystem.IsWindows())
        {
            var job = OpenJobObjectW(JobObjectTerminate, false, JobName(record.PreviewId));
            if (job != 0)
            {
                _ = TerminateJobObject(job, 1); // background children that left the tree go too
                _ = CloseHandle(job);
            }
        }
        else if (record.OwnGroup && (same || root is not null))
        {
            // The group id is the root's pid; while the group has a member, the kernel can't hand that number to anyone else.
            // (A root we started ourselves counts even after it exited: its group's background children go too.)
            _ = SysKill(-record.Pid, 9);
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private sealed class JobHandle(nint handle) : IDisposable
    {
        private nint _handle = handle;

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, 0);
            if (h != 0) { _ = CloseHandle(h); }
        }
    }

    internal static string Describe(PreviewRecord r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.Owner}/{r.Repo} (pid {r.Pid}, port {r.Port})");

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
