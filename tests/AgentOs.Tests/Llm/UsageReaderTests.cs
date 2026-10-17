// Cache-aware token accounting: provider usage is normalised to fresh input / cache read / cache write /
// output (+ reasoning), survives a tool-calling loop, and is priced per class — so a cached prompt costs what
// the provider bills, not full-price input.

using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class UsageReaderTests
{
    [Fact]
    public void Normalize_MeaiConvention_KeepsCacheInsideInput()
    {
        var u = UsageReader.Read(UsageReader.Normalize(new UsageDetails
        {
            InputTokenCount = 10_000, OutputTokenCount = 500, CachedInputTokenCount = 8_000, ReasoningTokenCount = 120,
        }, raw: null));

        u.Input.ShouldBe(10_000);
        u.CacheRead.ShouldBe(8_000);
        u.FreshInput.ShouldBe(2_000);
        u.Output.ShouldBe(500);
        u.Reasoning.ShouldBe(120);
        u.CacheHitRatio.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void Normalize_AnthropicRaw_AddsCacheClassesToTheirInputTokens()
    {
        // Anthropic: input_tokens EXCLUDES cache reads/writes; the adapter only copies input_tokens.
        var raw = new Anthropic.SDK.Messaging.MessageResponse
        {
            Usage = new Anthropic.SDK.Messaging.Usage
            {
                InputTokens = 300, CacheReadInputTokens = 9_000, CacheCreationInputTokens = 700, OutputTokens = 400,
            },
        };
        var u = UsageReader.Read(UsageReader.Normalize(new UsageDetails { InputTokenCount = 300, OutputTokenCount = 400 }, raw));

        u.Input.ShouldBe(10_000);
        u.CacheRead.ShouldBe(9_000);
        u.CacheWrite.ShouldBe(700);
        u.FreshInput.ShouldBe(300);
        u.Output.ShouldBe(400);
    }

    [Fact]
    public void Normalize_AdditionalCounts_PicksUpAdapterCacheKeys()
    {
        var details = new UsageDetails
        {
            InputTokenCount = 1_000, OutputTokenCount = 10,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_creation_input_tokens"] = 600, ["cache_read_input_tokens"] = 200 },
        };
        var u = UsageReader.Read(UsageReader.Normalize(details, raw: null));
        u.CacheWrite.ShouldBe(600);
        u.CacheRead.ShouldBe(200);
    }

    [Fact]
    public void Normalize_ClampsImpossibleCounts()
    {
        var u = UsageReader.Read(UsageReader.Normalize(new UsageDetails
        {
            InputTokenCount = 100, OutputTokenCount = 10, CachedInputTokenCount = 500, ReasoningTokenCount = 50,
        }, raw: null));
        u.CacheRead.ShouldBe(100);
        u.FreshInput.ShouldBe(0);
        u.Reasoning.ShouldBe(10);
    }

    [Fact]
    public void ToolLoop_PerTurnNormalisedUsage_SumsCacheClasses()
    {
        // FunctionInvokingChatClient sums each turn's UsageDetails (incl. AdditionalCounts).
        var turn1 = UsageReader.Normalize(new UsageDetails { InputTokenCount = 1_000, OutputTokenCount = 50 },
            new Anthropic.SDK.Messaging.MessageResponse { Usage = new Anthropic.SDK.Messaging.Usage { InputTokens = 100, CacheCreationInputTokens = 900, OutputTokens = 50 } })!;
        var turn2 = UsageReader.Normalize(new UsageDetails { InputTokenCount = 200, OutputTokenCount = 80 },
            new Anthropic.SDK.Messaging.MessageResponse { Usage = new Anthropic.SDK.Messaging.Usage { InputTokens = 200, CacheReadInputTokens = 900, OutputTokens = 80 } })!;
        turn1.Add(turn2);

        var u = UsageReader.Read(turn1);
        u.Input.ShouldBe(2_100);
        u.CacheWrite.ShouldBe(900);
        u.CacheRead.ShouldBe(900);
        u.Output.ShouldBe(130);
    }

    [Fact]
    public void Cost_CachedPrompt_IsCheaperThanAllFreshInput_AndSavingsArePositive()
    {
        var cached = new TokenUsage(Input: 100_000, Output: 1_000, CacheRead: 90_000);
        var withCache = CostCalculator.Calculate("claude-sonnet-4-6", cached);
        var allFresh = CostCalculator.Calculate("claude-sonnet-4-6", 100_000, 1_000);

        withCache.ShouldBeLessThan(allFresh);
        CostCalculator.CacheSavings("claude-sonnet-4-6", cached).ShouldBe(allFresh - withCache);
        // Sonnet 4.6: $3 in, $0.30 cache read, $15 out per 1M → 10k×3 + 90k×0.3 + 1k×15 = $0.072
        withCache.ShouldBe(0.072m);
    }
}
