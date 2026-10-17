// Fail-closed approver for remote/runner execution. A remote agent runs work on a developer machine
// (full prompts in M3, arbitrary `runner_shell` commands in M4), so an allowlisted LLM that reaches
// this path gets code execution on the paired box. Auto-approving everywhere makes the human-in-the-
// loop gate a no-op — so outside Development we DENY by default and require an explicit opt-in.
//
// Posture (resolved once at construction; IHostEnvironment + IConfiguration are singletons):
//   - `RemoteAgent:AutoApprove` set explicitly  → that value wins (true = unattended exec allowed).
//   - unset                                     → defaults to env.IsDevelopment() (dev: allow, else deny).
// Mirrors the `Tools:EnforceByDefault` convention (safe in production, frictionless in dev).

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Config-gated <see cref="IRemoteExecApprover"/>: allows remote dispatch + runner tool calls
/// only when explicitly opted in via <c>RemoteAgent:AutoApprove</c>, defaulting to allow in Development
/// and deny everywhere else. Replaces <see cref="AutoApproveRemoteExec"/> as the registered default so
/// no deployment ships an unattended remote-exec path by accident.</summary>
public sealed class ConfigGatedRemoteExecApprover : IRemoteExecApprover
{
    /// <summary>Configuration key that explicitly opts in (or out of) unattended remote execution.</summary>
    public const string AutoApproveKey = "RemoteAgent:AutoApprove";

    private readonly bool _allow;
    private readonly AgentOs.Domain.Configuration.ISettings? _settings;

    public ConfigGatedRemoteExecApprover(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<ConfigGatedRemoteExecApprover> logger,
        AgentOs.Domain.Configuration.ISettings? settings = null)
    {
        _settings = settings;
        System.ArgumentNullException.ThrowIfNull(environment);
        System.ArgumentNullException.ThrowIfNull(configuration);
        System.ArgumentNullException.ThrowIfNull(logger);

        var configured = configuration.GetValue<bool?>(AutoApproveKey);
        _allow = configured ?? environment.IsDevelopment();

        if (_allow && !environment.IsDevelopment())
        {
            logger.LogWarning(
                "[RemoteAgent] {Key}=true outside Development — dispatched prompts and runner_shell tool " +
                "calls execute UNATTENDED on paired machines. Ensure the per-tenant tool-policy allowlist is tight.",
                AutoApproveKey);
        }
        else if (!_allow)
        {
            logger.LogWarning(
                "[RemoteAgent] Remote-exec approval gate is DENY-by-default in environment '{Environment}'. " +
                "Remote dispatch and runner_shell are refused until {Key}=true is set explicitly.",
                environment.EnvironmentName, AutoApproveKey);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ApproveAsync(RemoteExecRequest request, CancellationToken cancellationToken = default)
        // Live from Settings (platform admin; production pins it via Settings:Locks) when the registry is
        // wired — same key, so an explicit RemoteAgent:AutoApprove in config still seeds it.
        => _settings is null
            ? _allow
            : await AgentOs.Domain.Configuration.SettingsExtensions.GetBoolAsync(
                _settings, AutoApproveKey, null, _allow, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> ApproveToolCallAsync(RunnerToolCall toolCall, CancellationToken cancellationToken = default)
        => Task.FromResult(_allow);
}
