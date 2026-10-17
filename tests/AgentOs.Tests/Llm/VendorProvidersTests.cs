// Competitor parity — bring-your-own-key OpenAI, Google Gemini and xAI Grok. Endpoints are fixed in code (a tenant
// key can't become an SSRF vector), keys are tenant-scoped secrets, the profile's model survives only when the
// vendor serves it, and every model is priced (fail-closed pricing would otherwise bill $15/$75). The wire test
// proves the vendor model + the tenant's key reach an OpenAI-protocol server, priced from the built-in table.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class VendorProvidersTests
{
    [Theory]
    [InlineData("OpenAI", "gpt-5.5", null, "gpt-5.5")]
    [InlineData("OpenAI", "o3-pro", null, "o3-pro")]
    [InlineData("OpenAI", "claude-sonnet-5", null, "gpt-5.4")]              // a Claude profile model → the vendor default
    [InlineData("OpenAI", "claude-sonnet-5", "gpt-6-sol", "gpt-6-sol")]     // … or the tenant's configured default
    [InlineData("Gemini", "gemini-2.5-flash", null, "gemini-2.5-flash")]
    [InlineData("Gemini", "gpt-5.4", null, "gemini-3.8-flash")]
    [InlineData("xAI", "grok-4.3", null, "grok-4.3")]
    [InlineData("xAI", "", " grok-4.5 ", "grok-4.5")]
    public void ResolveModel_KeepsTheVendorsOwnIds_ElseTheDefault(string vendor, string requested, string? configured, string expected)
        => VendorProviders.All.Single(v => v.Name == vendor).ResolveModel(requested, configured).ShouldBe(expected);

    [Fact]
    public void Catalogue_FixedHttpsEndpoints_DefaultsArePriced_NamesAreSelectable()
    {
        VendorProviders.All.Select(v => v.Name).ShouldBe([LlmProviders.OpenAi, LlmProviders.Gemini, LlmProviders.Xai]);
        foreach (var v in VendorProviders.All)
        {
            new Uri(v.Endpoint).Scheme.ShouldBe("https");
            CostCalculator.IsKnown(v.DefaultModel).ShouldBeTrue(v.DefaultModel);
            LlmProviders.Selectable.ShouldContain(v.Name);
        }
    }

    [Theory]
    [InlineData("OpenAI", "OpenAI")]          // "OpenAI" is api.openai.com now — no longer an alias of Azure
    [InlineData("openai", "OpenAI")]
    [InlineData("Azure", "AzureOpenAI")]
    [InlineData("AzureOpenAI", "AzureOpenAI")]
    [InlineData("google", "Gemini")]
    [InlineData("GEMINI", "Gemini")]
    [InlineData("grok", "xAI")]
    [InlineData("xai", "xAI")]
    public void NormalizeKey_MapsAliasesToTheCanonicalProvider(string input, string expected)
        => LlmClientFactory.NormalizeKey(input).ShouldBe(expected);

    [Fact]
    public void Settings_KeysAreTenantScopedSecrets_ModelsDefaultToTheVendorDefault()
    {
        var descriptors = new LlmSettings().Describe().ToDictionary(d => d.Key, StringComparer.Ordinal);
        foreach (var v in VendorProviders.All)
        {
            var key = descriptors[v.KeySetting];
            key.Kind.ShouldBe(SettingKind.Secret);
            key.Scope.ShouldBe(SettingScope.Tenant);     // each tenant brings its own key
            var model = descriptors[v.ModelSetting];
            model.Kind.ShouldBe(SettingKind.Model);
            model.DefaultValue.ShouldBe(v.DefaultModel);
        }
    }

    [Theory]
    [InlineData("gemini-3.8-flash", 1_000_000, 1_000_000, 4.50)]
    [InlineData("gemini-2.5-flash-lite", 1_000_000, 1_000_000, 0.50)]
    [InlineData("grok-4.7", 200_000, 0, 0.40)]          // at the threshold: base $2.00 / 1M
    [InlineData("grok-4.7", 250_000, 0, 1.00)]          // above it: the whole request at $4.00 / 1M
    [InlineData("grok-4.3", 1_000_000, 0, 2.50)]        // > 200k prompt → long tier $2.50
    public void Cost_VendorModelsArePriced_IncludingGrokLongContext(string model, int input, int output, double expected)
    {
        CostCalculator.IsKnown(model).ShouldBeTrue();
        CostCalculator.Calculate(model, input, output).ShouldBe((decimal)expected);
    }

    [Fact]
    public void ModelPicker_EachVendorListsOnlyItsOwnModels()
    {
        CostCalculator.KnownModelsFor("Gemini").ShouldAllBe(m => m.StartsWith("gemini-", StringComparison.Ordinal));
        CostCalculator.KnownModelsFor("xAI").ShouldAllBe(m => m.StartsWith("grok-", StringComparison.Ordinal));
        CostCalculator.KnownModelsFor("AzureOpenAI").ShouldNotContain(m => m.StartsWith("grok-", StringComparison.Ordinal) || m.StartsWith("gemini-", StringComparison.Ordinal));
        CostCalculator.KnownModelsFor("OpenAI").ShouldContain("gpt-5.4");
        CostCalculator.KnownModelsFor("Claude").ShouldAllBe(m => m.StartsWith("claude-", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigView_CountsAVendorLiveOnlyWithTheTenantsKey()
    {
        var cfg = LlmConfigView.Combine(new LlmOptions(), forceOverride: "Gemini", null, null, null);
        cfg.HasKeyFor("Gemini").ShouldBeFalse();
        var withKey = cfg with { VendorKeys = new HashSet<string>([LlmProviders.Gemini], StringComparer.OrdinalIgnoreCase) };
        withKey.HasKeyFor("google").ShouldBeTrue();
        withKey.HasKeyFor("xAI").ShouldBeFalse();
        withKey.HasKeyFor("OpenAI").ShouldBeFalse();     // OpenAI no longer rides on the Azure key
    }

    // ---- the wire --------------------------------------------------------------------------------------

    private sealed record Captured(string? Model, string? Authorization);

    [Fact]
    public async Task PooledClient_SendsTheVendorModelAndKey_AndPricesFromTheTable()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var seen = new ConcurrentQueue<Captured>();
        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            var model = doc.RootElement.GetProperty("model").GetString();
            seen.Enqueue(new Captured(model, http.Request.Headers.Authorization.ToString()));
            return Results.Json(new
            {
                id = "chatcmpl-1",
                @object = "chat.completion",
                created = 1_760_000_000,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "hi from grok" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1_000, completion_tokens = 100, total_tokens = 1_100 },
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/v1";

        var vendor = VendorProviders.Xai;   // the endpoint is swapped for the local server; everything else is the real wiring
        var client = new PooledChatLlmClient(
            vendor.Name,
            (key, model) => SdkChatClients.CreateOpenAiCompatible(key, baseUrl, model),
            _ => ValueTask.FromResult<IReadOnlyList<string>>(["xai-test-key"]),
            new ApiKeyRouter(TimeProvider.System),
            SdkChatClients.IsRateLimited,
            _ => null,
            NullLogger<PooledChatLlmClient>.Instance,
            modelResolver: requested => vendor.ResolveModel(requested, null));

        var response = await client.SendAsync(new LlmRequest("You are the Coding Agent.", "write code", "claude-sonnet-5", 0.2, 256), TestContext.Current.CancellationToken);

        var req = seen.ShouldHaveSingleItem();
        req.Model.ShouldBe("grok-4.7");
        req.Authorization.ShouldBe("Bearer xai-test-key");
        response.Provider.ShouldBe(LlmProviders.Xai);
        response.CostUsd.ShouldBe(CostCalculator.Calculate("grok-4.7", 1_000, 100));
        response.CostUsd.ShouldBeGreaterThan(0m);
    }
}
