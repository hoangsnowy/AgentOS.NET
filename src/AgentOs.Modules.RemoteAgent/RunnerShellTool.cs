// M4 — "runner_shell": an ITool that executes a shell command on the paired dev-machine runner.
// The server-side LLM loop decides WHAT to run; the runner provides the local execution environment
// (the real codebase, build toolchain, git, etc.). This is the primary "server thinks, runner does"
// primitive: build, test, file-ops, git commits all route through this tool.
//
// IHttpContextAccessor is used to read the current request's tenant + user claims, because ITool
// instances in IToolRegistry are singletons — they cannot capture a request-scoped ITenantContext
// directly. IHttpContextAccessor is the standard ASP.NET Core pattern for singletons that need
// per-request identity.

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Http;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Executes a shell command on the caller's paired dev-machine runner. Available when
/// a runner is connected for the current session member. Tool name: <c>runner_shell</c>.</summary>
public sealed class RunnerShellTool : ITool
{
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    // S3 — bound concurrent runner_shell dispatches PER TENANT so a single tenant (or a runaway agent
    // loop) cannot saturate the dispatch path or hammer a paired dev machine. In-process only; a
    // multi-instance deploy needs a distributed gate (deferred — coherence plan Phase 5/F).
    internal const int MaxConcurrentPerTenant = 4;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantGates = new(StringComparer.Ordinal);

    public ToolDefinition Definition { get; } = new(
        Name: "runner_shell",
        Description:
            "Execute a shell command on the paired dev-machine runner and return stdout/stderr. " +
            "Use for build, test, file I/O, git, or any command that needs the real local codebase. " +
            "Only available when a runner is connected for the current session member.",
        JsonInputSchema: """
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Shell command to execute on the runner (e.g. \"dotnet build ./src\")."
                },
                "working_dir": {
                  "type": "string",
                  "description": "Working directory on the runner. Optional; defaults to the runner's cwd."
                }
              },
              "required": ["command"]
            }
            """);

    private readonly IRemoteAgentBroker _broker;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISessionRunFeed? _feed;

    public RunnerShellTool(IRemoteAgentBroker broker, IHttpContextAccessor httpContextAccessor, ISessionRunFeed? feed = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        // Optional: live session-run feed. When the work runs under a session (AmbientIdentity carries
        // a SessionId), each shell command is published as a Step so the Board activity feed shows it.
        _feed = feed;
    }

    /// <inheritdoc />
    public async Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string command;
        string? workingDir = null;
        try
        {
            using var doc = JsonDocument.Parse(request.Input);
            var root = doc.RootElement;
            command = root.GetProperty("command").GetString()
                ?? throw new ToolException("'command' must not be null.");
            if (root.TryGetProperty("working_dir", out var wdProp))
            {
                workingDir = wdProp.GetString();
            }
        }
        catch (JsonException ex) { throw new ToolException($"runner_shell: invalid input JSON — {ex.Message}", ex); }
        catch (System.Collections.Generic.KeyNotFoundException ex) { throw new ToolException($"runner_shell: invalid input JSON — {ex.Message}", ex); }
        catch (InvalidOperationException ex) { throw new ToolException($"runner_shell: invalid input JSON — {ex.Message}", ex); }
        catch (ArgumentException ex) { throw new ToolException($"runner_shell: invalid input JSON — {ex.Message}", ex); }

        var target = ResolveTarget(request.TenantId);
        if (!_broker.HasRunnerFor(target))
        {
            throw new ToolException(
                "runner_shell: no runner connected for the current session member. " +
                "Register a runner and start the AgentOS.NET remote agent on your dev machine.");
        }

        var toolCallId = Guid.NewGuid().ToString("N");
        var payload = workingDir is not null
            ? JsonSerializer.Serialize(new { command, working_dir = workingDir })
            : JsonSerializer.Serialize(new { command });

        var call = new RunnerToolCall(
            RequestId: request.CallId,
            ToolCallId: toolCallId,
            ToolName: "shell",
            JsonInput: payload);

        // S3 — per-tenant concurrency gate around the dispatch.
        var gate = _tenantGates.GetOrAdd(target.TenantId, _ => new SemaphoreSlim(MaxConcurrentPerTenant, MaxConcurrentPerTenant));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmitCommand($"$ {Truncate(command, 160)}");

            RunnerToolResult result;
            try
            {
                result = await _broker.DispatchToolCallAsync(call, target, ToolTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                EmitCommand($"⏱ command timed out after {ToolTimeout.TotalSeconds:0}s");
                throw new ToolException($"runner_shell: runner timed out after {ToolTimeout.TotalSeconds:0}s.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new ToolException($"runner_shell: {ex.Message}", ex);
            }

            EmitCommand(result.Ok ? "✓ command ok" : $"✗ command failed: {Truncate(result.Error, 160)}");

            return result.Ok
                ? ToolInvocationResult.Success(request.CallId, result.JsonOutput)
                : ToolInvocationResult.Error(request.CallId, result.Error ?? "Runner reported failure.");
        }
        finally
        {
            gate.Release();
        }
    }

    // Publish a per-command line to the session's live feed. No-op unless a feed is registered AND the
    // ambient identity carries a session id (i.e. the work runs under a tracked session). Never throws.
    private void EmitCommand(string message)
    {
        if (_feed is null)
        {
            return;
        }
        var amb = AgentOs.SharedKernel.Identity.AmbientIdentity.Current;
        if (amb?.SessionId is not { } sessionId)
        {
            return;
        }
        try
        {
            _feed.Publish(new SessionRunEvent(
                amb.TenantId, sessionId, SessionRunEventKind.Step, message, DateTimeOffset.UtcNow));
        }
        // Best-effort telemetry — a feed hiccup must never break the tool call.
        catch (ObjectDisposedException ex) { _ = ex.Message; }
        catch (IOException ex) { _ = ex.Message; }
        catch (InvalidOperationException ex) { _ = ex.Message; }
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private RunnerTarget ResolveTarget(string tenantIdFallback)
    {
        // Background work (a Blazor circuit's Task.Run) has no HttpContext; the session seeds
        // AmbientIdentity so the dispatch still targets the right member's runner.
        if (AgentOs.SharedKernel.Identity.AmbientIdentity.Current is { } amb)
        {
            return new RunnerTarget(amb.TenantId, amb.UserId ?? string.Empty);
        }

        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            return new RunnerTarget(tenantIdFallback, string.Empty);
        }

        var tenantId = ctx.User.GetTenantId() ?? tenantIdFallback;
        var userId = ctx.User.GetUserId() ?? string.Empty;

        return new RunnerTarget(tenantId, userId);
    }
}
