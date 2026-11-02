// Module entry: registers the broker + approver + Llm provider, wires SignalR, and mounts the
// /hubs/remote-agent endpoint. Hooks the dispatch transport as a hosted service.

using System;
using AgentOs.Domain.Llm;
using AgentOs.SharedKernel.Modularity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.RemoteAgent;

public sealed class RemoteAgentModule : IModule, IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IRemoteAgentBroker, InProcessRemoteAgentBroker>();
        // "Try the app" rides the same connections: the in-process broker is also the preview seam.
        // GAP-04: revoking a runner aborts its live connection (the broker is the disconnector). Never a stand-in: a
        // disconnector that isn't the broker holding the connections would make every revoke a silent no-op.
        services.TryAddSingleton<AgentOs.Domain.Sessions.IRunnerDisconnector>(sp =>
            sp.GetRequiredService<IRemoteAgentBroker>() as AgentOs.Domain.Sessions.IRunnerDisconnector
            ?? throw new InvalidOperationException(
                "The registered IRemoteAgentBroker does not implement IRunnerDisconnector, so revoking a runner could not cut off "
                + "its live connection. Register an IRunnerDisconnector that drops the broker's connections."));
        services.TryAddSingleton<IRunnerPreviewBroker>(sp =>
            sp.GetRequiredService<IRemoteAgentBroker>() as IRunnerPreviewBroker ?? new InProcessRemoteAgentBroker());
        // Fail-closed: deny remote/runner exec outside Development unless RemoteAgent:AutoApprove=true.
        // (AutoApproveRemoteExec is retained for tests + explicit dev wiring.)
        services.TryAddSingleton<IRemoteExecApprover, ConfigGatedRemoteExecApprover>();
        // A member's Claude/Codex plan limit, as their computer reported it — Autopilot waits it out (PlanLimit.cs).
        services.TryAddSingleton<IPlanLimits>(_ => new PlanLimitTracker(TimeProvider.System));
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor>(
            new RemoteAgentSettings(AgentOs.SharedKernel.Modularity.ModuleEnvironment.IsDevelopment(configuration)));

        // Register the Llm provider as a keyed ILlmClient so Llm.LlmClientFactory can resolve it by name.
        services.AddKeyedTransient<ILlmClient, RemoteAgentLlmClient>("RemoteAgent");

        // M4 — runner_shell: ITool discovered + registered by ToolsModule.InitializeAsync at startup.
        // IHttpContextAccessor resolves the current user's identity from this singleton-scoped tool.
        services.AddHttpContextAccessor();
        services.AddTransient<AgentOs.Domain.Tools.ITool, RunnerShellTool>();
        // Ticket files ahead of a server-driven run (ADR-0015); a CLI run carries them with its dispatch instead.
        services.TryAddSingleton<AgentOs.Domain.Files.IRunnerFileDelivery, RunnerFileDeliveryService>();

        // A runner sends screenshots back in pieces of RunUploadRules.ChunkBytes (256 KB, ~342 KB as base64 JSON) — above
        // SignalR's 32 KB default for what a server receives. Raised for this hub only (Blazor's circuits keep theirs).
        services.AddSignalR().AddHubOptions<RemoteAgentHub>(o => o.MaximumReceiveMessageSize = RemoteAgentHub.MaxReceiveBytes);
        services.AddHostedService<RemoteAgentTransport>();
        // Backstop for revokes this instance didn't see (the other host's Board, a member removed elsewhere, a direct DB
        // change): live runners are re-checked against their row and cut off once it is revoked or gone.
        services.AddHostedService<RunnerRevalidationService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<RemoteAgentHub>(RemoteAgentHub.Path);
    }
}
