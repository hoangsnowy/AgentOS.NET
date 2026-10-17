// E0.8 — the OpenAI-compatible provider (vLLM, Ollama, LM Studio, Azure AI Foundry, a corporate gateway) for
// self-hosted and air-gapped tenants. The wire test runs a real HTTP server speaking /v1/chat/completions, so the
// request the OpenAI SDK actually sends (path, model, auth) and the usage it reads back are proven, not mocked.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class OpenAiCompatibleProviderTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6", "qwen2.5-coder", "qwen2.5-coder")]   // vendor id → the server's model
    [InlineData("gpt-4.1", null, OpenAiCompatibleModels.DefaultModel)]
    [InlineData("llama3.1:70b", "qwen2.5-coder", "llama3.1:70b")]          // a deliberate local id is kept
    [InlineData("", "mistral", "mistral")]
    public void Resolve_MapsVendorModelsToTheServerModel(string requested, string? configured, string expected)
        => OpenAiCompatibleModels.Resolve(requested, configured).ShouldBe(expected);

    [Fact]
    public void Cost_UsesTheOperatorsRates_ZeroByDefault()
    {
        var usage = new TokenUsage(1_000_000, 1_000_000);
        OpenAiCompatibleModels.Cost(usage, "0.5", "1.5").ShouldBe(2.0m);
        OpenAiCompatibleModels.Cost(usage, null, "").ShouldBe(0m);
        OpenAiCompatibleModels.Cost(usage, "-3", "abc").ShouldBe(0m);
    }

    [Fact]
    public void Settings_ArePlatformOnly_AndTheEndpointMustBeHttp()
    {
        var descriptors = new LlmSettings().Describe().Where(d => d.Key.StartsWith("Llm:OpenAiCompatible:", StringComparison.Ordinal)).ToArray();
        descriptors.Length.ShouldBe(5);
        descriptors.ShouldAllBe(d => d.Scope == SettingScope.Platform);   // the server calls it — never tenant-set (SSRF)
        var endpoint = descriptors.Single(d => d.Key == LlmSettings.CompatEndpoint);
        endpoint.Validator!("http://ollama:11434/v1").ShouldBeNull();
        endpoint.Validator!("https://foundry.example.com/v1").ShouldBeNull();
        endpoint.Validator!("ftp://x").ShouldNotBeNull();
        endpoint.Validator!("/v1").ShouldNotBeNull();
        LlmProviders.Selectable.ShouldContain(LlmProviders.OpenAiCompatible);
    }

    [Fact]
    public void ConfigView_CountsTheProviderLiveOnlyWithAnEndpoint()
    {
        var cfg = LlmConfigView.Combine(new LlmOptions(), forceOverride: "OpenAICompatible", null, null, null);
        cfg.HasKeyFor("OpenAICompatible").ShouldBeFalse();
        (cfg with { CompatEndpoint = "http://ollama:11434/v1" }).HasKeyFor("ollama").ShouldBeTrue();
    }

    // ---- the wire --------------------------------------------------------------------------------------

    private sealed record Captured(string Path, string? Model, string? Authorization);

    private static async Task<(WebApplication App, string BaseUrl, ConcurrentQueue<Captured> Requests)> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var seen = new ConcurrentQueue<Captured>();
        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            var model = doc.RootElement.GetProperty("model").GetString();
            seen.Enqueue(new Captured(http.Request.Path, model, http.Request.Headers.Authorization.ToString()));
            return Results.Json(new
            {
                id = "chatcmpl-1",
                @object = "chat.completion",
                created = 1_760_000_000,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "hello from a local model" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49 },
            });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, address.TrimEnd('/') + "/v1", seen);
    }

    [Fact]
    public async Task SdkClient_SpeaksTheOpenAiProtocol_ToACustomEndpoint()
    {
        var (app, baseUrl, seen) = await StartServerAsync();
        await using var _ = app;
        var chat = SdkChatClients.CreateOpenAiCompatible(null, baseUrl, "qwen2.5-coder");

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("hello from a local model");
        response.Usage!.InputTokenCount.ShouldBe(42);
        response.Usage!.OutputTokenCount.ShouldBe(7);
        var req = seen.ShouldHaveSingleItem();
        req.Path.ShouldBe("/v1/chat/completions");
        req.Model.ShouldBe("qwen2.5-coder");
        req.Authorization.ShouldBe("Bearer not-needed");   // keyless servers still get a well-formed header
    }

    [Fact]
    public async Task PooledClient_MapsTheProfileModel_AndMetersAtTheOperatorsRate()
    {
        var (app, baseUrl, seen) = await StartServerAsync();
        await using var _ = app;
        var client = new PooledChatLlmClient(
            LlmProviders.OpenAiCompatible,
            (key, model) => SdkChatClients.CreateOpenAiCompatible(key, baseUrl, model),
            _ => ValueTask.FromResult<System.Collections.Generic.IReadOnlyList<string>>([SdkChatClients.OpenAiCompatibleNoKey]),
            new ApiKeyRouter(TimeProvider.System),
            SdkChatClients.IsRateLimited,
            _ => null,
            NullLogger<PooledChatLlmClient>.Instance,
            modelResolver: requested => OpenAiCompatibleModels.Resolve(requested, "llama3.1"),
            costOverride: (_, usage) => OpenAiCompatibleModels.Cost(usage, "1", "2"));

        var response = await client.SendAsync(new LlmRequest("You are the Coding Agent.", "write code", "claude-sonnet-4-6", 0.2, 256), TestContext.Current.CancellationToken);

        seen.ShouldHaveSingleItem().Model.ShouldBe("llama3.1");   // the vendor id never reaches the local server
        response.Model.ShouldBe("llama3.1");
        response.Provider.ShouldBe(LlmProviders.OpenAiCompatible);
        response.InputTokens.ShouldBe(42);
        response.CostUsd.ShouldBe((42m * 1 + 7m * 2) / 1_000_000m);
    }
}
