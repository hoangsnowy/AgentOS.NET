// AgentOs.Domain/AgentMetrics.cs
// Phase 3 — Common metrics attached to every agent result (cost, token, latency).

namespace AgentOs.Domain;

/// <summary>
/// Common metric embedded in every agent result — used for KC1-KC5 benchmarks and cost reporting.
/// </summary>
/// <param name="Provider">Name of the provider that was called (Claude / AzureOpenAI).</param>
/// <param name="Model">Model alias that was used.</param>
/// <param name="InputTokens">Input tokens consumed.</param>
/// <param name="OutputTokens">Output tokens generated.</param>
/// <param name="CostUsd">Estimated cost in USD (CostCalculator).</param>
/// <param name="Latency">End-to-end agent time (including LLM call + parse + retry).</param>
public sealed record AgentMetrics(
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    System.TimeSpan Latency)
{
    /// <summary>Empty metrics — used for test scenarios or placeholder initialization.</summary>
    public static AgentMetrics Empty { get; } =
        new(Provider: "None", Model: "None", InputTokens: 0, OutputTokens: 0, CostUsd: 0m, Latency: System.TimeSpan.Zero);

    /// <summary>Prompt-cache reads — a subset of <see cref="InputTokens"/>, billed at the cache-read rate.</summary>
    public int CacheReadTokens { get; init; }

    /// <summary>Prompt-cache writes — a subset of <see cref="InputTokens"/>, billed at the cache-write rate.</summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>Reasoning / thinking tokens — a subset of <see cref="OutputTokens"/>.</summary>
    public int ReasoningTokens { get; init; }

    /// <summary>Sum of two metrics (accumulated for the pipeline result aggregate).</summary>
    public AgentMetrics Add(AgentMetrics other)
    {
        System.ArgumentNullException.ThrowIfNull(other);
        return new AgentMetrics(
            Provider: $"{Provider}+{other.Provider}",
            Model: $"{Model}+{other.Model}",
            InputTokens: InputTokens + other.InputTokens,
            OutputTokens: OutputTokens + other.OutputTokens,
            CostUsd: CostUsd + other.CostUsd,
            Latency: Latency + other.Latency)
        {
            CacheReadTokens = CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + other.CacheWriteTokens,
            ReasoningTokens = ReasoningTokens + other.ReasoningTokens,
        };
    }
}
