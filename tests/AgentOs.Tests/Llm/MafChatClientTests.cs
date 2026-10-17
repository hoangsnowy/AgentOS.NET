// M1 — proves MafChatClient pools its AzureOpenAIClient by (endpoint, key) instead of building a new one
// per request (socket/handler churn). Exercises the internal GetOrCreateClient seam (InternalsVisibleTo).

using AgentOs.Modules.Llm;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class MafChatClientTests
{
    [Fact]
    public void GetOrCreateClient_SameEndpointAndKey_ReturnsCachedInstance()
    {
        var a = MafChatClient.GetOrCreateClient("https://unit-test.openai.azure.com", "maf-key-1");
        var b = MafChatClient.GetOrCreateClient("https://unit-test.openai.azure.com", "maf-key-1");

        a.ShouldBeSameAs(b);
    }

    [Fact]
    public void GetOrCreateClient_DifferentKey_ReturnsDistinctInstance()
    {
        var a = MafChatClient.GetOrCreateClient("https://unit-test.openai.azure.com", "maf-key-1");
        var b = MafChatClient.GetOrCreateClient("https://unit-test.openai.azure.com", "maf-key-2");

        a.ShouldNotBeSameAs(b);
    }

    [Fact]
    public void ResolvePricingModel_AliasedDeploymentWithPricingModel_PricesViaCanonicalPrefix()
    {
        // A real Azure deployment alias ("gpt41-prod") is not a priced prefix; PricingModel pins it to "gpt-4.1".
        var pricingModel = MafChatClient.ResolvePricingModel("gpt-4.1", "gpt41-prod");

        pricingModel.ShouldBe("gpt-4.1");
        CostCalculator.IsKnown(pricingModel).ShouldBeTrue();
        // 1M in @ $2.00 + 1M out @ $8.00 = $10.00 — proves the aliased deployment prices correctly.
        CostCalculator.Calculate(pricingModel, 1_000_000, 1_000_000).ShouldBe(10.00m);
    }

    [Fact]
    public void ResolvePricingModel_AliasedDeploymentWithoutPricingModel_FallsBackAndWarnsUnpriced()
    {
        // No PricingModel set → falls back to the deployment alias, which is not in the price table.
        var pricingModel = MafChatClient.ResolvePricingModel(null, "gpt41-prod");

        pricingModel.ShouldBe("gpt41-prod");
        // IsKnown == false is exactly the condition that drives the UNPRICED warning in SendAsync; the cost
        // itself fails closed at the table's ceiling rate so the spend still counts against the budget.
        CostCalculator.IsKnown(pricingModel).ShouldBeFalse();
        CostCalculator.Calculate(pricingModel, 1_000_000, 1_000_000).ShouldBe(90.00m);
    }
}
