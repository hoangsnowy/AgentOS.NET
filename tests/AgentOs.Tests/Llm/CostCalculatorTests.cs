// AgentOs.Tests/Llm/CostCalculatorTests.cs
// Pricing table v2: exact-id lookup, dated-snapshot resolution, prompt-cache rates, and fail-closed pricing
// for unknown models (never $0 — an unpriced model must not slip past the budget gate).

using AgentOs.Modules.Llm;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public class CostCalculatorTests
{
    [Theory]
    // model, $/1M in, $/1M out → 1M in + 1M out
    [InlineData("claude-fable-5-1", 60.00)]
    [InlineData("claude-opus-5-5", 24.00)]
    [InlineData("claude-opus-5", 30.00)]
    [InlineData("claude-sonnet-5", 12.00)]
    [InlineData("claude-sonnet-4-6", 18.00)]
    [InlineData("claude-haiku-4-5", 6.00)]
    [InlineData("gpt-4.1", 10.00)]
    [InlineData("gpt-4.1-mini", 2.00)]
    [InlineData("gpt-4o-mini", 0.75)]
    public void Calculate_KnownModel_1MIn1MOut_UsesListPrice(string model, double expected)
        => CostCalculator.Calculate(model, 1_000_000, 1_000_000).ShouldBe((decimal)expected);

    [Fact]
    public void Calculate_ClaudeHaiku45_1KIn1KOut_Returns0006Usd()
        => CostCalculator.Calculate("claude-haiku-4-5", 1_000, 1_000).ShouldBe(0.006000m);

    [Theory]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4")]    // Anthropic dated snapshot
    [InlineData("gpt-4o-mini-2024-07-18", "gpt-4o-mini")]          // OpenAI dated snapshot
    [InlineData("claude-opus-4-5@20251101", "claude-opus-4-5")]    // Vertex-style snapshot
    [InlineData("CLAUDE-SONNET-5", "claude-sonnet-5")]             // case-insensitive
    public void Calculate_SnapshotOrCaseVariant_PricesLikeBaseModel(string variant, string canonical)
    {
        CostCalculator.IsKnown(variant).ShouldBeTrue();
        CostCalculator.Calculate(variant, 12_345, 6_789).ShouldBe(CostCalculator.Calculate(canonical, 12_345, 6_789));
    }

    [Fact]
    public void Calculate_Gpt41Mini_IsNotPricedAsGpt41()
    {
        // Regression: the old StartsWith match priced gpt-4.1-mini at gpt-4.1's rate (5× too high).
        CostCalculator.Calculate("gpt-4.1-mini", 1_000_000, 0).ShouldBe(0.40m);
        CostCalculator.Calculate("gpt-4.1", 1_000_000, 0).ShouldBe(2.00m);
    }

    [Theory]
    [InlineData("llama-3.2")]
    [InlineData("claude-opus-5-7")]     // a future model must not inherit claude-opus-5's rate silently
    [InlineData("gpt41-prod")]          // an Azure deployment alias with no pricing alias
    public void Calculate_UnknownModel_FailsClosedAtCeilingRate(string model)
    {
        CostCalculator.IsKnown(model).ShouldBeFalse();
        // Ceiling = the most expensive input/output rates in the table (Opus 4 / 4.1: $15 / $75).
        CostCalculator.Calculate(model, 1_000_000, 1_000_000).ShouldBe(90.00m);
    }

    [Fact]
    public void Calculate_WithCacheTokens_AddsCacheReadAndWriteRates()
    {
        // Sonnet 5: input $2, cache read 0.1× = $0.20, 5-min cache write 1.25× = $2.50 (per 1M).
        var cost = CostCalculator.Calculate("claude-sonnet-5", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 1_000_000, cacheWriteTokens: 1_000_000);
        cost.ShouldBe(2.70m);
    }

    [Theory]
    [InlineData("claude-fable-5-1", 0.25)]   // explicit 0.025× rate
    [InlineData("claude-opus-5-5", 0.20)]    // explicit 0.05× rate
    [InlineData("gpt-4.1", 0.50)]            // OpenAI published cached-input rate
    public void Calculate_CacheRead_UsesModelSpecificRate(string model, double perMillion)
        => CostCalculator.Calculate(model, 0, 0, cacheReadTokens: 1_000_000, cacheWriteTokens: 0).ShouldBe((decimal)perMillion);

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("claude-sonnet-5", -1, 1)]
    [InlineData("claude-sonnet-5", 1, -1)]
    public void Calculate_InvalidInput_ReturnsZero(string model, int inTok, int outTok)
        => CostCalculator.Calculate(model, inTok, outTok).ShouldBe(0m);

    [Theory]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4")]
    [InlineData("gpt-4o-mini-2024-07-18", "gpt-4o-mini")]
    [InlineData("x@y", "x")]
    [InlineData("claude-opus-5-5", null)]      // "-5" is a version segment, not a date
    [InlineData("gpt-4.1-2025-4-14", null)]    // malformed date
    public void StripSnapshot_RecognisesOnlyDateSuffixes(string id, string? expected)
        => CostCalculator.StripSnapshot(id).ShouldBe(expected);
}
