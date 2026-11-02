// A run leaves nothing behind. A Testing agent that started the project's Aspire AppHost (`dotnet run --project
// infra/X.AppHost`) to try the app left its Postgres, Keycloak, MailHog and DCP tunnel-proxy containers running after
// the run: the job killed the AppHost AND the Aspire orchestrator (DCP) at the same instant, so nobody was left to remove
// the containers — Docker runs them, not the run's process tree. At the end of every run (ok, failed, stopped, timed
// out), before the run folder goes:
//
//   1. Graceful: when one of the run's processes is DCP (`dcp`), every OTHER process of the run is killed first — the
//      AppHost among them. DCP watches its AppHost (`dcp start-apiserver --monitor <AppHost pid>`) and shuts down
//      gracefully when it exits, removing the containers it created. The runner waits for DCP to finish (bounded).
//   2. Force: the job is closed — whatever is still running dies (ProcessJob.cs).
//   3. Containers: Docker is asked for the containers DCP labels with their creator
//      (`com.microsoft.developer.usvc-dev.creatorProcessId` + `…creatorProcessStartTime` — the `dcp run-controllers`
//      process). Only those whose creator pid AND start time match a process sampled in THIS run's job are removed
//      (`docker rm -f`, never `-v`: named volumes are the project's data and stay). The lab stack's or a "Try the app"
//      preview's containers have a creator outside the run's job (a different AppHost, alive, not under the run
//      folder), so they never match. Docker is only asked at all when the run had a DCP process.
//
//   REMOTE_AGENT_KEEP_CONTAINERS  "1" keeps a run's containers (no graceful stop, no removal); processes still end.
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
internal sealed record CleanupOptions(bool KeepContainers, TimeSpan GracefulWait)
{
    /// <summary>How long DCP gets to remove its containers itself after its AppHost is gone.</summary>
    internal static readonly TimeSpan DefaultGracefulWait = TimeSpan.FromSeconds(30);

    /// <summary>From <c>REMOTE_AGENT_KEEP_CONTAINERS</c> ("1"/"true"/"on" keeps them).</summary>
    internal static CleanupOptions FromSetting(string? keepContainers)
    {
        var keep = keepContainers?.Trim() is { Length: > 0 } v
            && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));
        return new CleanupOptions(keep, DefaultGracefulWait);
    }
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

/// <summary>A container DCP created, as <c>docker ps</c> lists it with its creator labels.</summary>
internal sealed record DcpContainer(string Id, string Name, int CreatorPid, DateTime CreatorStartedUtc);

/// <summary>What the cleanup did at the end of one run.</summary>
internal sealed record CleanupOutcome(int ProcessesLeft, bool OrchestratorStoppedItself, IReadOnlyList<string> ContainersRemoved);

/// <summary>The end-of-run cleanup: the run's processes, then the containers its Aspire AppHost left.</summary>
internal static class RunCleanup
{
    internal const string CreatorPidLabel = "com.microsoft.developer.usvc-dev.creatorProcessId";
    internal const string CreatorStartLabel = "com.microsoft.developer.usvc-dev.creatorProcessStartTime";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>Aspire's orchestrator (DCP: <c>dcp</c>, older builds <c>dcpctrl</c>/<c>dcpd</c>).</summary>
    internal static bool IsOrchestrator(RunProcess process) =>
        process.Name.StartsWith("dcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The docker CLI arguments listing every container DCP labelled with its creator.</summary>
    internal static IReadOnlyList<string> ListArgs() =>
    [
        "ps", "-a", "--no-trunc", "--filter", "label=" + CreatorPidLabel,
        "--format", "{{.ID}}|{{.Names}}|{{.Label \"" + CreatorPidLabel + "\"}}|{{.Label \"" + CreatorStartLabel + "\"}}",
    ];

    /// <summary>End a run: let DCP remove its containers, kill what is left, remove what DCP could not. Never throws for
    /// a missing docker or a process that is already gone. Disposes <paramref name="tree"/>.</summary>
    internal static async Task<CleanupOutcome> EndAsync(IRunProcessTree tree, IDockerCli docker, CleanupOptions options,
        Action<string> log, CancellationToken cancellationToken = default)
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
        var removed = await RemoveContainersAsync(docker, tree.Seen, log, cancellationToken).ConfigureAwait(false);
        return new CleanupOutcome(left, stoppedItself, removed);
    }

    /// <summary>Remove the containers whose DCP creator was one of <paramref name="runProcesses"/>. Returns their names.</summary>
    internal static async Task<IReadOnlyList<string>> RemoveContainersAsync(IDockerCli docker, IReadOnlyCollection<RunProcess> runProcesses,
        Action<string> log, CancellationToken cancellationToken)
    {
        var listed = await docker.RunAsync(ListArgs(), cancellationToken).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            log("couldn't list the app's containers (" + Safe(listed.Error) + ")");
            return [];
        }
        var mine = Attributed(Parse(listed.Output), runProcesses);
        if (mine.Count == 0) { return []; }
        var removed = await docker.RunAsync(["rm", "-f", .. mine.Select(c => c.Id)], cancellationToken).ConfigureAwait(false);
        var names = mine.Select(c => c.Name).ToList();
        log(removed.ExitCode == 0
            ? $"removed {names.Count} container(s) the run's app left: {string.Join(", ", names.Select(Safe))} (volumes kept)"
            : $"couldn't remove the run's container(s) {string.Join(", ", names.Select(Safe))}: {Safe(removed.Error)}");
        return removed.ExitCode == 0 ? names : [];
    }

    /// <summary>The containers whose creator (pid AND start time) is one of the run's processes. A container whose creator
    /// is anything else — the lab stack's DCP, a preview's, an older process that reused a pid — is never chosen.</summary>
    internal static IReadOnlyList<DcpContainer> Attributed(IReadOnlyList<DcpContainer> containers, IReadOnlyCollection<RunProcess> runProcesses) =>
        [.. containers.Where(c => runProcesses.Any(p => p.Is(c.CreatorPid, c.CreatorStartedUtc)))];

    /// <summary>Parse <see cref="ListArgs"/> output (<c>id|name|pid|start</c> per line); unparsable lines are skipped.</summary>
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
}

/// <summary>Defence in depth against writes next to the run folders: what appeared in the runs root while runs were in
/// flight, and is not a run folder of the runner's own, is removed once the last of those runs ends.</summary>
internal sealed partial class RunsRootWatch
{
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
    /// runner's own: run folders (<c>yyyyMMdd-HHmmss-&lt;id&gt;</c>, ticket-file deliveries too) and their
    /// <c>.parked</c> markers (RunContinuation.cs).</summary>
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

    /// <summary>A name the runner itself gives entries in the runs root.</summary>
    internal static bool IsRunnersOwn(string name) => RunFolderName().IsMatch(name)
        || (name.EndsWith(RunContinuation.MarkerSuffix, StringComparison.Ordinal) && RunFolderName().IsMatch(name[..^RunContinuation.MarkerSuffix.Length]));

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
