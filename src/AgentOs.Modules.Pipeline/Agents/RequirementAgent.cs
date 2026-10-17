// Phase 4 — IRequirementAgent impl. Analyzes a user story → a structured JSON RequirementSpec.
// The LLM-call / parse / validate / metrics / error skeleton lives in LlmAgentBase.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.Modules.Pipeline.Validation;
using AgentOs.Domain;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Requirements;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Analyzes a user story → a structured JSON <see cref="RequirementSpec"/>.</summary>
public sealed class RequirementAgent : LlmAgentBase, IRequirementAgent
{
    /// <summary>Initializes.</summary>
    public RequirementAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILlmOutputValidator validator,
        IMetricsCollector collector,
        ILogger<RequirementAgent> logger,
        IPromptOverrides? prompts = null,
        IAgentProfiles? profiles = null)
        : base(factory, Slice(options), collector, logger, validator, prompts, profiles)
    {
    }

    private static AgentOptions Slice(IOptions<AgentsOptions> options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        return options.Value.Requirement;
    }

    protected override string PromptKey => "Requirement";

    protected override string DefaultSystemPrompt => RequirementPrompt.System;

    protected override string? SchemaName => SchemaNames.RequirementSpecV1;

    /// <inheritdoc />
    public Task<RequirementSpec> RunAsync(UserStory story, CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(story);
        story.Validate();
        return ExecuteAsync<RequirementSpecDto, RequirementSpec>(
            RequirementPrompt.RenderUser(story),
            dto => dto.Validate(AgentName),
            Map,
            LogSuccess,
            cancellationToken);
    }

    private void LogSuccess(AgentMetrics metrics, RequirementSpecDto dto)
        => Logger.LogInformation(
            "{Agent} done: {InTok}→{OutTok} tokens, ${Cost} USD, {Ms}ms ({Provider} {Model})",
            AgentName, metrics.InputTokens, metrics.OutputTokens, metrics.CostUsd,
            metrics.Latency.TotalMilliseconds, metrics.Provider, metrics.Model);

    private static RequirementSpec Map(RequirementSpecDto dto, AgentMetrics metrics)
        => new(
            Title: dto.Title!,
            Summary: dto.Summary!,
            Stakeholders: dto.Stakeholders ?? [],
            FunctionalRequirements: dto.FunctionalRequirements ?? [],
            NonFunctionalRequirements: dto.NonFunctionalRequirements ?? [],
            Entities: (dto.Entities ?? []).Select(e => new EntityDescriptor(e.Name!, e.Fields ?? [], e.Notes)).ToArray(),
            Endpoints: (dto.Endpoints ?? []).Select(e => new EndpointDescriptor(e.Method!, e.Path!, e.Purpose!, e.AuthRequired)).ToArray(),
            AcceptanceCriteria: dto.AcceptanceCriteria ?? [],
            Metrics: metrics);

    // ---- DTOs for JSON deserialization (matching the schema in the SystemPrompt) ----
    private sealed class RequirementSpecDto
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public List<string>? Stakeholders { get; set; }
        public List<string>? FunctionalRequirements { get; set; }
        public List<string>? NonFunctionalRequirements { get; set; }
        public List<EntityDto>? Entities { get; set; }
        public List<EndpointDto>? Endpoints { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }

        public void Validate(string agentName)
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                throw new LlmException($"{agentName}: missing 'title'.", agentName);
            }
            if (string.IsNullOrWhiteSpace(Summary))
            {
                throw new LlmException($"{agentName}: missing 'summary'.", agentName);
            }
            if (Entities is null || Entities.Count == 0)
            {
                throw new LlmException($"{agentName}: 'entities' must have ≥ 1 item.", agentName);
            }
            if (Endpoints is null || Endpoints.Count == 0)
            {
                throw new LlmException($"{agentName}: 'endpoints' must have ≥ 1 item.", agentName);
            }
        }
    }

    private sealed class EntityDto
    {
        public string? Name { get; set; }
        public List<string>? Fields { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class EndpointDto
    {
        public string? Method { get; set; }
        public string? Path { get; set; }
        public string? Purpose { get; set; }
        public bool AuthRequired { get; set; }
    }
}
