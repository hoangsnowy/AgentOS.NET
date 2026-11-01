// AgentOs.Infrastructure/Agents/DependencyInjection.cs
// Phase 4 — Registers the 5 agents + PipelineOrchestrator + AgentsOptions + PipelineOptions into DI.

using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Modules.Pipeline.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>DI extension for the agents layer (call after <c>AddLlmGateway</c>).</summary>
public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Register:
    /// <list type="bullet">
    ///   <item>Bind <see cref="AgentsOptions"/> + <see cref="PipelineOptions"/>.</item>
    ///   <item>4 specialist agents (Requirement / Coding / Testing / QA).</item>
    ///   <item><see cref="PipelineOrchestrator"/> implements <see cref="IOrchestratorAgent"/>.</item>
    ///   <item><see cref="IPipelineProgressSink"/> defaults to no-op (a realtime host overrides it later).</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        System.ArgumentNullException.ThrowIfNull(services);
        System.ArgumentNullException.ThrowIfNull(configuration);

        // Fail-fast config: a bad agent/pipeline value aborts startup with a named error instead of
        // surfacing mid-run as a 404 from the LLM provider or a zero-iteration pipeline.
        services.AddOptions<AgentsOptions>()
            .Bind(configuration.GetSection(AgentsOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "Agents:* — every agent needs a Provider, Temperature in [0, 2] and MaxTokens > 0; every Workflow-pipeline agent needs a Model (a Board agent's empty Model = the computer's / provider's default).")
            .ValidateOnStart();
        // UI-first settings (ADR-0011): agent profiles + pipeline knobs, editable in the desktop.
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor, AgentSettings>();
        // E0.6: per-tenant run admission (concurrent runs + runs per minute), shared by every run entry point.
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor, Limits.RunLimitSettings>();
        services.TryAddSingleton<AgentOs.Domain.Pipeline.IRunAdmission>(sp =>
            new Limits.RunAdmission(sp.GetService<AgentOs.Domain.Configuration.ISettings>(), sp.GetService<TimeProvider>()));
        // Values a renamed role saved under its former key (Decomposer → Discovery) keep applying until re-saved.
        services.TryAddSingleton(sp => new LegacyAgentSettings(
            sp.GetService<AgentOs.Modules.AppConfig.IAppConfigStore>(), sp.GetService<IConfiguration>()));
        services.AddSingleton<IAgentProfiles>(sp =>
            sp.GetService<AgentOs.Domain.Configuration.ISettings>() is { } settings
                ? new SettingsAgentProfiles(settings, sp.GetService<IServiceScopeFactory>(), sp.GetService<LegacyAgentSettings>())
                : StaticAgentProfiles.Instance);

        services.AddOptions<PipelineOptions>()
            .Bind(configuration.GetSection(PipelineOptions.SectionName))
            .Validate(o => o.MaxIterations >= 1,
                "Pipeline:MaxIterations must be >= 1.")
            .Validate(o => string.Equals(o.Engine, "Classic", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Engine, "Workflow", System.StringComparison.OrdinalIgnoreCase),
                "Pipeline:Engine must be 'Classic' or 'Workflow'.")
            .ValidateOnStart();

        services.AddTransient<IRequirementAgent, RequirementAgent>();
        services.AddTransient<AgentOs.Domain.Sessions.IIssueWorkAgent, IssueWorkAgent>();
        services.AddTransient<AgentOs.Domain.Sessions.IAcceptanceAgent, AcceptanceAgent>();
        services.AddTransient<AgentOs.Domain.Sessions.IReviewAgent, ReviewAgent>();
        services.AddTransient<AgentOs.Domain.Sessions.IDesignAgent, DesignAgent>();
        services.AddTransient<AgentOs.Domain.Memory.IRetrospectiveAgent, RetrospectiveAgent>();
        services.AddTransient<AgentOs.Domain.Sessions.IAutopilotOrchestrator, AutopilotOrchestratorAgent>();
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsContributor, AutopilotSettings>();
        services.AddTransient<ICodingAgent, CodingAgent>();
        services.AddTransient<ITestingAgent, TestingAgent>();
        services.AddTransient<IQaAgent, QaAgent>();

        // Workflow graph executor: runs an OrchestrationStudio graph against these typed agents. Hosts depend
        // on the Domain facade (AgentOs.Domain.Pipeline.Graph.IGraphExecutor), not the concrete type.
        services.AddScoped<GraphExecution.GraphExecutor>();
        services.AddScoped<AgentOs.Domain.Pipeline.Graph.IGraphExecutor>(sp => sp.GetRequiredService<GraphExecution.GraphExecutor>());

        // Bootstrap (idea → board tickets): the decomposer agent + the idea→preview service.
        services.AddTransient<Decomposition.ITicketDecomposerAgent, Decomposition.TicketDecomposerAgent>();
        services.AddTransient<Decomposition.IIdeaBootstrapService, Decomposition.IdeaBootstrapService>();

        // IOrchestratorAgent = PersistingOrchestratorAgent wrapping PipelineOrchestrator.
        // Persists run + metrics best-effort (no-op if the DB is not configured via AddPersistence).
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<PipelineOrchestrator>();
        services.AddTransient<MafWorkflowOrchestrator>();   // platform-v2: MAF Workflows graph engine
        services.AddTransient<IOrchestratorAgent>(sp =>
        {
            var engine = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>().Value.Engine;
            IOrchestratorAgent inner = string.Equals(engine, "Workflow", System.StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<MafWorkflowOrchestrator>()
                : sp.GetRequiredService<PipelineOrchestrator>();
            return new PersistingOrchestratorAgent(
                inner,
                sp.GetRequiredService<IPipelineRunRepository>(),
                sp.GetRequiredService<AgentOs.Domain.Cost.IBudgetGuard>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<AgentOs.SharedKernel.Identity.ITenantContext>(),
                sp.GetRequiredService<ILogger<PersistingOrchestratorAgent>>(),
                sp.GetService<AgentOs.Domain.Pipeline.IRunAdmission>());
        });

        // Defaults to no-op — a host needing realtime (Blazor) overrides it with a scoped registration after AddAgents.
        services.TryAddSingleton<IPipelineProgressSink>(NullPipelineProgressSink.Instance);

        return services;
    }
}
