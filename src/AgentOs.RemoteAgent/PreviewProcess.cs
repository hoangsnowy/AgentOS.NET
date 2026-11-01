// A "Try the app" preview outlives the runner that started it. Before, the app was a child of the runner inside its
// Job Object (KILL_ON_JOB_CLOSE), so every runner restart — a lab update, `lab.ps1 pair`, a crash — killed the app the
// person was looking at. Now:
//
//   - Windows: the runner starts cmd.exe itself (CreateProcessW), SUSPENDED, puts it in a named Job Object WITHOUT
//     kill-on-close, then lets it run — nothing it starts can slip out before it is in the job. The app inherits a handle
//     to that job (only that, its log and NUL — an explicit handle list), so the job's name lives as long as any of the
//     app's processes does: a restarted runner opens it by name and Stop still ends the whole tree. A name that already
//     exists is refused (it isn't ours). Output goes to a log handle opened for appending only.
//   - Linux: `setsid` gives the app a session and process group of its own (a Ctrl+C or a closed terminal on the runner
//     doesn't reach it). Output goes to the log via the shell's `>>`.
//   - macOS has no `setsid`: the app stays in the runner's process group, so a Ctrl+C in the runner's terminal (or
//     closing that terminal) stops the app with the runner. It survives only a runner that exits on its own (an update,
//     a crash); the next runner reports anything gone.
//   - Nothing is tied to pipes the runner holds (a dead reader would kill the app).
//   - Its state (pid, process start time, port, recipe, board/repo) is a small JSON file in the runner's data folder
//     (PreviewStore, a folder only this user can open), so a restarted runner finds it, checks it is still the same
//     process — the start time guards against a recycled pid (Linux: the raw start time in jiffies from /proc), and the
//     process must still be the shell the runner started (cmd / sh) — and re-announces it to the server.
//   - Stop kills the whole tree: the named job (Windows) or the process group (Linux — also when its first process has
//     exited, as long as a member of that group remains), plus .NET's process-tree kill for anything that left it.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOs.RemoteAgent;

/// <summary>What the runner keeps on disk about a running preview (one file per preview).
/// <paramref name="ProcessStartTicks"/> is the OS start-time token of <paramref name="Pid"/>: on Linux the raw start time in
/// clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c> field 22), elsewhere the start time as UTC ticks.
/// <paramref name="OwnJob"/> (Windows) says the runner created the preview's named job itself.</summary>
internal sealed record PreviewRecord(
    string PreviewId, string Owner, string Repo, string Command, string Url, int Port, int ReadyTimeoutSeconds, int IdleMinutes,
    Guid BoardId, Guid RepoId, int Pid, long ProcessStartTicks, bool OwnGroup, DateTimeOffset StartedAt, bool Ready, bool OwnJob = false);

/// <summary>A preview this runner still runs, as it announces it on (re)connect — mirrors the server's record.</summary>
internal sealed record PreviewAnnouncement(string PreviewId, string Owner, string Repo, string Command, string Url,
    DateTimeOffset StartedAt, Guid BoardId, Guid RepoId, string State);

[JsonSerializable(typeof(PreviewRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PreviewJson : JsonSerializerContext;

/// <summary>The previews' state files: <c>&lt;previews folder&gt;/running/&lt;preview id&gt;.json</c> (+ its <c>.log</c>).</summary>
internal static class PreviewStore
{
    /// <summary>The app's log is rotated once it grows past this (the previous part is kept as <c>.log.1</c>).</summary>
    public const long MaxLogBytes = 10L * 1024 * 1024;

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The folder holding the state files.</summary>
    public static string Folder(string root) => Path.Join(Path.GetFullPath(root), "running");

    /// <summary>Create the state folder — readable by this user only off Windows (0700; an existing one is narrowed too).
    /// On Windows it lives under the user's own %LOCALAPPDATA%.</summary>
    public static string EnsureFolder(string root)
    {
        var folder = Folder(root);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, OwnerOnly);
            File.SetUnixFileMode(folder, OwnerOnly);
        }
        return folder;
    }

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
        var folder = EnsureFolder(root);
        var path = Path.Join(folder, record.PreviewId + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, PreviewJson.Default.PreviewRecord));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Every preview state on disk. An unreadable file is deleted (it can't be re-attached anyway); when it still
    /// names a process (pid + start time) that is added to <paramref name="unreadable"/>, so the caller can stop that app
    /// if — and only if — it is still the same process.</summary>
    public static IReadOnlyList<PreviewRecord> Load(string root, List<PreviewRecord>? unreadable = null)
    {
        var folder = Folder(root);
        if (!Directory.Exists(folder)) { return []; }
        var found = new List<PreviewRecord>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Take(64))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), PreviewJson.Default.PreviewRecord);
                if (record is not null && IsSafeId(record.PreviewId) && string.Equals(name, record.PreviewId, StringComparison.Ordinal))
                {
                    found.Add(record);
                    continue;
                }
                // A readable record under another name (a copy): never acted on — its process is another file's.
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                if (unreadable is not null && Salvage(path, name) is { } identity) { unreadable.Add(identity); }
            }
            TryDelete(path);
        }
        return found;
    }

    // What an unreadable state file still says about its process: just enough to recognise (and stop) it.
    private static PreviewRecord? Salvage(string path, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty(nameof(PreviewRecord.Pid), out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var p)
                && r.TryGetProperty(nameof(PreviewRecord.ProcessStartTicks), out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ticks))
            {
                return new PreviewRecord(IsSafeId(name) ? name : "unreadable", "", "", "", "", 0, 0, 0, Guid.Empty, Guid.Empty, p, ticks,
                    r.TryGetProperty(nameof(PreviewRecord.OwnGroup), out var g) && g.ValueKind == JsonValueKind.True,
                    default, false,
                    r.TryGetProperty(nameof(PreviewRecord.OwnJob), out var j) && j.ValueKind == JsonValueKind.True);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _ = ex; // nothing left to recognise it by
        }
        return null;
    }

    /// <summary>Forget a preview (its state and logs).</summary>
    public static void Delete(string root, string previewId)
    {
        if (!IsSafeId(previewId)) { return; }
        TryDelete(Path.Join(Folder(root), previewId + ".json"));
        TryDelete(LogPath(root, previewId));
        TryDelete(LogPath(root, previewId) + ".1");
    }

    /// <summary>Start a preview's log afresh (an older part from an earlier start goes).</summary>
    public static void ResetLog(string root, string previewId)
    {
        EnsureFolder(root);
        File.WriteAllText(LogPath(root, previewId), string.Empty);
        TryDelete(LogPath(root, previewId) + ".1");
    }

    /// <summary>Rotate the app's log once it is past <paramref name="maxBytes"/>: its content becomes <c>.log.1</c> (the one
    /// older part kept) and the log is emptied in place — the app keeps writing to it, appending (Linux: the shell's
    /// <c>&gt;&gt;</c>; Windows: a handle opened for appending only). True when it rotated.</summary>
    public static bool CapLog(string root, string previewId, long maxBytes = MaxLogBytes)
    {
        var path = LogPath(root, previewId);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes) { return false; }
            using var log = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using (var older = new FileStream(path + ".1", FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                log.CopyTo(older);
            }
            log.SetLength(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex; // the next check retries
            return false;
        }
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

/// <summary>A preview's first process, just started: the process, what groups it (Windows: the job handle the runner holds
/// while it runs), and the identity recorded for it.</summary>
internal sealed record LaunchedPreview(Process Process, IDisposable? Group, bool OwnGroup, bool OwnJob, long StartTicks);

/// <summary>Starts, recognises and kills a preview's process group, independent of the runner's lifetime.</summary>
internal static partial class PreviewProcess
{
    private const string LogVariable = "AGENTOS_PREVIEW_LOG";
    private static readonly string[] SetsidPaths = ["/usr/bin/setsid", "/bin/setsid"];

    // What a preview's first process is: the shell the runner started (cmd.exe; sh — setsid execs it, and the trailing
    // `exit` keeps the shell from exec'ing the app in its place). Checked before acting on a recorded pid.
    private static readonly string[] ShellImages = ["cmd", "sh", "dash", "bash", "ash", "busybox", "zsh", "setsid"];

    /// <summary>The Windows job name of a preview (per user session: the <c>Local\</c> namespace).</summary>
    public static string JobName(string previewId) => "Local\\AgentOS.NET.Preview." + previewId;

    /// <summary>Start a recipe line in <paramref name="dir"/>, in a group of its own, its output appended to
    /// <paramref name="logPath"/> and its input empty — nothing tied to the runner process.</summary>
    public static LaunchedPreview Launch(string command, string dir, string logPath, string previewId,
        IEnumerable<(string Name, string Value)>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var extra = environment?.ToList() ?? [];
        if (OperatingSystem.IsWindows()) { return LaunchWindows(command, dir, logPath, previewId, extra); }
        var psi = ShellFor(command, dir, logPath);
        foreach (var (name, value) in extra) { psi.Environment[name] = value; }
        var process = Process.Start(psi) ?? throw new InvalidOperationException("The command didn't start.");
        return new LaunchedPreview(process, null, StartsOwnGroup(psi), false, StartTicks(process.Id) ?? 0);
    }

    /// <summary>Linux / macOS: <c>/bin/sh</c> (under <c>setsid</c> when there is one) running the line with its output
    /// appended to the log (<c>&gt;&gt;</c> opens it O_APPEND, so rotating the log in place is safe).</summary>
    internal static ProcessStartInfo ShellFor(string command, string dir, string logPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        var setsid = Array.Find(SetsidPaths, File.Exists);
        var psi = new ProcessStartInfo(setsid ?? "/bin/sh")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[LogVariable] = logPath;
        if (setsid is not null) { psi.ArgumentList.Add("/bin/sh"); }
        psi.ArgumentList.Add("-c");
        // The blank line ends a trailing `\`; `exit $?` keeps the exit status and keeps sh (not the app) as the recorded process.
        psi.ArgumentList.Add("exec >>\"$" + LogVariable + "\" 2>&1 </dev/null\n" + command + "\n\nexit $?");
        return psi;
    }

    /// <summary>Windows: the command line — <c>/s</c> strips exactly the outer quotes, so the recipe line reaches cmd as
    /// written (its output goes to the log by handle, not by a redirect wrapped around it).</summary>
    internal static string WindowsCommandLine(string cmdPath, string command) => "\"" + cmdPath + "\" /d /s /c \"" + command + "\"";

    /// <summary>True when <paramref name="psi"/> puts the app in a process group of its own (pid = group id).</summary>
    internal static bool StartsOwnGroup(ProcessStartInfo psi) =>
        !OperatingSystem.IsWindows() && psi.FileName.EndsWith("setsid", StringComparison.Ordinal);

    /// <summary>The process start-time token (see <see cref="PreviewRecord"/>), or null when <paramref name="pid"/> isn't
    /// running (or can't be read).</summary>
    public static long? StartTicks(int pid)
    {
        if (pid <= 0) { return null; }
        if (OperatingSystem.IsLinux()) { return ReadStat(pid) is { State: not 'Z' } stat ? stat.StartJiffies : null; }
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited ? null : p.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>True when <paramref name="pid"/> is still the process that started at <paramref name="startTicks"/> — a pid
    /// the OS recycled for another program never matches — and is still the shell a preview starts with. Linux compares the
    /// raw start time exactly; elsewhere within a second (the OS's clock rounding, far below any realistic pid reuse).</summary>
    public static bool IsSame(int pid, long startTicks) =>
        StartTicks(pid) is { } now
        && (OperatingSystem.IsLinux() ? now == startTicks : Math.Abs(now - startTicks) < TimeSpan.TicksPerSecond)
        && IsShell(pid);

    /// <summary>True when <paramref name="pid"/> runs a shell (cmd.exe / sh) — what a preview's first process is.</summary>
    internal static bool IsShell(int pid) =>
        ImageName(pid) is { } name && Array.Exists(ShellImages, s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    private static string? ImageName(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/comm")).Trim();
            }
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or Win32Exception or NotSupportedException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> a preview needs: state, process group, start time (jiffies).</summary>
    internal readonly record struct ProcStat(char State, int Group, long StartJiffies);

    /// <summary>Parse a <c>/proc/&lt;pid&gt;/stat</c> line (the command name may hold spaces and parentheses: fields are
    /// counted after the last ')'). Null when it isn't one.</summary>
    internal static ProcStat? ParseStat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) { return null; }
        // fields[0] is field 3 (state); the process group is field 5, the start time field 22.
        var fields = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0].Length == 0
            || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            || !long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }
        return new ProcStat(fields[0][0], group, start);
    }

    private static ProcStat? ReadStat(int pid)
    {
        try { return ParseStat(File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/stat"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>Linux: true when process group <paramref name="group"/> still has members but no leader — what is left of a
    /// preview whose first process exited. While a group has a member the kernel never hands its number to anyone else, so
    /// those members are the preview's; a live leader would mean the number now belongs to another program's group.</summary>
    internal static bool OrphanedGroupRemains(int group)
    {
        if (!OperatingSystem.IsLinux() || group <= 1) { return false; }
        var found = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    || ReadStat(pid) is not { } stat || stat.Group != group || stat.State == 'Z')
                {
                    continue;
                }
                if (pid == group) { return false; }
                found = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
        return found;
    }

    /// <summary>Kill a preview's whole tree: its named job (Windows) or process group (Linux), and — when it is still the
    /// same process — the root and every descendant .NET can find. A pid that is no longer the preview's is never touched.
    /// True when the preview's first process was still running.</summary>
    public static bool Kill(PreviewRecord record, Process? root = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var same = root is not null ? !HasExited(root) : IsSame(record.Pid, record.ProcessStartTicks);
        if (OperatingSystem.IsWindows())
        {
            // First, while the root still runs and can prove the job is the preview's: background children that left the tree go too.
            if (record.OwnJob) { TerminateJob(record.PreviewId, same ? record.Pid : 0); }
        }
        else if (record.OwnGroup && record.Pid > 1 && (same || OrphanedGroupRemains(record.Pid)))
        {
            // The group id is the root's pid; while the group has a member, the kernel can't hand that number to anyone else.
            _ = SysKill(-record.Pid, 9);
        }
        if (same)
        {
            try
            {
                using var owned = root is null ? Process.GetProcessById(record.Pid) : null;
                (root ?? owned!).Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                _ = ex; // gone meanwhile
            }
        }
        return same;
    }

    /// <summary>Windows: true while the preview's named job exists (some process of the app still holds it).</summary>
    internal static bool JobExists(string previewId)
    {
        if (!OperatingSystem.IsWindows()) { return false; }
        var job = OpenJobObjectW(JobObjectQuery, false, JobName(previewId));
        if (job == 0) { return false; }
        _ = CloseHandle(job);
        return true;
    }

    // Terminate the preview's job. With a live root, only when the root is in it — a job of that name that doesn't hold the
    // root is someone else's.
    private static void TerminateJob(string previewId, int rootPid)
    {
        var job = OpenJobObjectW(JobObjectTerminate | JobObjectQuery, false, JobName(previewId));
        if (job == 0) { return; }
        try
        {
            if (rootPid > 0 && !InJob(rootPid, job)) { return; }
            _ = TerminateJobObject(job, 1);
        }
        finally
        {
            _ = CloseHandle(job);
        }
    }

    private static bool InJob(int pid, nint job)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return IsProcessInJob(p.Handle, job, out var inJob) && inJob;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _ = ex;
            return false;
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    internal static string Describe(PreviewRecord r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.Owner}/{r.Repo} (pid {r.Pid}, port {r.Port})");

    // ── Windows launch ──────────────────────────────────────────────────────────────────────────────

    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectTerminate = 0x0008;
    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const uint FileAppendData = 0x0004;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareAll = 0x7; // read | write | delete: the runner can read, rotate and delete the log
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;
    private const uint HandleFlagInherit = 0x1;
    private static readonly nint InvalidHandle = -1;

    // cmd.exe from System32 by full path — never one found in the checkout (a repo can't ship its own "cmd.exe").
    private static unsafe LaunchedPreview LaunchWindows(string command, string dir, string logPath, string previewId,
        List<(string Name, string Value)> environment)
    {
        var inheritable = new SecurityAttributes { Length = sizeof(SecurityAttributes), Inherit = 1 };
        var job = CreateJobObjectW(&inheritable, JobName(previewId));
        if (job == 0) { throw new InvalidOperationException("Couldn't set up the app's process group: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message); }
        if (Marshal.GetLastPInvokeError() == ErrorAlreadyExists)
        {
            _ = CloseHandle(job);
            throw new InvalidOperationException("Another program already uses this preview's process group name — try again.");
        }
        nint log = 0, nul = 0, attributes = 0, handles = 0;
        var info = default(ProcessInformation);
        var keepJob = false;
        try
        {
            log = CreateFileW(logPath, FileAppendData | FileReadAttributes | Synchronize, FileShareAll, &inheritable, OpenAlways, FileAttributeNormal, 0);
            if (log == InvalidHandle) { log = 0; throw new IOException("Couldn't open the app's log: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message); }
            nul = CreateFileW("NUL", GenericRead, FileShareAll, &inheritable, OpenExisting, 0, 0);
            if (nul == InvalidHandle) { nul = 0; throw new IOException("Couldn't open NUL: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message); }

            // The app inherits exactly these three handles — nothing else the runner has open.
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attributes);
                attributes = 0;
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, job);
            Marshal.WriteIntPtr(handles, IntPtr.Size, log);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, nul);
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handles, (nuint)(3 * IntPtr.Size), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = default(StartupInfoEx);
            startup.StartupInfo.Cb = sizeof(StartupInfoEx);
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.StartupInfo.StdInput = nul;
            startup.StartupInfo.StdOutput = log;
            startup.StartupInfo.StdError = log;
            startup.AttributeList = attributes;

            var cmdPath = Path.Join(Environment.SystemDirectory, "cmd.exe");
            var commandLine = (WindowsCommandLine(cmdPath, command) + "\0").ToCharArray();
            var block = EnvironmentBlock(environment);
            var flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow;
            bool created;
            fixed (char* line = commandLine)
            fixed (char* env = block)
            {
                // Out of any job the runner itself runs in (a terminal's, a service host's), so closing that can't take the
                // app along; a job that forbids breaking away still gets the app, just nested.
                created = CreateProcessW(cmdPath, line, 0, 0, true, flags | CreateBreakawayFromJob, env, dir, &startup, &info);
                if (!created && Marshal.GetLastPInvokeError() == ErrorAccessDenied)
                {
                    created = CreateProcessW(cmdPath, line, 0, 0, true, flags, env, dir, &startup, &info);
                }
            }
            if (!created) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            // Only the app's processes may keep the job's name alive: the runner's own handle is no longer inheritable.
            _ = SetHandleInformation(job, HandleFlagInherit, 0);

            var ownJob = AssignProcessToJobObject(job, info.Process);
            if (!ownJob)
            {
                _ = TerminateProcess(info.Process, 1);
                throw new InvalidOperationException("Couldn't put the app in its process group: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
            _ = ResumeThread(info.Thread);

            Process process;
            try
            {
                // We still hold the process handle, so the pid can't be handed to another program meanwhile.
                process = Process.GetProcessById(info.ProcessId);
                _ = process.Handle; // keep a handle: the exit code stays readable after it ends
            }
            catch (ArgumentException)
            {
                var code = GetExitCodeProcess(info.Process, out var exit) ? exit : 0u;
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The command ended as soon as it started (exit {code})."));
            }
            keepJob = true;
            return new LaunchedPreview(process, new JobHandle(job), false, true, StartTicks(process.Id) ?? 0);
        }
        finally
        {
            if (info.Thread != 0) { _ = CloseHandle(info.Thread); }
            if (info.Process != 0) { _ = CloseHandle(info.Process); }
            if (attributes != 0) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != 0) { Marshal.FreeHGlobal(handles); }
            if (log != 0) { _ = CloseHandle(log); } // the app has its own copies
            if (nul != 0) { _ = CloseHandle(nul); }
            if (!keepJob) { _ = CloseHandle(job); }
        }
    }

    // The runner's environment plus the preview's extra variables, as CreateProcessW wants it: sorted, NUL-separated.
    private static char[] EnvironmentBlock(List<(string Name, string Value)> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            vars[(string)entry.Key] = entry.Value as string ?? string.Empty;
        }
        foreach (var (name, value) in extra) { vars[name] = value; }
        var block = new StringBuilder();
        foreach (var (name, value) in vars) { block.Append(name).Append('=').Append(value).Append('\0'); }
        block.Append('\0');
        if (vars.Count == 0) { block.Append('\0'); }
        return block.ToString().ToCharArray();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint CreateJobObjectW(SecurityAttributes* attributes, string? name);

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
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint CreateFileW(string name, uint access, uint share, SecurityAttributes* security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, string currentDirectory,
        StartupInfoEx* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);
}
