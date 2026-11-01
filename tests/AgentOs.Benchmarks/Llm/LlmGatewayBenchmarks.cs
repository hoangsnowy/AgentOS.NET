// Per-LLM-call lookups: every gateway call prices its usage (CostCalculator — also the budget gate) and shapes the
// request (ModelCapabilities.TemperatureFor). §2.7 budget: lookup < 100 ns, 0 B on the steady state.

using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using BenchmarkDotNet.Attributes;

namespace AgentOs.Benchmarks.Llm;

/// <summary><see cref="CostCalculator"/> on the id shapes the providers actually report.</summary>
[MemoryDiagnoser]
public class CostCalculatorBenchmarks
{
    // Instance fields (not constants) so the JIT cannot fold the lookup away.
    private readonly string _exact = "claude-sonnet-5-5";
    private readonly string _exactMixedCase = "GPT-5.4-mini";
    private readonly string _anthropicSnapshot = "claude-sonnet-4-20250514";
    private readonly string _openAiSnapshot = "gpt-4o-mini-2024-07-18";
    private readonly string _vertexSnapshot = "claude-opus-4-5@20251101";
    private readonly string _unknown = "my-azure-deployment-prod";
    private readonly string _longContextModel = "claude-haiku-5-5";
    private readonly TokenUsage _usage = new(Input: 18_400, Output: 2_100, CacheRead: 12_000, CacheWrite: 1_600);

    /// <summary>Warm the snapshot memo once (the steady state on a running host).</summary>
    [GlobalSetup]
    public void Setup()
    {
        _ = CostCalculator.Calculate(_anthropicSnapshot, 1, 1);
        _ = CostCalculator.Calculate(_openAiSnapshot, 1, 1);
        _ = CostCalculator.Calculate(_vertexSnapshot, 1, 1);
        _ = CostCalculator.Calculate(_unknown, 1, 1);
    }

    /// <summary>Canonical id, the common case.</summary>
    [Benchmark(Baseline = true)]
    public decimal ExactId() => CostCalculator.Calculate(_exact, 12_000, 1_800, 30_000, 2_000);

    /// <summary>Canonical id in another case (Azure deployments often arrive upper-cased).</summary>
    [Benchmark]
    public decimal ExactIdMixedCase() => CostCalculator.Calculate(_exactMixedCase, 12_000, 1_800);

    /// <summary>Anthropic dated snapshot → base id via the memo.</summary>
    [Benchmark]
    public decimal AnthropicDatedSnapshot() => CostCalculator.Calculate(_anthropicSnapshot, 12_000, 1_800);

    /// <summary>OpenAI dated snapshot → base id via the memo.</summary>
    [Benchmark]
    public decimal OpenAiDatedSnapshot() => CostCalculator.Calculate(_openAiSnapshot, 12_000, 1_800);

    /// <summary>Vertex-style "@" snapshot.</summary>
    [Benchmark]
    public decimal VertexSnapshot() => CostCalculator.Calculate(_vertexSnapshot, 12_000, 1_800);

    /// <summary>Unknown model → fail-closed rate (memoised miss).</summary>
    [Benchmark]
    public decimal UnknownModel() => CostCalculator.Calculate(_unknown, 12_000, 1_800);

    /// <summary>Prompt over the long-context threshold (Haiku 5.5 &gt; 100k).</summary>
    [Benchmark]
    public decimal LongContextTier() => CostCalculator.Calculate(_longContextModel, 90_000, 4_000, 20_000, 0);

    /// <summary>Normalised usage overload + cache savings (both run per call for Insights).</summary>
    [Benchmark]
    public decimal UsageWithCacheSavings() => CostCalculator.Calculate(_exact, _usage) + CostCalculator.CacheSavings(_exact, _usage);

    /// <summary>IsKnown — logged per call to flag an estimated price.</summary>
    [Benchmark]
    public bool IsKnownSnapshot() => CostCalculator.IsKnown(_openAiSnapshot);
}

/// <summary><see cref="ModelCapabilities.TemperatureFor"/> — runs on every request build.</summary>
[MemoryDiagnoser]
public class ModelCapabilitiesBenchmarks
{
    private readonly string _reasoningModel = "claude-opus-5-5";
    private readonly string _legacyModel = "gpt-4.1-mini";
    private readonly string _legacySnapshot = "claude-sonnet-4-5-20250929";
    private readonly string _unknown = "my-azure-deployment-prod";

    /// <summary>Warm the snapshot memo.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _ = ModelCapabilities.TemperatureFor(_legacySnapshot, 0.2);
        _ = ModelCapabilities.TemperatureFor(_unknown, 0.2);
    }

    /// <summary>A model that rejects sampling params → omitted.</summary>
    [Benchmark(Baseline = true)]
    public float? ReasoningModel() => ModelCapabilities.TemperatureFor(_reasoningModel, 0.2);

    /// <summary>An allow-listed legacy model → sent.</summary>
    [Benchmark]
    public float? LegacyModel() => ModelCapabilities.TemperatureFor(_legacyModel, 0.2);

    /// <summary>A dated snapshot of an allow-listed model.</summary>
    [Benchmark]
    public float? LegacySnapshot() => ModelCapabilities.TemperatureFor(_legacySnapshot, 0.2);

    /// <summary>A deployment alias nobody priced → omitted.</summary>
    [Benchmark]
    public float? UnknownModel() => ModelCapabilities.TemperatureFor(_unknown, 0.2);
}
