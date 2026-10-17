// AgentOs.RemoteAgent — the dev-side agent for the "remote dev-IDE agent" runtime.
// Connects to the API's SignalR hub, receives requests, and executes them locally.
//
// M3 — "Execute": receives a full LLM prompt, runs it through a local CLI (e.g. claude -p),
//       returns the text result.
// M4 — "ExecuteToolCall": receives a single tool-execution request from the server's agentic
//       loop, executes the shell command locally, returns stdout/stderr. The LLM reasoning stays
//       on the server; the runner only provides the execution environment.
//
// Configure via environment variables:
//   REMOTE_AGENT_HUB    hub URL      (default https://localhost:5080/hubs/remote-agent)
//   REMOTE_AGENT_ID     runner id    (the Guid returned by POST /runners)
//   REMOTE_AGENT_TOKEN  pairing token (the plaintext returned ONCE by POST /runners)
//   REMOTE_AGENT_CLI    default CLI-agent profile: "claude" | "codex" (default "claude"). The server can
//                       override this per session; Claude reads the prompt on stdin (`claude -p`), Codex
//                       takes it as an arg (`codex exec "<prompt>"`) — both leverage a flat subscription.
//   REMOTE_AGENT_YOLO   "1" to append the profile's autonomous flags (claude --dangerously-skip-permissions,
//                       codex --full-auto) — the "Run on my machine" opt-in for non-interactive tool use.
//   REMOTE_AGENT_CMD    override the profile's command (advanced)
//   REMOTE_AGENT_ARGS   override the profile's args entirely (advanced; wins over the profile + YOLO)
//   REMOTE_AGENT_ALLOW  shell allowlist for server-driven tool calls (see RunnerCommandPolicy): a comma list
//                       replaces the defaults, "+name" adds, "*" allows everything (explicit opt-out)
//   REMOTE_AGENT_TIMEOUT_SECONDS  per shell command (default 600); on expiry the whole process tree is killed
//
// A shell command outside the allowlist needs approval at THIS console ([y]es / [a]lways this session / [N]o,
// 60 s, default no); a runner with no interactive console denies it. The pairing token travels in a header,
// never in the URL (URLs end up in proxy and server logs).
//
// "Run on my machine" (issue-work routed to the local CLI): the server sends a full agentic prompt
// that expects the CLI to clone, edit, build, and `git push` on its own. For that the CLI must
//   (a) be allowed to run tools non-interactively — set REMOTE_AGENT_ARGS to include a permission
//       flag, e.g. "-p --dangerously-skip-permissions" (this lets the agent run arbitrary shell on
//       THIS machine — your own box + an explicit per-session opt-in), and
//   (b) already have git/gh authenticated to push the target repo — the clone URL carries no token.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

const int MaxOutputBytes = 32 * 1024; // 32 KB cap on tool output

var policy = AgentOs.RemoteAgent.RunnerCommandPolicy.FromSetting(Environment.GetEnvironmentVariable("REMOTE_AGENT_ALLOW"));
var commandTimeout = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("REMOTE_AGENT_TIMEOUT_SECONDS"), out var t) && t > 0 ? t : 600);
var approvedForSession = new HashSet<string>(StringComparer.Ordinal);
var approvalGate = new SemaphoreSlim(1, 1);

var hubUrl = Environment.GetEnvironmentVariable("REMOTE_AGENT_HUB") ?? "https://localhost:5080/hubs/remote-agent";
var runnerId = Environment.GetEnvironmentVariable("REMOTE_AGENT_ID") ?? "";
var token = Environment.GetEnvironmentVariable("REMOTE_AGENT_TOKEN") ?? "";

var connection = new HubConnectionBuilder()
    .WithUrl(hubUrl, options =>
    {
        // Credentials in headers, not the query string (E0.5): the hub reads these first.
        options.Headers["X-AgentOS-Runner-Id"] = runnerId;
        options.Headers["X-AgentOS-Runner-Token"] = token;
    })
    .WithAutomaticReconnect()
    .Build();

// M3 — full-prompt LLM dispatch
connection.On<RemoteExecRequest>("Execute", async request =>
{
    Console.WriteLine($"[agent] Execute {request.Id} (model={request.Model})");
    var result = await RunLlmAsync(request).ConfigureAwait(false);
    await connection.SendAsync("CompleteRequest", result).ConfigureAwait(false);
    // Print WHY on failure. Logging only ok=False made every distinct failure — CLI not on PATH, CLI not
    // logged in, non-zero exit — look identical from the runner console, which is the one place an
    // operator watches while pairing a machine.
    Console.WriteLine(result.Ok
        ? $"[agent] -> {request.Id} ok=True"
        : $"[agent] -> {request.Id} ok=False: {result.Error}");
});

// M4 — server-driven tool execution
connection.On<RunnerToolCall>("ExecuteToolCall", async call =>
{
    Console.WriteLine($"[agent] ExecuteToolCall {call.ToolCallId} ({call.ToolName})");
    var result = await ExecuteToolAsync(call, policy, commandTimeout, ApproveAsync).ConfigureAwait(false);
    await connection.SendAsync("CompleteToolCall", result).ConfigureAwait(false);
    Console.WriteLine($"[agent] -> {call.ToolCallId} ok={result.Ok}");
});

await connection.StartAsync().ConfigureAwait(false);
Console.WriteLine($"[agent] connected to {hubUrl}; runner id={runnerId}. Ctrl+C to exit.");
await Task.Delay(Timeout.Infinite).ConfigureAwait(false);

// ── M3: run the LLM CLI with the prompt piped to stdin ──────────────────────────────────────────

static async Task<RemoteExecResult> RunLlmAsync(RemoteExecRequest request)
{
    // Pick the CLI-agent profile: per-request hint (server-chosen), else REMOTE_AGENT_CLI, else claude.
    var cli = (request.Cli ?? Environment.GetEnvironmentVariable("REMOTE_AGENT_CLI") ?? "claude").Trim();
    var profile = ResolveProfile(cli);

    var cmd = Environment.GetEnvironmentVariable("REMOTE_AGENT_CMD") ?? profile.Command;
    var argsEnv = Environment.GetEnvironmentVariable("REMOTE_AGENT_ARGS");
    var yolo = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REMOTE_AGENT_YOLO"));
    // REMOTE_AGENT_ARGS overrides everything; otherwise the profile's flags + (when opted in) its auto flags.
    var args = argsEnv is not null
        ? argsEnv.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        : yolo ? [.. profile.DefaultArgs, .. profile.AutoArgs] : profile.DefaultArgs;

    var prompt = string.IsNullOrEmpty(request.SystemPrompt)
        ? request.UserPrompt
        : request.SystemPrompt + "\n\n" + request.UserPrompt;

    try
    {
        var psi = new ProcessStartInfo(ResolveExecutable(cmd))
        {
            RedirectStandardInput = profile.PromptViaStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // Claude reads the prompt from stdin (`claude -p`); Codex takes it as a positional arg (`codex exec "<prompt>"`).
        if (!profile.PromptViaStdin)
        {
            psi.ArgumentList.Add(prompt);
        }

        Console.WriteLine($"[agent] cli={cli} cmd={cmd} prompt={(profile.PromptViaStdin ? "stdin" : "arg")}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{cmd}'. Is the {cli} CLI installed and on PATH?");

        if (profile.PromptViaStdin)
        {
            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return new RemoteExecResult(request.Id, true, stdout.Trim(), null);
        }

        // CLI agents routinely print the reason on STDOUT and exit non-zero (claude's
        // "Not logged in · Please run /login" is the case that matters most here), so reporting only
        // stderr produced a bare "exit 1: " and hid the one line that says what to do about it.
        var detail = stderr.Trim();
        if (detail.Length == 0) { detail = stdout.Trim(); }
        return new RemoteExecResult(request.Id, false, string.Empty, $"exit {process.ExitCode}: {detail}");
    }
    catch (System.ComponentModel.Win32Exception ex) { return new RemoteExecResult(request.Id, false, string.Empty, ex.Message); }
    catch (System.IO.IOException ex) { return new RemoteExecResult(request.Id, false, string.Empty, ex.Message); }
    catch (InvalidOperationException ex) { return new RemoteExecResult(request.Id, false, string.Empty, ex.Message); }
    catch (TimeoutException ex) { return new RemoteExecResult(request.Id, false, string.Empty, ex.Message); }
}

// Resolve a bare command name to something CreateProcess can actually launch.
//
// Windows only: with UseShellExecute = false, .NET does NOT apply PATHEXT, so ProcessStartInfo("claude")
// throws Win32Exception "The system cannot find the file specified" — npm installs the CLI as
// claude.cmd / claude.ps1 (plus an extensionless shell script for Git Bash), never claude.exe. That made
// the default profile unusable on Windows: the runner connected, received Execute, and failed on every
// dispatch. Probing PATH with PATHEXT reproduces what a shell would have done. A rooted or already-
// extensioned path is returned untouched, and an unresolved name is returned as-is so the original
// Win32Exception still surfaces with its normal message.
static string ResolveExecutable(string command)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.IsPathRooted(command))
    {
        return command;
    }

    var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        foreach (var ext in extensions)
        {
            var candidate = Path.Join(dir, command + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    return command;
}

// Built-in CLI-agent profiles. REMOTE_AGENT_CMD / REMOTE_AGENT_ARGS still override command + flags.
static CliProfile ResolveProfile(string cli) => cli.ToUpperInvariant() switch
{
    "CODEX" => new CliProfile("codex", PromptViaStdin: false, DefaultArgs: ["exec"], AutoArgs: ["--full-auto"]),
    "CLAUDE" or "CLAUDE-CODE" or "" => new CliProfile("claude", PromptViaStdin: true, DefaultArgs: ["-p"], AutoArgs: ["--dangerously-skip-permissions"]),
    // Unknown name: treat it as the command, claude-style (prompt on stdin).
    _ => new CliProfile(cli, PromptViaStdin: true, DefaultArgs: ["-p"], AutoArgs: ["--dangerously-skip-permissions"]),
};

// ── M4: execute a single tool call (shell command) ──────────────────────────────────────────────

// Ask the person at the console. Serialized (one prompt at a time); 60 s, default deny; "a" approves the exact
// command for the rest of this runner session. No interactive console → deny.
async Task<bool> ApproveAsync(string command, string reason)
{
    if (approvedForSession.Contains(command)) { return true; }
    if (Console.IsInputRedirected || !Environment.UserInteractive)
    {
        Console.WriteLine($"[agent] DENIED (no console to approve; {reason}): {command}");
        return false;
    }
    await approvalGate.WaitAsync().ConfigureAwait(false);
    try
    {
        Console.WriteLine($"[agent] The server asks to run a command that needs approval ({reason}):");
        Console.WriteLine($"          {command}");
        Console.Write("[agent] Allow? [y]es / [a]lways this session / [N]o (60 s): ");
        var read = Task.Run(Console.ReadLine);
        var answer = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) == read
            ? (await read.ConfigureAwait(false))?.Trim().ToLowerInvariant()
            : null;
        Console.WriteLine();
        if (answer == "a") { approvedForSession.Add(command); return true; }
        return answer == "y";
    }
    finally
    {
        approvalGate.Release();
    }
}

static async Task<RunnerToolResult> ExecuteToolAsync(
    RunnerToolCall call, AgentOs.RemoteAgent.RunnerCommandPolicy policy, TimeSpan timeout, Func<string, string, Task<bool>> approve)
{
    try
    {
        if (!string.Equals(call.ToolName, "shell", StringComparison.OrdinalIgnoreCase))
        {
            return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty,
                $"Unknown tool '{call.ToolName}'. Supported: shell.");
        }

        using var doc = JsonDocument.Parse(call.JsonInput);
        var root = doc.RootElement;

        var command = root.GetProperty("command").GetString()
            ?? throw new InvalidOperationException("Missing 'command' in tool input.");

        if (policy.Check(command) is { } needsApproval && !await approve(command, needsApproval).ConfigureAwait(false))
        {
            return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty,
                $"Refused by the runner: {needsApproval}. Approve it at the runner console, or add it to REMOTE_AGENT_ALLOW.");
        }

        string? workingDir = null;
        if (root.TryGetProperty("working_dir", out var wdProp))
        {
            workingDir = wdProp.GetString();
        }

        // Use the OS shell to handle quoting, pipes, and redirections correctly.
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var shell = isWindows ? "cmd.exe" : "sh";
        var shellFlag = isWindows ? "/c" : "-c";

        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(shellFlag);
        psi.ArgumentList.Add(command);
        if (!string.IsNullOrEmpty(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start shell process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using (var deadline = new CancellationTokenSource(timeout))
        {
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Runaway command: kill the whole tree (a shell's children too), report, never hang the session.
                process.Kill(entireProcessTree: true);
                return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty,
                    $"Timed out after {timeout.TotalSeconds:0} s — the process tree was killed.");
            }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // Cap output to avoid flooding the server with huge build logs.
        var combined = string.IsNullOrEmpty(stderr)
            ? stdout
            : stdout + "\n[stderr]\n" + stderr;

        if (combined.Length > MaxOutputBytes)
        {
            combined = combined[..MaxOutputBytes] + $"\n[output truncated at {MaxOutputBytes / 1024} KB]";
        }

        var ok = process.ExitCode == 0;
        return new RunnerToolResult(
            call.RequestId,
            call.ToolCallId,
            ok,
            combined.Trim(),
            ok ? null : $"exit {process.ExitCode}");
    }
    catch (System.ComponentModel.Win32Exception ex) { return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty, ex.Message); }
    catch (System.IO.IOException ex) { return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty, ex.Message); }
    catch (InvalidOperationException ex) { return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty, ex.Message); }
    catch (TimeoutException ex) { return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty, ex.Message); }
    catch (JsonException ex) { return new RunnerToolResult(call.RequestId, call.ToolCallId, false, string.Empty, ex.Message); }
}

// ── DTOs — must match the server's shapes ───────────────────────────────────────────────────────

internal sealed record RemoteExecRequest(string Id, string SystemPrompt, string UserPrompt, string Model, string? Cli = null);
internal sealed record RemoteExecResult(string Id, bool Ok, string Content, string? Error);
internal sealed record RunnerToolCall(string RequestId, string ToolCallId, string ToolName, string JsonInput);
internal sealed record RunnerToolResult(string RequestId, string ToolCallId, bool Ok, string JsonOutput, string? Error);

// How to invoke one subscription CLI agent: the command, whether the prompt goes to stdin or as a
// positional arg, the default flags, and the extra "autonomous" flags appended when REMOTE_AGENT_YOLO
// is set (the "Run on my machine" opt-in that lets the CLI run tools non-interactively on this box).
internal sealed record CliProfile(string Command, bool PromptViaStdin, string[] DefaultArgs, string[] AutoArgs);
