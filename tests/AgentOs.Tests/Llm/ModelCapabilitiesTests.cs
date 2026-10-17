// Sampling-parameter guard: newer reasoning models (Claude Opus 4.7+, Sonnet 5/5.5, Haiku 5.5, Fable; OpenAI
// GPT-5+/o-series) reject a caller-chosen temperature with HTTP 400, so the gateway must omit it for them.

using AgentOs.Modules.Llm;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class ModelCapabilitiesTests
{
    [Theory]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-sonnet-4-20250514")]   // dated snapshot of an allow-listed model
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4o-mini-2024-07-18")]
    public void TemperatureFor_LegacyModel_SendsRequestedValue(string model)
        => ModelCapabilities.TemperatureFor(model, 0.2).ShouldBe(0.2f);

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-fable-5-1")]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-6-sol")]
    [InlineData("o3")]
    public void TemperatureFor_ReasoningModel_OmitsParameter(string model)
        => ModelCapabilities.TemperatureFor(model, 0.2).ShouldBeNull();

    [Theory]
    [InlineData("gpt41-prod")]    // unknown deployment alias — omitting is the safe default
    [InlineData("claude-opus-9")]
    [InlineData("")]
    [InlineData(null)]
    public void TemperatureFor_UnknownModel_OmitsParameter(string? model)
        => ModelCapabilities.TemperatureFor(model, 0.2).ShouldBeNull();
}
