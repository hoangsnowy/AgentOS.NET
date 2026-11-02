// A run leaves nothing behind. A Testing agent that started the project's Aspire AppHost (`dotnet run --project
// infra/X.AppHost`) to try the app left its Postgres, Keycloak, MailHog and DCP tunnel-proxy containers running after
// the run — and its session network and data volume: the job killed the AppHost AND the Aspire orchestrator (DCP) at the
// same instant, so nobody was left to remove them — Docker runs them, not the run's process tree. At the end of every
// run (ok, failed, stopped, timed out), before the run folder goes:
//
//   1. Graceful: when one of the run's processes is DCP (`dcp`), every OTHER process of the run is killed first — the
//      AppHost among them. DCP watches its AppHost (`dcp start-apiserver --monitor <AppHost pid>`) and shuts down
//      gracefully when it exits, removing the containers and network it created. The runner waits (bounded).
//   2. Force: the job is closed — whatever is still running dies (ProcessJob.cs).
//   3. Docker: DCP labels its containers AND its `aspire-session-network-*` networks with their creator
//      (`com.microsoft.developer.usvc-dev.creatorProcessId` + `…creatorProcessStartTime` — the `dcp run-controllers`
//      process). Only those whose creator pid AND start time match a process sampled in THIS run's job are removed:
//      containers with `docker rm -f -v` (persistent-lifetime ones too — the run created them; `-v` removes their
//      anonymous volumes only), then networks with `docker network rm`, then the NAMED volumes those containers mounted
//      that were created during the run (`docker volume rm` — each run folder gives the AppHost a new volume name, so a
//      run's volume is never reused; an older volume a run merely mounted stays). The lab stack's or a "Try the app"
//      preview's containers and networks have a creator outside the run's job (a different AppHost, alive, not under the
//      run folder), so they never match. Docker is only asked at all when the run had a DCP process.
//
//   REMOTE_AGENT_KEEP_CONTAINERS  "1" keeps a run's containers, networks and volumes (no graceful stop, no removal);
//                                 processes still end.
//   REMOTE_AGENT_KEEP_VOLUMES     "1" keeps the volumes (containers and networks still go; `docker rm -f` without -v).
//
// Defence in depth for writes outside the run folder (RunsRootWatch): an entry that appeared next to the run folders
// while runs were in flight and is not a run folder of the runner's own (a `../base-122` a run created to compare
// against main) is removed when the last of those runs ends — under the runner's default runs root; under a custom
// REMOTE_AGENT_WORKROOT it is only reported.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentOs.RemoteAgent;

/// <summary>How the end-of-run cleanup behaves on this computer.</summary>
internal sealed record CleanupOptions(bool KeepContainers, TimeSpan GracefulWait, bool KeepVolumes = false)
{
    /// <summary>How long DCP gets to remove its containers itself after its AppHost is gone.</summary>
    internal static readonly TimeSpan DefaultGracefulWait = TimeSpan.FromSeconds(30);

    /// <summary>From <c>REMOTE_AGENT_KEEP_CONTAINERS</c> / <c>REMOTE_AGENT_KEEP_VOLUMES</c> ("1"/"true"/"on" keeps them).</summary>
    internal static CleanupOptions FromSetting(string? keepContainers, string? keepVolumes = null)
    {
        var containers = IsOn(keepContainers);
        return new CleanupOptions(containers, DefaultGracefulWait, containers || IsOn(keepVolumes));
    }

    private static bool IsOn(string? setting) => setting?.Trim() is { Length: > 0 } v
        && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));
}

/// <summary>What one docker CLI call returned.</summary>
internal readonly record struct DockerResult(int ExitCode, string Output, string Error);

/// <summary>The docker CLI (a fake in tests).</summary>
internal interface IDockerCli
{
    /// <summary>Run <c>docker</c> with these arguments; a missing CLI is exit code -1, never an exception.</summary>
    Task<DockerResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>The real docker CLI on PATH, each call bounded to a minute.</summary>
internal sealed class DockerCli : IDockerCli
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<DockerResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        try
        {
            using var process = Process.Start(psi);
            if (process is null) { return new DockerResult(-1, string.Empty, "docker did not start"); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CallTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new DockerResult(-1, string.Empty, "docker did not answer in time");
            }
            return new DockerResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new DockerResult(-1, string.Empty, ex.Message); // no docker on this computer
        }
    }
}

/// <summary>A container or network DCP created, as <c>docker ps</c> / <c>docker network ls</c> lists it with its creator labels.</summary>
internal sealed record DcpContainer(string Id, string Name, int CreatorPid, DateTime CreatorStartedUtc);

/// <summary>What the cleanup did at the end of one run.</summary>
internal sealed record CleanupOutcome(int ProcessesLeft, bool OrchestratorStoppedItself, IReadOnlyList<string> ContainersRemoved)
{
    /// <summary>The run's Aspire networks removed.</summary>
    public IReadOnlyList<string> NetworksRemoved { get; init; } = [];

    /// <summary>The named volumes the run created and its containers mounted, removed.</summary>
    public IReadOnlyList<string> VolumesRemoved { get; init; } = [];
}

/// <summary>The end-of-run cleanup: the run's processes, then the containers, networks and volumes its Aspire AppHost left.</summary>
internal static partial class RunCleanup
{
    internal const string CreatorPidLabel = "com.microsoft.developer.usvc-dev.creatorProcessId";
    internal const string CreatorStartLabel = "com.microsoft.developer.usvc-dev.creatorProcessStartTime";

    private const string LabelFormat = "|{{.Label \"" + CreatorPidLabel + "\"}}|{{.Label \"" + CreatorStartLabel + "\"}}";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>Aspire's orchestrator (DCP: <c>dcp</c>, older builds <c>dcpctrl</c>/<c>dcpd</c>).</summary>
    internal static bool IsOrchestrator(RunProcess process) =>
        process.Name.StartsWith("dcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The docker CLI arguments listing every container DCP labelled with its creator.</summary>
    internal static IReadOnlyList<string> ListArgs() =>
        ["ps", "-a", "--no-trunc", "--filter", "label=" + CreatorPidLabel, "--format", "{{.ID}}|{{.Names}}" + LabelFormat];

    /// <summary>The docker CLI arguments listing every network DCP labelled with its creator.</summary>
    internal static IReadOnlyList<string> NetworkListArgs() =>
        ["network", "ls", "--no-trunc", "--filter", "label=" + CreatorPidLabel, "--format", "{{.ID}}|{{.Name}}" + LabelFormat];

    /// <summary>End a run: let DCP remove what it created, kill what is left, remove what DCP could not. Never throws for
    /// a missing docker or a process that is already gone. Disposes <paramref name="tree"/>.</summary>
    internal static async Task<CleanupOutcome> EndAsync(IRunProcessTree tree, IDockerCli docker, CleanupOptions options,
        Action<string> log, DateTime runStartedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var stoppedItself = false;
        int left;
        using (tree) // force on the way out: whatever is still running goes
        {
            tree.Sample();
            var alive = tree.Members();
            left = alive.Count;
            var orchestrators = alive.Where(IsOrchestrator).ToList();
            if (!options.KeepContainers && orchestrators.Count > 0)
            {
                // The AppHost (and the rest of the run) first: DCP notices its AppHost is gone and removes its containers.
                foreach (var p in alive.Where(p => !IsOrchestrator(p))) { tree.Kill(p); }
                var deadline = DateTime.UtcNow + options.GracefulWait;
                while (orchestrators.Exists(tree.IsAlive) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
                }
                stoppedItself = !orchestrators.Exists(tree.IsAlive);
                log(stoppedItself
                    ? "the app's orchestrator (Aspire DCP) stopped its containers itself"
                    : $"the app's orchestrator (Aspire DCP) did not stop within {options.GracefulWait.TotalSeconds:0} s — forcing");
            }
        }
        if (left > 0) { log($"stopped {left} process(es) the run left running"); }

        if (options.KeepContainers || !tree.Seen.Any(IsOrchestrator)) { return new CleanupOutcome(left, stoppedItself, []); }
        var seen = tree.Seen;
        var (containers, volumes) = await RemoveContainersAsync(docker, seen, options.KeepVolumes, log, cancellationToken).ConfigureAwait(false);
        var networks = await RemoveNetworksAsync(docker, seen, log, cancellationToken).ConfigureAwait(false);
        var removedVolumes = options.KeepVolumes || volumes.Count == 0
            ? []
            : await RemoveVolumesAsync(docker, volumes, runStartedUtc, log, cancellationToken).ConfigureAwait(false);
        return new CleanupOutcome(left, stoppedItself, containers) { NetworksRemoved = networks, VolumesRemoved = removedVolumes };
    }

    /// <summary>Remove the containers whose DCP creator was one of <paramref name="runProcesses"/>. Returns their names and
    /// the named volumes they mounted (asked before the containers go).</summary>
    internal static async Task<(IReadOnlyList<string> Removed, IReadOnlyList<string> Volumes)> RemoveContainersAsync(IDockerCli docker,
        IReadOnlyCollection<RunProcess> runProcesses, bool keepVolumes, Action<string> log, CancellationToken cancellationToken)
    {
        var listed = await docker.RunAsync(ListArgs(), cancellationToken).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            log("couldn't list the app's containers (" + Safe(listed.Error) + ")");
            return ([], []);
        }
        var mine = Attributed(Parse(listed.Output), runProcesses);
        if (mine.Count == 0) { return ([], []); }
        IReadOnlyList<string> volumes = [];
        if (!keepVolumes)
        {
            var mounts = await docker.RunAsync(
                ["inspect", "--format", "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}} {{end}}{{end}}", .. mine.Select(c => c.Id)],
                cancellationToken).ConfigureAwait(false);
            volumes = NamedVolumes(mounts.Output);
        }
        string[] rm = keepVolumes ? ["rm", "-f"] : ["rm", "-f", "-v"];
        var removed = await docker.RunAsync([.. rm, .. mine.Select(c => c.Id)], cancellationToken).ConfigureAwait(false);
        var names = mine.Select(c => c.Name).ToList();
        log(removed.ExitCode == 0
            ? $"removed {names.Count} container(s) the run's app left: {string.Join(", ", names.Select(Safe))}"
            : $"couldn't remove the run's container(s) {string.Join(", ", names.Select(Safe))}: {Safe(removed.Error)}");
        return removed.ExitCode == 0 ? (names, volumes) : ([], []);
    }

    /// <summary>Remove the Aspire session networks whose DCP creator was one of <paramref name="runProcesses"/> (Docker's
    /// default address pools hold ~31 bridge networks: orphaned ones end in every AppHost on the computer failing to start).</summary>
    internal static async Task<IReadOnlyList<string>> RemoveNetworksAsync(IDockerCli docker, IReadOnlyCollection<RunProcess> runProcesses,
        Action<string> log, CancellationToken cancellationToken)
    {
        var listed = await docker.RunAsync(NetworkListArgs(), cancellationToken).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            log("couldn't list the app's networks (" + Safe(listed.Error) + ")");
            return [];
        }
        var mine = Attributed(Parse(listed.Output), runProcesses);
        if (mine.Count == 0) { return []; }
        var removed = await docker.RunAsync(["network", "rm", .. mine.Select(n => n.Id)], cancellationToken).ConfigureAwait(false);
        var names = mine.Select(n => n.Name).ToList();
        log(removed.ExitCode == 0
            ? $"removed {names.Count} network(s) the run's app left: {string.Join(", ", names.Select(Safe))}"
            : $"couldn't remove the run's network(s) {string.Join(", ", names.Select(Safe))}: {Safe(removed.Error)}");
        return removed.ExitCode == 0 ? names : [];
    }

    /// <summary>Remove the named volumes the run's containers mounted that were created at or after the run's start. An
    /// older volume (the project's data from before, shared with other runs) is kept.</summary>
    internal static async Task<IReadOnlyList<string>> RemoveVolumesAsync(IDockerCli docker, IReadOnlyList<string> volumes,
        DateTime runStartedUtc, Action<string> log, CancellationToken cancellationToken)
    {
        // `volume inspect` prints the volumes it found even when one is gone (exit 1): read the output either way.
        var inspected = await docker.RunAsync(["volume", "inspect", "--format", "{{.Name}}|{{.CreatedAt}}", .. volumes], cancellationToken)
            .ConfigureAwait(false);
        var created = VolumesCreatedSince(inspected.Output, runStartedUtc - TimeSpan.FromSeconds(1));
        if (created.Count == 0) { return []; }
        var removed = await docker.RunAsync(["volume", "rm", .. created], cancellationToken).ConfigureAwait(false);
        log(removed.ExitCode == 0
            ? $"removed {created.Count} volume(s) the run's app created: {string.Join(", ", created.Select(Safe))}"
            : $"couldn't remove the run's volume(s) {string.Join(", ", created.Select(Safe))}: {Safe(removed.Error)}");
        return removed.ExitCode == 0 ? created : [];
    }

    /// <summary>The named volumes in <c>docker inspect</c>'s mount listing — not the anonymous ones (64 hex characters),
    /// which <c>rm -v</c> removes with their container.</summary>
    internal static IReadOnlyList<string> NamedVolumes(string output) =>
        [.. output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !(v.Length == 64 && v.All(char.IsAsciiHexDigitLower)) && VolumeName().IsMatch(v))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The volumes in <c>volume inspect</c> output (<c>name|createdAt</c>) created at or after <paramref name="sinceUtc"/>.</summary>
    internal static IReadOnlyList<string> VolumesCreatedSince(string output, DateTime sinceUtc) =>
        [.. output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length == 2 && VolumeName().IsMatch(parts[0])
                && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && at.UtcDateTime >= sinceUtc)
            .Select(parts => parts[0])];

    /// <summary>The containers or networks whose creator (pid AND start time) is one of the run's processes. One whose
    /// creator is anything else — the lab stack's DCP, a preview's, an older process that reused a pid — is never chosen.</summary>
    internal static IReadOnlyList<DcpContainer> Attributed(IReadOnlyList<DcpContainer> containers, IReadOnlyCollection<RunProcess> runProcesses) =>
        [.. containers.Where(c => runProcesses.Any(p => p.Is(c.CreatorPid, c.CreatorStartedUtc)))];

    /// <summary>Parse <see cref="ListArgs"/> / <see cref="NetworkListArgs"/> output (<c>id|name|pid|start</c> per line);
    /// unparsable lines are skipped.</summary>
    internal static IReadOnlyList<DcpContainer> Parse(string output) =>
        [.. output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseLine).OfType<DcpContainer>()];

    private static DcpContainer? ParseLine(string line)
    {
        var parts = line.Split('|');
        if (parts.Length != 4 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiHexDigit)) { return null; }
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) { return null; }
        return DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
            ? new DcpContainer(parts[0], parts[1], pid, started.UtcDateTime)
            : null;
    }

    // Console-safe: no control characters (a crafted container name can't forge a log line), bounded.
    internal static string Safe(string text)
    {
        var clean = new string([.. text.Trim().Select(c => char.IsControl(c) ? ' ' : c)]);
        return clean.Length <= 300 ? clean : clean[..300] + "…";
    }

    // Docker's volume name rule (a name never starts with '-', so it can't be read as an option).
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeName();
}

/// <summary>Defence in depth against writes next to the run folders: what appeared in the runs root while runs were in
/// flight, and is not a run folder of the runner's own, is removed once the last of those runs ends.</summary>
internal sealed partial class RunsRootWatch
{
    /// <summary>The suffix of a run's own temp folder, beside its run folder (ScopedWriteGuard.TempFolderFor).</summary>
    internal const string TempSuffix = ".tmp";

    private readonly object _gate = new();
    private int _active;
    private DateTime _windowStartUtc;

    /// <summary>The one watch of this runner.</summary>
    internal static RunsRootWatch Shared { get; } = new();

    /// <summary>A run begins (before its folder is created).</summary>
    internal void Begin(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_active++ == 0) { _windowStartUtc = nowUtc - TimeSpan.FromSeconds(1); }
        }
    }

    /// <summary>A run ended. When it was the last one in flight, sweep <paramref name="root"/>: entries created since the
    /// first of those runs began that are not the runner's own are removed (<paramref name="remove"/>) or only reported.
    /// Returns the names found.</summary>
    internal IReadOnlyList<string> End(string root, bool remove, Action<string> log)
    {
        DateTime since;
        lock (_gate)
        {
            if (--_active > 0) { return []; }
            _active = 0;
            since = _windowStartUtc;
        }
        var strays = Strays(root, since);
        if (strays.Count == 0) { return []; }
        var names = strays.Select(Path.GetFileName).OfType<string>().ToList();
        if (!remove)
        {
            log($"found {names.Count} item(s) a run created next to the run folders (not removed — custom work root): {string.Join(", ", names.Select(RunCleanup.Safe))}");
            return names;
        }
        foreach (var stray in strays)
        {
            if (Directory.Exists(stray)) { ScopedAutonomy.TryDelete(stray); }
            else { TryDeleteFile(stray); }
        }
        log($"removed {names.Count} item(s) a run created outside its folder, next to the run folders: {string.Join(", ", names.Select(RunCleanup.Safe))}");
        return names;
    }

    /// <summary>Entries of <paramref name="root"/> created at or after <paramref name="sinceUtc"/> that are not the
    /// runner's own: run folders (<c>yyyyMMdd-HHmmss-&lt;id&gt;</c>, ticket-file deliveries too), their <c>.parked</c>
    /// markers (RunContinuation.cs) and their <c>.tmp</c> folders.</summary>
    internal static IReadOnlyList<string> Strays(string root, DateTime sinceUtc)
    {
        try
        {
            if (!Directory.Exists(root)) { return []; }
            return [.. Directory.EnumerateFileSystemEntries(root)
                .Where(e => !IsRunnersOwn(Path.GetFileName(e)) && CreatedUtc(e) >= sinceUtc)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return [];
        }
    }

    /// <summary>At start (no run in flight): remove the temp folders runs of an earlier runner left. Returns how many.</summary>
    internal static int SweepTempLeftovers(string root)
    {
        try
        {
            if (!Directory.Exists(root)) { return 0; }
            var leftovers = Directory.EnumerateDirectories(root, "*" + TempSuffix)
                .Where(d => RunFolderName().IsMatch(Path.GetFileName(d)[..^TempSuffix.Length])).ToList();
            foreach (var dir in leftovers) { ScopedAutonomy.TryDelete(dir); }
            return leftovers.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return 0;
        }
    }

    /// <summary>A name the runner itself gives entries in the runs root.</summary>
    internal static bool IsRunnersOwn(string name) => RunFolderName().IsMatch(name)
        || IsRunFolderWith(name, RunContinuation.MarkerSuffix) || IsRunFolderWith(name, TempSuffix);

    private static bool IsRunFolderWith(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal) && RunFolderName().IsMatch(name[..^suffix.Length]);

    private static DateTime CreatedUtc(string path) =>
        Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; } // held open — the next sweep
    }

    // ScopedAutonomy.CreateRunDirectory: "yyyyMMdd-HHmmss-" + a request id of letters, digits and '-'.
    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunFolderName();
}
