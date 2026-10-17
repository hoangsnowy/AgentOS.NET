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
        // Fail-closed: deny remote/runner exec outside Development unless RemoteAgent:AutoApprove=true.
        // (AutoApproveRemoteExec is retained for tests + explicit dev wiring.)
        services.TryAddSingleton<IRemoteExecApprover, ConfigGatedRemoteExecApprover>();
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor>(
            new RemoteAgentSettings(AgentOs.SharedKernel.Modularity.ModuleEnvironment.IsDevelopment(configuration)));

        // Register the Llm provider as a keyed ILlmClient so Llm.LlmClientFactory can resolve it by name.
        services.AddKeyedTransient<ILlmClient, RemoteAgentLlmClient>("RemoteAgent");

        // M4 — runner_shell: ITool discovered + registered by ToolsModule.InitializeAsync at startup.
        // IHttpContextAccessor resolves the current user's identity from this singleton-scoped tool.
        services.AddHttpContextAccessor();
        services.AddTransient<AgentOs.Domain.Tools.ITool, RunnerShellTool>();

        services.AddSignalR();
        services.AddHostedService<RemoteAgentTransport>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<RemoteAgentHub>(RemoteAgentHub.Path);
    }
}
