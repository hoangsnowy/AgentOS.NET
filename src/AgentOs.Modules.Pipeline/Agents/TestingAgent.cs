// Phase 4 — ITestingAgent impl. Generates xUnit tests (happy/edge/error) from spec + code.
// The LLM-call / parse / validate / metrics / error skeleton lives in LlmAgentBase.

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
using AgentOs.Domain.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Generates an xUnit test suite categorized as happy/edge/error.</summary>
public sealed class TestingAgent : LlmAgentBase, ITestingAgent
{
    /// <summary>Initializes.</summary>
    public TestingAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILlmOutputValidator validator,
        IMetricsCollector collector,
        ILogger<TestingAgent> logger,
        IPromptOverrides? prompts = null,
        IAgentProfiles? profiles = null)
        : base(factory, Slice(options), collector, logger, validator, prompts, profiles)
    {
    }

    private static AgentOptions Slice(IOptions<AgentsOptions> options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        return options.Value.Testing;
    }

    protected override string PromptKey => "Testing";

    protected override string DefaultSystemPrompt => TestingPrompt.System;

    protected override string? SchemaName => SchemaNames.TestArtifactV1;

    /// <inheritdoc />
    public Task<TestArtifact> RunAsync(
        RequirementSpec spec,
        CodeArtifact code,
        QaReport? previousFeedback = null,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(spec);
        System.ArgumentNullException.ThrowIfNull(code);
        return ExecuteAsync<TestArtifactDto, TestArtifact>(
            TestingPrompt.RenderUser(spec, code, previousFeedback),
            dto => dto.Validate(AgentName),
            Map,
            LogSuccess,
            cancellationToken);
    }

    private static TestArtifact Map(TestArtifactDto dto, AgentMetrics metrics)
        => new(
            Framework: dto.Framework ?? "xUnit",
            Files: dto.Files!.Select(f => new CodeFile(f.Path!, f.Content ?? string.Empty, f.Language ?? "csharp")).ToArray(),
            HappyPathCount: dto.HappyPathCount,
            EdgeCaseCount: dto.EdgeCaseCount,
            ErrorCaseCount: dto.ErrorCaseCount,
            EstimatedCoveragePercent: dto.EstimatedCoveragePercent,
            Metrics: metrics);

    private void LogSuccess(AgentMetrics metrics, TestArtifactDto dto)
        => Logger.LogInformation(
            "{Agent} done: {InTok}→{OutTok} tokens, ${Cost} USD, {Ms}ms — {Total} tests ({Happy}H/{Edge}E/{Err}X)",
            AgentName, metrics.InputTokens, metrics.OutputTokens, metrics.CostUsd,
            metrics.Latency.TotalMilliseconds, dto.HappyPathCount + dto.EdgeCaseCount + dto.ErrorCaseCount,
            dto.HappyPathCount, dto.EdgeCaseCount, dto.ErrorCaseCount);

    // ---- DTOs ----
    private sealed class TestArtifactDto
    {
        public string? Framework { get; set; }
        public List<FileDto>? Files { get; set; }
        public int HappyPathCount { get; set; }
        public int EdgeCaseCount { get; set; }
        public int ErrorCaseCount { get; set; }
        public int EstimatedCoveragePercent { get; set; }

        public void Validate(string agentName)
        {
            if (Files is null || Files.Count == 0)
            {
                throw new LlmException($"{agentName}: 'files' must have ≥ 1 item.", agentName);
            }
            if (HappyPathCount + EdgeCaseCount + ErrorCaseCount <= 0)
            {
                throw new LlmException($"{agentName}: total test count must be > 0.", agentName);
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
