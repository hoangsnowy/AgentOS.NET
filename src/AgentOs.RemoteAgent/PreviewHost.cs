// "Try the app": run a project on THIS computer so its person can click through it — the latest default branch, started
// with the recipe in its `.agentos/preview.json` (AgentOs.Domain.Preview.PreviewRecipe, linked into this project).
//
//   1. Get the latest version into a per-repo checkout under the previews folder (reused, so restores stay warm).
//   2. Re-read the recipe from that checkout: it must be exactly the command + url the person was shown, else refuse.
//   3. Run the command in the checkout, in a process group of its own (PreviewProcess.cs) with its state on disk
//      (PreviewStore); ready once something accepts connections on the url's port.
//   4. The app outlives the runner: a restarted runner re-attaches every preview whose process is still the same one
//      (pid + start time + still the shell it started), announces it to the server, and reports the ones it found gone
//      (Restore / Announcements). A state file it can't use (unreadable, or a board/repo/url the Board can't place) is
//      not left running unseen: its app is stopped — but only when that pid is still the preview's own process.
//   5. Stop = kill the whole process tree — also of a re-attached preview. A preview nobody has had open for its idle
//      time (Settings › Autopilot › Stop an unused app after, default 120 min) stops by itself. `AgentOs.RemoteAgent
//      stop-previews` (and `unpair`) stop every preview on this computer (StopAll).
//   6. The app's log is rotated at PreviewStore.MaxLogBytes (one older part kept).
//
// One preview per repository: starting it again replaces the old one.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AgentOs.Domain.Preview;

namespace AgentOs.RemoteAgent;

/// <summary>A preview the server asked for — mirrors the server's <c>PreviewStart</c>.</summary>
internal sealed record PreviewStart(string PreviewId, string Owner, string Repo, string Branch, string Command, string Url, int ReadyTimeoutSeconds,
    int IdleMinutes = 0, Guid BoardId = default, Guid RepoId = default);

/// <summary>Runs "Try the app" previews on this computer.</summary>
/// <param name="root">The previews folder (checkouts + the <c>running</c> state folder).</param>
/// <param name="report">Progress to the server: preview id, state, message.</param>
/// <param name="awake">Keeps the computer from idle-sleeping while a preview runs.</param>
/// <param name="inUse">True while something is connected to the port (a browser) — the idle clock; null = the OS's TCP table.</param>
internal sealed class PreviewHost(string root, Func<string, string, string?, Task> report, KeepAwake? awake = null, Func<int, bool>? inUse = null)
{
    /// <summary>The idle stop when the server didn't send one (minutes).</summary>
    internal const int DefaultIdleMinutes = 120;

    private const int TailLines = 30;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Running> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idByRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _gone = new(StringComparer.Ordinal);
    private readonly Func<int, bool> _inUse = inUse ?? HasClientOn;

    private sealed class Running(string id, string repoKey, string dir, PreviewStart? start)
    {
        public string Id { get; } = id;
        public string RepoKey { get; } = repoKey;
        public string Dir { get; } = dir;
        public PreviewStart? Start { get; } = start;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public Process? Process { get; set; }
        public volatile PreviewRecord? Record;
        public IDisposable? Group { get; set; }
        public IDisposable? Awake { get; set; }
        public CancellationTokenSource Stop { get; } = new();
    }

    /// <summary>The previews folder next to the runs folder (%LOCALAPPDATA%\AgentOS.NET\previews on Windows).</summary>
    internal static string DefaultRoot() =>
        Path.Join(Path.GetDirectoryName(ScopedAutonomy.DefaultRoot()) ?? ScopedAutonomy.DefaultRoot(), "previews");

    /// <summary>Owner / repo names as GitHub allows them — they become a folder name and a clone URL.</summary>
    internal static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 100 && name[0] != '.' && name[0] != '-'
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>A branch name that can't be read as a git option or escape the ref namespace.</summary>
    internal static bool IsSafeBranch(string branch) =>
        branch.Length is > 0 and <= 200 && branch[0] != '-' && !branch.Contains("..", StringComparison.Ordinal)
        && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');

    /// <summary>The idle stop to apply: the server's value when it is in range, else <see cref="DefaultIdleMinutes"/>.</summary>
    internal static int IdleMinutesFor(int fromServer) => fromServer is >= 10 and <= 1440 ? fromServer : DefaultIdleMinutes;

    /// <summary>True once a preview nobody uses has been idle for its idle time.</summary>
    internal static bool IsIdle(DateTimeOffset lastUsed, DateTimeOffset now, int idleMinutes) =>
        now - lastUsed >= TimeSpan.FromMinutes(IdleMinutesFor(idleMinutes));

    /// <summary>Start (or restart) a preview. Returns at once; progress goes through the report callback.</summary>
    public void Start(PreviewStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!PreviewStore.IsSafeId(start.PreviewId))
        {
            return; // not an id the server makes — nothing to report it under
        }
        if (!IsSafeName(start.Owner) || !IsSafeName(start.Repo) || !IsSafeBranch(start.Branch))
        {
            _ = report(start.PreviewId, "Failed", "That repository or branch name can't be used.");
            return;
        }
        var repoKey = $"{start.Owner}/{start.Repo}";
        if (_idByRepo.TryGetValue(repoKey, out var previous)) { Stop(previous); }
        var run = new Running(start.PreviewId, repoKey, Path.Join(Path.GetFullPath(root), $"{start.Owner}-{start.Repo}"), start)
        {
            Awake = awake?.Hold(), // a running preview keeps the computer from idle-sleeping (KeepAwake.cs)
        };
        _byId[run.Id] = run;
        _idByRepo[repoKey] = run.Id;
        _ = Task.Run(() => RunAsync(run, start));
    }

    /// <summary>Stop a preview and everything it started — also one re-attached after a runner restart. Unknown id = already gone.</summary>
    public void Stop(string previewId)
    {
        if (!_byId.TryRemove(previewId, out var run)) { return; }
        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
        run.Stop.Cancel();
        Kill(run);
        Forget(run);
        _ = report(run.Id, "Stopped", null);
    }

    /// <summary>The runner is exiting: let go of the previews WITHOUT stopping them — they keep running, and the next
    /// runner re-attaches them from their state files (<see cref="Restore"/>).</summary>
    public void Detach()
    {
        foreach (var run in _byId.Values)
        {
            run.Stop.Cancel();
            run.Awake?.Dispose();
            run.Group?.Dispose(); // Windows: no kill-on-close; the app's own processes keep the job (and its name) alive
        }
    }

    /// <summary>Re-attach the previews an earlier runner left running (their state files). A preview whose process is gone
    /// — or whose pid now belongs to another program (start time differs) — is forgotten without touching that process.
    /// Returns the ids found gone; they are also reported in <see cref="Announcements"/>.</summary>
    public IReadOnlyList<string> Restore()
    {
        var gone = new List<string>();
        var unreadable = new List<PreviewRecord>();
        foreach (var record in PreviewStore.Load(root, unreadable))
        {
            if (_byId.ContainsKey(record.PreviewId)) { continue; }
            var repoKey = $"{record.Owner}/{record.Repo}";
            if (!CanReattach(record) || !PreviewProcess.IsSame(record.Pid, record.ProcessStartTicks))
            {
                // Gone — or a state the Board can't place. Whatever of it still runs is stopped (Kill acts only on the
                // preview's own process, group or job), so nothing keeps running unseen.
                if (PreviewProcess.Kill(record)) { Console.WriteLine($"[agent] preview {PreviewProcess.Describe(record)} can't be shown on the Board — stopped"); }
                PreviewStore.Delete(root, record.PreviewId);
                gone.Add(record.PreviewId);
                continue;
            }
            var run = new Running(record.PreviewId, repoKey, Path.Join(Path.GetFullPath(root), $"{record.Owner}-{record.Repo}"), null)
            {
                Record = record,
                Awake = awake?.Hold(),
            };
            if (!_idByRepo.TryAdd(repoKey, run.Id))
            {
                // Two state files for one repo can't both be the app on its port: keep the first, stop this one.
                Kill(run);
                Forget(run);
                gone.Add(record.PreviewId);
                continue;
            }
            _byId[run.Id] = run;
            Console.WriteLine($"[agent] preview {PreviewProcess.Describe(record)} is still running — re-attached");
            _ = Task.Run(() => WatchAsync(run, run.Stop.Token));
        }
        foreach (var broken in unreadable)
        {
            var stopped = PreviewProcess.Kill(broken);
            Console.WriteLine(stopped
                ? $"[agent] a preview's state file was unreadable; its app (pid {broken.Pid}) was still running — stopped"
                : "[agent] a preview's state file was unreadable and was removed");
            if (PreviewStore.IsSafeId(broken.PreviewId)) { gone.Add(broken.PreviewId); }
        }
        foreach (var id in gone) { _gone[id] = 0; }
        return gone;
    }

    /// <summary>Stop every preview recorded on this computer — <c>AgentOs.RemoteAgent stop-previews</c> / <c>unpair</c>, for a
    /// runner that is going away for good. Only processes that are still the previews' own are touched. Returns how many
    /// were still running.</summary>
    public static int StopAll(string root)
    {
        var unreadable = new List<PreviewRecord>();
        var stopped = 0;
        foreach (var record in PreviewStore.Load(root, unreadable).Concat(unreadable))
        {
            if (PreviewProcess.Kill(record))
            {
                stopped++;
                Console.WriteLine($"[agent] stopped preview {PreviewProcess.Describe(record)}");
            }
            PreviewStore.Delete(root, record.PreviewId);
        }
        return stopped;
    }

    // A state file the Board can place: GitHub names, the board + repo it was started for, and a local http(s) url.
    private static bool CanReattach(PreviewRecord r) =>
        IsSafeName(r.Owner) && IsSafeName(r.Repo) && r.BoardId != Guid.Empty && r.RepoId != Guid.Empty && PreviewRecipe.IsLocalHttpUrl(r.Url);

    /// <summary>What to announce on (re)connect: every preview this runner runs (also one still preparing), and the ids
    /// <see cref="Restore"/> found gone.</summary>
    public (PreviewAnnouncement[] Running, string[] Gone) Announcements()
    {
        var running = new List<PreviewAnnouncement>();
        foreach (var run in _byId.Values)
        {
            if (run.Record is { } r)
            {
                running.Add(new PreviewAnnouncement(r.PreviewId, r.Owner, r.Repo, r.Command, r.Url, r.StartedAt, r.BoardId, r.RepoId,
                    r.Ready ? "Ready" : "Starting"));
            }
            else if (run.Start is { } s)
            {
                running.Add(new PreviewAnnouncement(s.PreviewId, s.Owner, s.Repo, s.Command, s.Url, run.CreatedAt, s.BoardId, s.RepoId, "Preparing"));
            }
        }
        return ([.. running], [.. _gone.Keys]);
    }

    /// <summary>Previews that left their state behind under the old scheme (a <c>preview.pid</c> in the checkout, from a
    /// runner before re-attaching existed) — kill them when they are still the same process.</summary>
    public static void KillLeftovers(string root)
    {
        if (!Directory.Exists(root)) { return; }
        foreach (var pidFile in Directory.EnumerateFiles(root, "preview.pid", SearchOption.AllDirectories).Take(64))
        {
            try
            {
                var parts = File.ReadAllText(pidFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid)
                    && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var started))
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.StartTime.ToUniversalTime().Ticks == started) { p.Kill(entireProcessTree: true); }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException)
            {
                _ = ex; // already gone, or not ours to touch
            }
            finally
            {
                try { File.Delete(pidFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
            }
        }
    }

    private async Task RunAsync(Running run, PreviewStart start)
    {
        var ct = run.Stop.Token;
        try
        {
            await report(run.Id, "Preparing", "Getting the latest version…").ConfigureAwait(false);
            var fetched = await GetLatestAsync(run.Dir, start, ct).ConfigureAwait(false);
            if (fetched is not null) { await FailAsync(run, fetched).ConfigureAwait(false); return; }

            var recipePath = Path.Join(run.Dir, ".agentos", "preview.json");
            if (!File.Exists(recipePath))
            {
                await FailAsync(run, "The project has no .agentos/preview.json.").ConfigureAwait(false);
                return;
            }
            var recipe = PreviewRecipe.TryParse(await File.ReadAllTextAsync(recipePath, ct).ConfigureAwait(false), out var error);
            if (recipe is null)
            {
                await FailAsync(run, $"The recipe can't be used: {error}").ConfigureAwait(false);
                return;
            }
            if (!string.Equals(recipe.Command, start.Command, StringComparison.Ordinal)
                || !string.Equals(recipe.Url.AbsoluteUri, new Uri(start.Url).AbsoluteUri, StringComparison.Ordinal))
            {
                await FailAsync(run, "The project's recipe changed since you looked — open Try the app again to see the new one.").ConfigureAwait(false);
                return;
            }

            // Readiness is "something answers on the port" — so the port must be free before we start, or a leftover
            // server (another checkout's dev server) would read as this app being up.
            if (await IsListeningAsync(recipe.Url, ct).ConfigureAwait(false))
            {
                await FailAsync(run, $"Something else on this computer is already using port {recipe.Url.Port}. Close that app (or stop its preview), then try again.").ConfigureAwait(false);
                return;
            }
            await report(run.Id, "Starting", $"Running {recipe.Command}").ConfigureAwait(false);
            Console.WriteLine($"[agent] preview {run.RepoKey}: {recipe.Command} (in {run.Dir})");
            PreviewStore.ResetLog(root, run.Id);
            // The test data this computer offers the agents (REMOTE_AGENT_DATA) reaches the app too: a project that loads
            // real data when it is there (e.g. TeacherHub's CHESS_LIBRARY_PATH library) shows it in Try the app.
            var launched = PreviewProcess.Launch(recipe.Command, run.Dir, PreviewStore.LogPath(root, run.Id), run.Id,
                TestData.Parse(Environment.GetEnvironmentVariable("REMOTE_AGENT_DATA")));
            run.Process = launched.Process;
            run.Group = launched.Group;
            var record = new PreviewRecord(run.Id, start.Owner, start.Repo, recipe.Command, recipe.Url.AbsoluteUri, recipe.Url.Port,
                recipe.ReadyTimeoutSeconds, IdleMinutesFor(start.IdleMinutes), start.BoardId, start.RepoId, launched.Process.Id,
                launched.StartTicks, launched.OwnGroup, DateTimeOffset.UtcNow, Ready: false, launched.OwnJob);
            run.Record = record;
            if (ct.IsCancellationRequested) { Kill(run); return; } // stopped while it was starting
            PreviewStore.Save(root, record);
            await WatchAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stopped (or the runner is exiting — the app runs on) */ }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
            or UnauthorizedAccessException or UriFormatException)
        {
            await FailAsync(run, ex.Message).ConfigureAwait(false);
        }
    }

    // Watch a started (or re-attached) preview until it is ready, then until it ends, is stopped, or sits unused too long.
    private async Task WatchAsync(Running run, CancellationToken ct)
    {
        try
        {
            var record = run.Record!;
            var url = new Uri(record.Url);
            var deadline = record.StartedAt.AddSeconds(Math.Clamp(record.ReadyTimeoutSeconds, 10, 3600));
            var lastUsed = DateTimeOffset.UtcNow;
            var nextIdleCheck = lastUsed;
            var nextLogCheck = lastUsed;
            while (!ct.IsCancellationRequested)
            {
                if (!IsAlive(run))
                {
                    await EndedAsync(run, record).ConfigureAwait(false);
                    return;
                }
                var now = DateTimeOffset.UtcNow;
                if (now >= nextLogCheck)
                {
                    nextLogCheck = now + IdleCheck;
                    _ = PreviewStore.CapLog(root, run.Id); // a chatty app can't fill the disk over a long session
                }
                if (!record.Ready)
                {
                    if (await IsListeningAsync(url, ct).ConfigureAwait(false))
                    {
                        record = record with { Ready = true };
                        run.Record = record;
                        TrySave(record);
                        lastUsed = now;
                        await report(run.Id, "Ready", url.AbsoluteUri).ConfigureAwait(false);
                    }
                    else if (now > deadline)
                    {
                        await FailAsync(run, $"Nothing answered at {url} after {record.ReadyTimeoutSeconds} s.\n{PreviewStore.Tail(root, run.Id, TailLines)}").ConfigureAwait(false);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
                if (now >= nextIdleCheck)
                {
                    nextIdleCheck = now + IdleCheck;
                    if (_inUse(record.Port)) { lastUsed = now; }
                    else if (IsIdle(lastUsed, now, record.IdleMinutes) && _byId.TryRemove(run.Id, out _))
                    {
                        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
                        Kill(run);
                        Forget(run);
                        Console.WriteLine($"[agent] preview {run.RepoKey} stopped: unused for {record.IdleMinutes} min");
                        await report(run.Id, "Stopped", $"Stopped after {record.IdleMinutes} minutes without anyone using it.").ConfigureAwait(false);
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped (or the runner is exiting — the app runs on) */ }
    }

    // Ended on its own (crashed, or someone closed it) — not via Stop.
    private async Task EndedAsync(Running run, PreviewRecord record)
    {
        if (!_byId.TryRemove(run.Id, out _)) { return; }
        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
        var exit = run.Process is { } p && TryExitCode(p) is { } code ? string.Create(CultureInfo.InvariantCulture, $" (exit {code})") : "";
        var tail = PreviewStore.Tail(root, run.Id, TailLines);
        Kill(run); // whatever it left running in the background goes with it
        Forget(run);
        await (record.Ready
            ? report(run.Id, "Stopped", $"The app ended{exit}.\n{tail}".Trim())
            : report(run.Id, "Failed", $"The app stopped{exit}.\n{tail}".Trim())).ConfigureAwait(false);
    }

    private async Task FailAsync(Running run, string message)
    {
        if (!_byId.TryRemove(run.Id, out _)) { return; } // stopped meanwhile — that report already went out
        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
        Kill(run);
        Forget(run);
        Console.WriteLine($"[agent] preview {run.RepoKey} failed: {message.Split('\n')[0]}");
        await report(run.Id, "Failed", message.Trim()).ConfigureAwait(false);
    }

    private static bool IsAlive(Running run)
    {
        if (run.Process is { } p)
        {
            try { return !p.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
        return run.Record is { } r && PreviewProcess.IsSame(r.Pid, r.ProcessStartTicks);
    }

    private static int? TryExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private void TrySave(PreviewRecord record)
    {
        try { PreviewStore.Save(root, record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex; // the app runs on; only a re-attach after a restart would miss the newer state
        }
    }

    // True while a client (a browser tab, its live-reload socket) is connected to the app's port.
    private static bool HasClientOn(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Any(c => c.State == TcpState.Established && c.LocalEndPoint.Port == port);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return true; // can't tell: never stop an app on a guess
        }
    }

    // Clone once, then fetch + hard reset to the branch tip (this folder is AgentOS's own; nobody edits it).
    private static async Task<string?> GetLatestAsync(string dir, PreviewStart start, CancellationToken ct)
    {
        if (Directory.Exists(Path.Join(dir, ".git")))
        {
            var fetch = await GitAsync(dir, ct, "fetch", "--depth", "1", "origin", start.Branch).ConfigureAwait(false);
            if (fetch is not null) { return $"Couldn't get the latest version: {fetch}"; }
            var reset = await GitAsync(dir, ct, "reset", "--hard", "FETCH_HEAD").ConfigureAwait(false);
            return reset is null ? null : $"Couldn't update the copy on this computer: {reset}";
        }
        ScopedAutonomy.TryDelete(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var url = $"https://github.com/{start.Owner}/{start.Repo}.git";
        var clone = await GitAsync(Path.GetDirectoryName(dir)!, ct, "clone", "--depth", "1", "--branch", start.Branch, "--", url, dir).ConfigureAwait(false);
        return clone is null ? null : $"Couldn't download the project (is git signed in to GitHub on this computer?): {clone}";
    }

    // Null on success, else the last line git printed.
    private static async Task<string?> GitAsync(string workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git didn't start.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            return "git took too long.";
        }
        if (process.ExitCode == 0) { return null; }
        var lines = (await stderr.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : $"git exited {process.ExitCode}.";
    }

    /// <summary>True once something accepts a TCP connection on the url's host and port.</summary>
    internal static async Task<bool> IsListeningAsync(Uri url, CancellationToken ct)
    {
        // Both loopback families at once: an app may listen on only one of them, and on Windows a refused loopback
        // connect takes ~2 s (SYN retries) — trying "localhost" address by address ran out the clock on ::1.
        System.Net.IPAddress[] targets = System.Net.IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : [System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback];
        var results = await Task.WhenAll(targets.Select(a => ConnectsAsync(a, url.Port, ct))).ConfigureAwait(false);
        return results.Contains(true);
    }

    private static async Task<bool> ConnectsAsync(System.Net.IPAddress address, int port, CancellationToken ct)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // Kill the preview's whole tree (its group / job too) and let go of what the runner held for it.
    private static void Kill(Running run)
    {
        if (run.Record is { } record) { PreviewProcess.Kill(record, run.Process); }
        else if (run.Process is { } p)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { _ = ex; }
        }
        run.Group?.Dispose();
        run.Awake?.Dispose();
        run.Process?.Dispose();
    }

    private void Forget(Running run) => PreviewStore.Delete(root, run.Id);
}
