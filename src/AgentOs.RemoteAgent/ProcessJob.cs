// Everything a run starts dies with the run. A CLI agent's background tools (a `vite` dev server, a test host, a corpus
// converter) outlived their run and held ports and memory for hours; when the runner itself was restarted, the whole
// CLI run carried on orphaned and raced its own retry for the same branch.
//
// On Windows the run goes into a Job Object with KILL_ON_JOB_CLOSE: children join the job automatically (the job does
// not allow breakaway, so a `dotnet run` the agent's shell starts in the background stays in it), and closing the
// handle — Dispose, or the OS when the runner process dies for any reason — kills every process still in it.
// Elsewhere it falls back to killing the root's current process tree on Dispose, plus every descendant it has SEEN
// (Sample, every few seconds during a run): a background server whose parent exited is re-parented to init and is no
// longer in the root's tree, but it was sampled while it was. No orphan guarantee after a hard kill of the runner there.
//
// A run's processes are also what the end-of-run cleanup works from (RunCleanup.cs): which one is the Aspire orchestrator
// (DCP) to let it tear its containers down, and which containers' creator process belonged to this run. A process is
// identified by its pid AND its start time — a pid alone is reused.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentOs.RemoteAgent;

/// <summary>One process of a run: its pid, when it started (UTC — with the pid, its identity) and its image name.</summary>
internal readonly record struct RunProcess(int Pid, DateTime StartedUtc, string Name)
{
    /// <summary>Two start times closer than this are the same process (the OS and DCP's labels round differently).</summary>
    internal static readonly TimeSpan SameStart = TimeSpan.FromSeconds(1);

    /// <summary>Whether the process <paramref name="pid"/> that started at <paramref name="startedUtc"/> is this one.</summary>
    internal bool Is(int pid, DateTime startedUtc) => pid == Pid && (startedUtc - StartedUtc).Duration() < SameStart;
}

/// <summary>The processes a run started — what the end-of-run cleanup works from (a fake in tests).</summary>
internal interface IRunProcessTree : IDisposable
{
    /// <summary>Remember the run's current processes (cheap; called every few seconds and before the cleanup).</summary>
    void Sample();

    /// <summary>The run's processes alive now.</summary>
    IReadOnlyList<RunProcess> Members();

    /// <summary>Every process ever sampled in this run, alive or not.</summary>
    IReadOnlyCollection<RunProcess> Seen { get; }

    /// <summary>Whether that very process (pid + start time) still runs.</summary>
    bool IsAlive(RunProcess process);

    /// <summary>Kill that one process (never its tree) when it still runs.</summary>
    void Kill(RunProcess process);
}

/// <summary>A process tree that is killed as a whole when disposed (or when the runner exits, on Windows).</summary>
internal sealed partial class ProcessJob : IRunProcessTree
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const int MaxIds = 4096;
    private const int MaxSeen = 4096;

    private static readonly string[] PsArgs = ["-A", "-o", "pid=", "-o", "ppid="];

    private nint _handle;
    private readonly List<Process> _roots = [];
    private readonly List<int> _rootPids = [];
    private readonly Dictionary<(int Pid, long Ticks), RunProcess> _seen = [];

    private ProcessJob(nint handle) => _handle = handle;

    /// <summary>A new, empty job.</summary>
    public static ProcessJob Create()
    {
        if (!OperatingSystem.IsWindows()) { return new ProcessJob(0); }
        var handle = CreateJobObjectW(0, null);
        if (handle == 0) { return new ProcessJob(0); } // best effort: still tracks roots
        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            CloseHandle(handle);
            return new ProcessJob(0);
        }
        return new ProcessJob(handle);
    }

    /// <summary>Put a just-started process (and everything it starts from now on) into the job.</summary>
    public void Add(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_roots)
        {
            _roots.Add(process);
            try { _rootPids.Add(process.Id); }
            catch (InvalidOperationException ex) { _ = ex; } // exited already
        }
        if (_handle != 0)
        {
            try { _ = AssignProcessToJobObject(_handle, process.Handle); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { _ = ex; } // exited already
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<RunProcess> Seen
    {
        get { lock (_seen) { return [.. _seen.Values]; } }
    }

    /// <inheritdoc />
    public void Sample()
    {
        var now = Members();
        lock (_seen)
        {
            foreach (var p in now)
            {
                if (_seen.Count >= MaxSeen) { break; }
                _seen.TryAdd((p.Pid, p.StartedUtc.Ticks), p);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RunProcess> Members()
    {
        var handle = _handle;
        List<int> pids;
        if (handle != 0 && OperatingSystem.IsWindows()) { pids = JobProcessIds(handle); }
        else
        {
            lock (_roots) { pids = [.. _rootPids]; }
            if (!OperatingSystem.IsWindows()) { pids = Descendants(pids); }
        }
        return [.. pids.Select(Describe).OfType<RunProcess>()];
    }

    /// <inheritdoc />
    public bool IsAlive(RunProcess process) => Describe(process.Pid) is { } now && process.Is(now.Pid, now.StartedUtc);

    /// <inheritdoc />
    public void Kill(RunProcess process)
    {
        try
        {
            using var p = Process.GetProcessById(process.Pid);
            if (process.Is(p.Id, p.StartTime.ToUniversalTime())) { p.Kill(); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { _ = ex; }
    }

    /// <summary>Kill everything in the job.</summary>
    public void Dispose()
    {
        List<Process> roots;
        lock (_roots) { roots = [.. _roots]; _roots.Clear(); }
        foreach (var root in roots)
        {
            try { if (!root.HasExited) { root.Kill(entireProcessTree: true); } }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { _ = ex; }
        }
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            CloseHandle(handle); // KILL_ON_JOB_CLOSE: background children that left the tree go too
            return;
        }
        // No job: whatever was seen in the run and still runs (a server re-parented to init) goes too.
        foreach (var p in Seen) { Kill(p); }
    }

    /// <summary>A process by pid, with its start time and name; null when it is gone or not ours to inspect.</summary>
    internal static RunProcess? Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new RunProcess(pid, p.StartTime.ToUniversalTime(), p.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _ = ex;
            return null;
        }
    }

    // Windows: the ids of every process in the job right now.
    private static List<int> JobProcessIds(nint handle)
    {
        var size = 8 + (MaxIds * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(handle, JobObjectBasicProcessIdList, buffer, (uint)size, out _)) { return []; }
            var count = Math.Min(Marshal.ReadInt32(buffer, 4), MaxIds);
            var ids = new List<int>(count);
            for (var i = 0; i < count; i++) { ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size))); }
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Linux / macOS: the roots and every descendant of theirs (one `ps` call).
    private static List<int> Descendants(List<int> roots)
    {
        if (roots.Count == 0) { return roots; }
        string output;
        try
        {
            var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in PsArgs) { psi.ArgumentList.Add(a); }
            using var ps = Process.Start(psi);
            if (ps is null) { return roots; }
            output = ps.StandardOutput.ReadToEnd();
            if (!ps.WaitForExit(5000)) { ps.Kill(); }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _ = ex;
            return roots;
        }
        return DescendantsOf(roots, output);
    }

    /// <summary>The roots plus every descendant of theirs, from <c>ps -o pid= -o ppid=</c> output.</summary>
    internal static List<int> DescendantsOf(IReadOnlyCollection<int> roots, string psOutput)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var line in psOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)) { continue; }
            if (!children.TryGetValue(ppid, out var list)) { children[ppid] = list = []; }
            list.Add(pid);
        }
        var result = new List<int>(roots);
        var known = new HashSet<int>(roots);
        for (var i = 0; i < result.Count; i++)
        {
            if (!children.TryGetValue(result[i], out var kids)) { continue; }
            result.AddRange(kids.Where(known.Add));
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int infoClass, nint info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
