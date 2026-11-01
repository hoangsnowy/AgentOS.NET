// AgentOs.Domain/Llm/LlmResponse.cs
// Sprint 1 — LLM Gateway contract (response side).

namespace AgentOs.Domain.Llm;

/// <summary>
/// Response returned from <see cref="ILlmClient.SendAsync(LlmRequest, System.Threading.CancellationToken)"/>.
/// </summary>
/// <param name="Content">Text content (extracted from the provider's shape). Required, may be empty.</param>
/// <param name="InputTokens">Number of input tokens consumed.</param>
/// <param name="OutputTokens">Number of output tokens generated.</param>
/// <param name="CostUsd">Estimated cost in USD (computed by <c>CostCalculator</c>).</param>
/// <param name="Latency">End-to-end time (measured with <see cref="System.Diagnostics.Stopwatch"/> in the client).</param>
/// <param name="Model">Name of the model that actually generated the response (the provider may alias it).</param>
/// <param name="Provider">Provider name: <c>"Claude"</c>, <c>"AzureOpenAI"</c>, or <c>"MAF"</c>.</param>
public sealed record LlmResponse(
    string Content,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    System.TimeSpan Latency,
    string Model,
    string Provider)
{
    /// <summary>Input tokens served from the provider's prompt cache — a subset of <see cref="InputTokens"/>,
    /// billed at the (much lower) cache-read rate.</summary>
    public int CacheReadTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache on this call — a subset of <see cref="InputTokens"/>,
    /// billed at the cache-write rate (Anthropic; 0 for providers that cache implicitly).</summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>Reasoning / thinking tokens — a subset of <see cref="OutputTokens"/> (billed as output).</summary>
    public int ReasoningTokens { get; init; }

    /// <summary>The files the paired runner sent back and the server stored (<see cref="LlmRequest.Uploads"/>), by path.</summary>
    public System.Collections.Generic.IReadOnlyList<AgentOs.Domain.Files.StoredFileInfo> Uploaded { get; init; } = [];

    /// <summary>A handle to continue this run with a follow-up call (<see cref="LlmRequest.ResumeOf"/>) — the paired
    /// runner keeps a run's folder and CLI session for a short while when its reply looked unfinished. Null when the
    /// provider can't continue a run (every server-side provider).</summary>
    public string? Continuation { get; init; }

    /// <summary>True only when this reply came from a follow-up (<see cref="LlmRequest.ResumeOf"/>) that really continued
    /// the earlier run — the paired runner ran <c>claude -p --resume &lt;session&gt;</c> in that run's kept folder — so the
    /// model answering remembers the work it is asked about. False for a fresh conversation (context was full, the CLI
    /// has no resume, the keep window expired, another runner took it) and for every server-side provider.</summary>
    public bool Resumed { get; init; }
}

/// <summary>
/// Provider-neutral token usage of one call. <see cref="Input"/> is ALL prompt tokens (fresh + cache read +
/// cache write) and <see cref="Output"/> includes reasoning — the Microsoft.Extensions.AI convention.
/// </summary>
public sealed record TokenUsage(int Input, int Output, int CacheRead = 0, int CacheWrite = 0, int Reasoning = 0)
{
    /// <summary>Prompt tokens billed at the full input rate (neither read from nor written to the cache).</summary>
    public int FreshInput => System.Math.Max(0, Input - CacheRead - CacheWrite);

    /// <summary>Share of the prompt served from cache, 0..1.</summary>
    public double CacheHitRatio => Input <= 0 ? 0d : (double)CacheRead / Input;
}
