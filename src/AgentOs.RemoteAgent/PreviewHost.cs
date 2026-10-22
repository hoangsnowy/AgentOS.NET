// "Try the app": run a project on THIS computer so its person can click through it — the latest default branch, started
// with the recipe in its `.agentos/preview.json` (AgentOs.Domain.Preview.PreviewRecipe, linked into this project).
//
//   1. Get the latest version into a per-repo checkout under the previews folder (reused, so restores stay warm).
//   2. Re-read the recipe from that checkout: it must be exactly the command + url the person was shown, else refuse.
//   3. Run the command in the checkout; ready once something accepts connections on the url's port.
//   4. Stop = kill the whole process tree. The runner stops every preview when it exits, and on start kills any it left
//      behind after a hard kill (a pid file with the process start time, so a recycled pid is never touched).
//
// One preview per repository: starting it again replaces the old one.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AgentOs.Domain.Preview;

namespace AgentOs.RemoteAgent;

/// <summary>A preview the server asked for — mirrors the server's <c>PreviewStart</c>.</summary>
internal sealed record PreviewStart(string PreviewId, string Owner, string Repo, string Branch, string Command, string Url, int ReadyTimeoutSeconds);

/// <summary>Runs "Try the app" previews on this computer.</summary>
internal sealed class PreviewHost(string root, Func<string, string, string?, Task> report)
{
    private const int TailLines = 30;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<string, Running> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idByRepo = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Running(string id, string repoKey, string dir)
    {
        public string Id { get; } = id;
        public string RepoKey { get; } = repoKey;
        public string Dir { get; } = dir;
        public Process? Process { get; set; }
        public ProcessJob Job { get; } = ProcessJob.Create();
        public CancellationTokenSource Stop { get; } = new();
        public Queue<string> Tail { get; } = new();
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

    /// <summary>Start (or restart) a preview. Returns at once; progress goes through the report callback.</summary>
    public void Start(PreviewStart start)
    {
        if (!IsSafeName(start.Owner) || !IsSafeName(start.Repo) || !IsSafeBranch(start.Branch))
        {
            _ = report(start.PreviewId, "Failed", "That repository or branch name can't be used.");
            return;
        }
        var repoKey = $"{start.Owner}/{start.Repo}";
        if (_idByRepo.TryGetValue(repoKey, out var previous)) { Stop(previous); }
        var run = new Running(start.PreviewId, repoKey, Path.Join(Path.GetFullPath(root), $"{start.Owner}-{start.Repo}"));
        _byId[run.Id] = run;
        _idByRepo[repoKey] = run.Id;
        _ = Task.Run(() => RunAsync(run, start));
    }

    /// <summary>Stop a preview and everything it started. Unknown id = already gone.</summary>
    public void Stop(string previewId)
    {
        if (!_byId.TryRemove(previewId, out var run)) { return; }
        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
        run.Stop.Cancel();
        Kill(run);
        _ = report(run.Id, "Stopped", null);
    }

    /// <summary>Stop everything (the runner is exiting).</summary>
    public void StopAll()
    {
        foreach (var id in _byId.Keys.ToList()) { Stop(id); }
    }

    /// <summary>Kill previews a previous runner left running when it was killed outright (no chance to stop them).</summary>
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
                try { File.Delete(pidFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
            var process = Process.Start(ShellFor(recipe.Command, run.Dir))
                ?? throw new InvalidOperationException("The command didn't start.");
            run.Process = process;
            run.Job.Add(process);
            WritePidFile(run.Dir, process);
            process.OutputDataReceived += (_, e) => Remember(run, e.Data);
            process.ErrorDataReceived += (_, e) => Remember(run, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(recipe.ReadyTimeoutSeconds, 10, 3600));
            while (!ct.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    await FailAsync(run, $"The app stopped (exit {process.ExitCode}).\n{TailOf(run)}").ConfigureAwait(false);
                    return;
                }
                if (await IsListeningAsync(recipe.Url, ct).ConfigureAwait(false)) { break; }
                if (DateTime.UtcNow > deadline)
                {
                    await FailAsync(run, $"Nothing answered at {recipe.Url} after {recipe.ReadyTimeoutSeconds} s.\n{TailOf(run)}").ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested) { return; }
            await report(run.Id, "Ready", recipe.Url.AbsoluteUri).ConfigureAwait(false);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // Ended on its own (crashed, or someone closed it) — not via Stop.
            if (_byId.TryRemove(run.Id, out _))
            {
                _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
                run.Job.Dispose(); // whatever it left running in the background goes with it
                await report(run.Id, "Stopped", $"The app ended (exit {process.ExitCode}).\n{TailOf(run)}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
            or UnauthorizedAccessException or UriFormatException)
        {
            await FailAsync(run, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(Running run, string message)
    {
        if (!_byId.TryRemove(run.Id, out _)) { return; } // stopped meanwhile — that report already went out
        _idByRepo.TryRemove(new KeyValuePair<string, string>(run.RepoKey, run.Id));
        Kill(run);
        Console.WriteLine($"[agent] preview {run.RepoKey} failed: {message.Split('\n')[0]}");
        await report(run.Id, "Failed", message.Trim()).ConfigureAwait(false);
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

    /// <summary>The OS shell running one recipe line in <paramref name="dir"/>.</summary>
    internal static ProcessStartInfo ShellFor(string command, string dir)
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        if (windows)
        {
            // /s: strip exactly the outer quotes and keep the line as written (quotes inside it included).
            psi.Arguments = "/d /s /c \"" + command + "\"";
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
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

    private static void WritePidFile(string dir, Process process)
    {
        try
        {
            File.WriteAllText(Path.Join(dir, "preview.pid"),
                string.Create(CultureInfo.InvariantCulture, $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ = ex; // only the leftover sweep needs it
        }
    }

    private static void Kill(Running run)
    {
        run.Job.Dispose(); // the app's whole tree, background children included
        try { File.Delete(Path.Join(run.Dir, "preview.pid")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    private static void Remember(Running run, string? line)
    {
        if (line is null) { return; }
        lock (run.Tail)
        {
            run.Tail.Enqueue(line.Length > 300 ? line[..300] : line);
            while (run.Tail.Count > TailLines) { run.Tail.Dequeue(); }
        }
    }

    private static string TailOf(Running run)
    {
        lock (run.Tail) { return string.Join('\n', run.Tail); }
    }
}
