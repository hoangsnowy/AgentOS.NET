// AgentOs.Application/Metrics/RunMetric.cs
// Sprint 4 — one record per LLM call. Sinks to CSV for thesis Table 2.6.

using System;

namespace AgentOs.Modules.Pipeline.Metrics;

/// <summary>One record per LLM call within one iteration of one KC run.</summary>
/// <param name="RunId">UUID of a single pipeline run (same RunId → same pipeline execution).</param>
/// <param name="KcId">KC code: KC1..KC5, or "ad-hoc" for calls outside the KC bench.</param>
/// <param name="Iteration">QA loop iteration number (1..NMax), 0 for requirement (runs once).</param>
/// <param name="AgentName">RequirementAgent / CodingAgent / TestingAgent / QaAgent.</param>
/// <param name="Model">Model used (e.g. "claude-sonnet-4-5", "gpt-4.1").</param>
/// <param name="Provider">Provider (Claude / AzureOpenAI).</param>
/// <param name="TokensIn">Input tokens.</param>
/// <param name="TokensOut">Output tokens.</param>
/// <param name="LatencyMs">Total wall time of the LLM call (ms).</param>
/// <param name="CostUsd">Estimated cost (USD).</param>
/// <param name="Success">True if the call succeeded (parse + validate OK), false if it failed.</param>
/// <param name="ErrorMessage">Error (if failed).</param>
/// <param name="Timestamp">UTC timestamp when the call finished.</param>
/// <param name="CacheReadTokens">Prompt-cache reads — a subset of <paramref name="TokensIn"/>, billed at the cache-read rate.</param>
/// <param name="CacheWriteTokens">Prompt-cache writes — a subset of <paramref name="TokensIn"/>, billed at the cache-write rate.</param>
/// <param name="ReasoningTokens">Reasoning / thinking tokens — a subset of <paramref name="TokensOut"/>.</param>
public sealed record RunMetric(
    string RunId,
    string KcId,
    int Iteration,
    string AgentName,
    string Model,
    string Provider,
    int TokensIn,
    int TokensOut,
    double LatencyMs,
    decimal CostUsd,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset Timestamp,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int ReasoningTokens = 0);
