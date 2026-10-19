// Settings → Model keys "Test": a 5-token probe of ONE provider. The provider is allow-listed (never an arbitrary
// keyed-service lookup from a query string) and the probe names the cheapest model — Haiku for Claude, otherwise
// the provider's own configured default.

using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class LlmProbeTests
{
    [Theory]
    [InlineData("Claude", true)]
    [InlineData("anthropic", true)]
    [InlineData("AzureOpenAI", true)]
    [InlineData("OpenAI", true)]
    [InlineData("gemini", true)]
    [InlineData("xAI", true)]
    [InlineData("OpenAICompatible", true)]
    [InlineData("RemoteAgent", false)]     // a paired machine is tested by pairing, not by spending its CLI
    [InlineData("Offline", false)]
    [InlineData("", false)]
    [InlineData("System.IO.File", false)]
    public void CanProbe_IsAnAllowList(string provider, bool expected) => LlmProbe.CanProbe(provider).ShouldBe(expected);

    [Fact]
    public void Request_IsTinyAndUsesTheCheapestModel()
    {
        var claude = LlmProbe.Request("Claude");
        claude.Model.ShouldBe(LlmProbe.ClaudeProbeModel);
        claude.MaxTokens.ShouldBe(5);
        CostCalculator.IsKnown(LlmProbe.ClaudeProbeModel).ShouldBeTrue();
        LlmProbe.Request(LlmProviders.Gemini).Model.ShouldBeEmpty();   // → the tenant's Gemini default model
        VendorProviders.Gemini.ResolveModel(LlmProbe.Request(LlmProviders.Gemini).Model, null).ShouldBe(VendorProviders.Gemini.DefaultModel);
    }
}
