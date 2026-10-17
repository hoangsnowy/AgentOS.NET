// Phase 4 — ICodingAgent impl. Generates C# Clean Architecture source code from a RequirementSpec.
// The LLM-call / parse / validate / metrics / error skeleton lives in LlmAgentBase; this agent supplies
// only the Coding-specific prompt, schema, DTO validation, mapping, and success log.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Prompts;
using AgentOs.Modules.Pipeline.Validation;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Generates C# Clean Architecture source code from a requirement spec (+ optional QA feedback).</summary>
public sealed class CodingAgent : LlmAgentBase, ICodingAgent
{
    /// <summary>Initializes.</summary>
    public CodingAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILlmOutputValidator validator,
        IMetricsCollector collector,
        ILogger<CodingAgent> logger,
        IPromptOverrides? prompts = null,
        IAgentProfiles? profiles = null)
        : base(factory, Slice(options), collector, logger, validator, prompts, profiles)
    {
    }

    private static AgentOptions Slice(IOptions<AgentsOptions> options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        return options.Value.Coding;
    }

    protected override string PromptKey => "Coding";

    protected override string DefaultSystemPrompt => CodingPrompt.System;

    protected override string? SchemaName => SchemaNames.CodeArtifactV1;

    /// <inheritdoc />
    public Task<CodeArtifact> RunAsync(
        RequirementSpec spec,
        QaReport? previousFeedback = null,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(spec);
        return ExecuteAsync<CodeArtifactDto, CodeArtifact>(
            CodingPrompt.RenderUser(spec, previousFeedback),
            dto => dto.Validate(AgentName),
            Map,
            LogSuccess,
            cancellationToken);
    }

    private static CodeArtifact Map(CodeArtifactDto dto, AgentMetrics metrics)
        => new(
            ProjectName: dto.ProjectName!,
            Architecture: dto.Architecture ?? "Clean Architecture",
            Files: dto.Files!.Select(f => new CodeFile(f.Path!, f.Content ?? string.Empty, f.Language ?? "csharp")).ToArray(),
            Notes: dto.Notes,
            Metrics: metrics);

    private void LogSuccess(AgentMetrics metrics, CodeArtifactDto dto)
        => Logger.LogInformation(
            "{Agent} done: {InTok}→{OutTok} tokens, ${Cost} USD, {Ms}ms — {FileCount} files",
            AgentName, metrics.InputTokens, metrics.OutputTokens, metrics.CostUsd,
            metrics.Latency.TotalMilliseconds, dto.Files!.Count);

    // ---- DTOs ----
    private sealed class CodeArtifactDto
    {
        public string? ProjectName { get; set; }
        public string? Architecture { get; set; }
        public List<FileDto>? Files { get; set; }
        public string? Notes { get; set; }

        public void Validate(string agentName)
        {
            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                throw new LlmException($"{agentName}: missing 'projectName'.", agentName);
            }
            if (Files is null || Files.Count == 0)
            {
                throw new LlmException($"{agentName}: 'files' must have ≥ 1 item.", agentName);
            }
        }
    }

    private sealed class FileDto
    {
        public string? Path { get; set; }
        public string? Content { get; set; }
        public string? Language { get; set; }
    }
}
