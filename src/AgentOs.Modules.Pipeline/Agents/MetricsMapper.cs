// AgentOs.Infrastructure/Agents/MetricsMapper.cs
// Phase 4 — Map LlmResponse → AgentMetrics.

using AgentOs.Domain;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Helper that maps LlmResponse to AgentMetrics (one line, avoids duplication across every agent).</summary>
internal static class MetricsMapper
{
    public static AgentMetrics From(LlmResponse response)
    {
        System.ArgumentNullException.ThrowIfNull(response);
        return new AgentMetrics(
            Provider: response.Provider,
            Model: response.Model,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            CostUsd: response.CostUsd,
            Latency: response.Latency)
        {
            CacheReadTokens = response.CacheReadTokens,
            CacheWriteTokens = response.CacheWriteTokens,
            ReasoningTokens = response.ReasoningTokens,
        };
    }
}
