// UX phase 1: Agent profiles shows only the model; the provider follows it. The current provider is kept when it
// already serves the model (Azure GPT deployments stay on Azure) and for providers that map models themselves.

using AgentOs.Modules.Llm;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class ModelRoutingTests
{
    [Theory]
    [InlineData("Claude", "gemini-3.8-flash", "Gemini")]
    [InlineData("Claude", "grok-4.7", "xAI")]
    [InlineData("Claude", "gpt-5.4", "OpenAI")]
    [InlineData("AzureOpenAI", "claude-haiku-5-5", "Claude")]
    [InlineData("AzureOpenAI", "gpt-5.4", "AzureOpenAI")]   // already serves GPT → stays on Azure
    [InlineData("MAF", "o3-pro", "MAF")]
    [InlineData("Anthropic", "claude-sonnet-5", "Anthropic")]
    [InlineData("Gemini", "gemini-2.5-flash", "Gemini")]
    [InlineData("RemoteAgent", "gpt-5.4", "RemoteAgent")]   // paired machine picks its own model
    [InlineData("OpenAICompatible", "claude-sonnet-5", "OpenAICompatible")]
    [InlineData("", "claude-sonnet-5", "Claude")]
    [InlineData("Claude", "", "Claude")]
    public void ProviderFor_FollowsTheModel_KeepsAProviderThatServesIt(string current, string model, string expected)
        => ModelRouting.ProviderFor(current, model).ShouldBe(expected);

    [Theory]
    [InlineData("claude-opus-5-5", "anthropic")]
    [InlineData("gemini-3.6-flash", "gemini")]
    [InlineData("grok-build-0.1", "xai")]
    [InlineData("o4-mini", "openai")]
    public void ModelFamily_ByVendorPrefix(string model, string family) => CostCalculator.ModelFamily(model).ShouldBe(family);
}
