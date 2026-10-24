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

        services.AddSignalR();
        services.AddHostedService<RemoteAgentTransport>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<RemoteAgentHub>(RemoteAgentHub.Path);
    }
}
