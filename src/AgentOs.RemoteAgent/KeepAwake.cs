// "Use your own computer" means a laptop that idles to sleep: on the lab the machine slept from 08:41 to 20:00 with two
// tasks mid-run, and Autopilot sat still for eleven hours. While this runner has work in flight (a CLI run or a preview)
// it resets the system idle timer every 30 s, so the computer doesn't idle-sleep under the agent. It never prevents a
// sleep the person asks for (lid, Start › Sleep), never keeps the display on, and does nothing once the work is done.
// Windows only; REMOTE_AGENT_KEEP_AWAKE=off opts out.

using System.Runtime.InteropServices;

namespace AgentOs.RemoteAgent;

/// <summary>Keeps the computer from idle-sleeping while work is in flight.</summary>
internal sealed partial class KeepAwake : IDisposable
{
    private const uint SystemRequired = 0x00000001;

    private readonly Timer? _timer;
    private int _active;

    public KeepAwake(bool enabled)
    {
        if (enabled && OperatingSystem.IsWindows())
        {
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>True when the machine setting allows it (anything but "off").</summary>
    public static bool Allowed(string? setting) => !string.Equals(setting?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Work in flight right now.</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>Mark one piece of work as started; dispose the result when it ends.</summary>
    public IDisposable Hold()
    {
        Interlocked.Increment(ref _active);
        Tick();
        return new Lease(this);
    }

    private void Tick()
    {
        // A non-continuous call only resets the idle timer once — repeated while work is in flight, never after.
        if (_timer is not null && Volatile.Read(ref _active) > 0) { _ = SetThreadExecutionState(SystemRequired); }
    }

    public void Dispose() => _timer?.Dispose();

    private sealed class Lease(KeepAwake owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) { Interlocked.Decrement(ref owner._active); }
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SetThreadExecutionState(uint flags);
}
