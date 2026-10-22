// Everything a run starts dies with the run. A CLI agent's background tools (a `vite` dev server, a test host, a corpus
// converter) outlived their run and held ports and memory for hours; when the runner itself was restarted, the whole
// CLI run carried on orphaned and raced its own retry for the same branch.
//
// On Windows the run goes into a Job Object with KILL_ON_JOB_CLOSE: children join the job automatically, and closing
// the handle — Dispose, or the OS when the runner process dies for any reason — kills every process still in it.
// Elsewhere it falls back to killing the root's current process tree on Dispose (no orphan guarantee after a hard kill).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentOs.RemoteAgent;

/// <summary>A process tree that is killed as a whole when disposed (or when the runner exits, on Windows).</summary>
internal sealed partial class ProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private nint _handle;
    private readonly List<Process> _roots = [];

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
        lock (_roots) { _roots.Add(process); }
        if (_handle != 0)
        {
            try { _ = AssignProcessToJobObject(_handle, process.Handle); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { _ = ex; } // exited already
        }
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
        if (handle != 0) { CloseHandle(handle); } // KILL_ON_JOB_CLOSE: background children that left the tree go too
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
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
