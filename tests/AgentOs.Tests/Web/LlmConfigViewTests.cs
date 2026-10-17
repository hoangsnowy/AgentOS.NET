// Unit tests for the shared effective-LLM-config precedence (LlmConfigView.Combine) that the Settings
// Status table and both studio "Live" chips now read from one place. Pure — no DI, no store.

using AgentOs.Domain.Llm;
using AgentOs.Web.Services;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class LlmConfigViewTests
{
    private static LlmOptions Opts(string provider = "Claude", string? force = null,
        string anthropicKey = "", string azureKey = "", string azureEndpoint = "")
    {
        var o = new LlmOptions { Provider = provider, ForceProvider = force };
        o.Claude.ApiKey = anthropicKey;
        o.AzureOpenAi.ApiKey = azureKey;
        o.AzureOpenAi.Endpoint = azureEndpoint;
        return o;
    }

    [Fact]
    public void Combine_TenantForceOverride_BeatsAppsettings_AndIsTaggedRuntime()
    {
        var cfg = LlmConfigView.Combine(Opts(force: "Claude"), forceOverride: "AzureOpenAI",
            anthropicOverride: null, azureOverride: null, endpointOverride: null);

        cfg.Force.ShouldBe("AzureOpenAI");
        cfg.ForceSource.ShouldBe(ForceSource.RuntimeOverride);
        cfg.ChipLabel.ShouldBe("AzureOpenAI (forced)");
    }

    [Fact]
    public void Combine_NoOverride_FallsBackToAppsettingsForce_TaggedAppSettings()
    {
        var cfg = LlmConfigView.Combine(Opts(force: "AzureOpenAI"), forceOverride: null,
            anthropicOverride: null, azureOverride: null, endpointOverride: null);

        cfg.Force.ShouldBe("AzureOpenAI");
        cfg.ForceSource.ShouldBe(ForceSource.AppSettings);
    }

    [Fact]
    public void Combine_NoForceAnywhere_ChipShowsDefaultProvider()
    {
        var cfg = LlmConfigView.Combine(Opts(provider: "Claude", force: null), forceOverride: null,
            anthropicOverride: null, azureOverride: null, endpointOverride: null);

        cfg.Force.ShouldBeNull();
        cfg.ForceSource.ShouldBe(ForceSource.None);
        cfg.ChipLabel.ShouldBe("Claude");
    }

    [Fact]
    public void Combine_KeySet_WhenOnlyTenantOverridePresent()
    {
        var cfg = LlmConfigView.Combine(Opts(anthropicKey: ""), forceOverride: null,
            anthropicOverride: "sk-ant-tenant", azureOverride: null, endpointOverride: null);

        cfg.AnthropicKeySet.ShouldBeTrue();   // the platform key is blank — the tenant's own key counts
        cfg.AzureKeySet.ShouldBeFalse();
    }

    [Fact]
    public void Combine_KeySet_WhenOnlyPlatformFallbackPresent()
    {
        var cfg = LlmConfigView.Combine(Opts(azureKey: "platform-azure"), forceOverride: null,
            anthropicOverride: null, azureOverride: null, endpointOverride: null);

        cfg.AzureKeySet.ShouldBeTrue();
        cfg.AnthropicKeySet.ShouldBeFalse();
    }

    [Fact]
    public void Combine_Endpoint_OverrideWins_ThenAppsettings_ThenNull()
    {
        LlmConfigView.Combine(Opts(azureEndpoint: "https://platform.openai.azure.com"),
            null, null, null, "https://tenant.openai.azure.com").AzureEndpoint
            .ShouldBe("https://tenant.openai.azure.com");

        LlmConfigView.Combine(Opts(azureEndpoint: "https://platform.openai.azure.com"),
            null, null, null, null).AzureEndpoint
            .ShouldBe("https://platform.openai.azure.com");

        LlmConfigView.Combine(Opts(azureEndpoint: ""), null, null, null, "   ").AzureEndpoint
            .ShouldBeNull();
    }

    private static LlmOptions Offline(LlmOptions o)
    {
        o.OfflineFallback = true;
        return o;
    }

    [Fact]
    public void WillServeCanned_OfflineFallbackOn_DefaultProviderHasNoKey_IsTrue()
    {
        var cfg = LlmConfigView.Combine(Offline(Opts(provider: "Anthropic")), null, null, null, null);

        cfg.WillServeCanned.ShouldBeTrue();
    }

    [Fact]
    public void WillServeCanned_TenantKeyPresent_IsFalse()
    {
        var cfg = LlmConfigView.Combine(Offline(Opts(provider: "Claude")), null, "sk-ant-tenant", null, null);

        cfg.WillServeCanned.ShouldBeFalse();
    }

    [Fact]
    public void WillServeCanned_OfflineFallbackOff_IsFalse_EvenWithoutKey()
    {
        // No fallback → a keyless run fails loudly instead of serving canned text, so the chip stays "Live".
        var cfg = LlmConfigView.Combine(Opts(provider: "Claude"), null, null, null, null);

        cfg.WillServeCanned.ShouldBeFalse();
    }

    [Fact]
    public void WillServeCanned_ForcedAzure_NeedsKeyAndEndpoint()
    {
        var keyOnly = LlmConfigView.Combine(Offline(Opts(provider: "Claude", anthropicKey: "sk")), "AzureOpenAI", null, "az-key", null);
        var keyAndEndpoint = LlmConfigView.Combine(Offline(Opts(provider: "Claude")), "AzureOpenAI", null, "az-key", "https://r.openai.azure.com");

        keyOnly.WillServeCanned.ShouldBeTrue();          // the forced provider wins; the Claude key is irrelevant
        keyAndEndpoint.WillServeCanned.ShouldBeFalse();
    }

    [Fact]
    public void WillServeCanned_ProviderWithoutKeyConcept_IsFalse()
    {
        var cfg = LlmConfigView.Combine(Offline(Opts(provider: "RemoteAgent")), null, null, null, null);

        cfg.WillServeCanned.ShouldBeFalse();
    }
}
