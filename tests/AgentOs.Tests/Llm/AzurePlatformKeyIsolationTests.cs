// GAP-01 follow-up: the platform's own Azure OpenAI key must only ever be sent to the platform's own endpoint. A tenant
// that sets an endpoint in Settings but no key used to fall back to the platform key — so a tenant admin could point
// the platform's key at a resource it controls. Now a Settings (untrusted) endpoint runs on the tenant's own keys only,
// and with none the call is refused before any request, in both the pooled AzureOpenAI client and the MAF client.
// Also proves the SDK client really goes through SsrfGuard's connect-time handler for a Settings endpoint.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class AzurePlatformKeyIsolationTests
{
    private const string PlatformKey = "platform-azure-key";
    private const string PlatformEndpoint = "https://platform-gap01.openai.azure.com";
    private const string TenantKey = "tenant-azure-key";
    private const string TenantEndpoint = "https://tenant-gap01.openai.azure.com";

    // ── Key selection (shared by both clients) ──

    [Fact]
    public void KeysFor_TenantEndpoint_NoTenantKey_IsRefused()
        => Should.Throw<LlmException>(() => AzureEndpoints.KeysFor(Untrusted(), [], [PlatformKey], "AzureOpenAI"))
            .Message.ShouldContain("Set your own Azure OpenAI key together with your own endpoint");

    [Fact]
    public void KeysFor_TenantEndpoint_WithTenantKey_UsesOnlyTheTenantKey()
        => AzureEndpoints.KeysFor(Untrusted(), [TenantKey], [PlatformKey], "AzureOpenAI").ShouldHaveSingleItem().ShouldBe(TenantKey);

    [Fact]
    public void KeysFor_PlatformEndpoint_NoTenantKey_UsesPlatformKeys()
        => AzureEndpoints.KeysFor(Trusted(), [], [PlatformKey], "AzureOpenAI").ShouldHaveSingleItem().ShouldBe(PlatformKey);

    [Fact]
    public void EnsureKeyMayGoTo_PlatformKeyToTenantEndpoint_IsRefused()
    {
        Should.Throw<LlmException>(() => AzureEndpoints.EnsureKeyMayGoTo(Untrusted(), PlatformKey, [PlatformKey], "AzureOpenAI"));
        Should.NotThrow(() => AzureEndpoints.EnsureKeyMayGoTo(Untrusted(), TenantKey, [PlatformKey], "AzureOpenAI"));
        Should.NotThrow(() => AzureEndpoints.EnsureKeyMayGoTo(Trusted(), PlatformKey, [PlatformKey], "AzureOpenAI"));
    }

    // ── Pooled AzureOpenAI client (LlmModule) ──

    [Fact]
    public async Task AzurePool_TenantEndpoint_NoTenantKey_RefusedBeforeAnyRequest()
    {
        using var sp = Services(new FakeOverrides { AzureEndpoint = TenantEndpoint });
        var client = sp.GetRequiredKeyedService<ILlmClient>("AzureOpenAI");

        // The refusal is our LlmException with our message — not a transport / SDK error, so no request went out.
        var ex = await Should.ThrowAsync<LlmException>(() => client.SendAsync(new LlmRequest("sys", "user", "gpt-4.1"), TestContext.Current.CancellationToken));
        ex.Message.ShouldBe(AzureEndpoints.TenantEndpointNeedsTenantKey);
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task AzurePool_TenantEndpoint_WithTenantKey_UsesTheTenantKey()
    {
        var keys = await LlmModule.AzureKeyPoolAsync(PlatformOptions(), new FakeOverrides { AzureEndpoint = TenantEndpoint, AzureKeys = [TenantKey] }, TestContext.Current.CancellationToken);
        keys.ShouldHaveSingleItem().ShouldBe(TenantKey);
    }

    [Fact]
    public async Task AzurePool_PlatformEndpoint_UsesPlatformKeys()
    {
        var keys = await LlmModule.AzureKeyPoolAsync(PlatformOptions(), new FakeOverrides(), TestContext.Current.CancellationToken);
        keys.ShouldHaveSingleItem().ShouldBe(PlatformKey);
    }

    // ── MAF client ──

    [Fact]
    public async Task Maf_TenantEndpoint_NoTenantKey_RefusedBeforeAnyRequest()
    {
        var maf = Maf(new FakeOverrides { AzureEndpoint = TenantEndpoint });

        (await Should.ThrowAsync<LlmException>(async () => await maf.ResolveCredentialsAsync(TestContext.Current.CancellationToken)))
            .Message.ShouldBe(AzureEndpoints.TenantEndpointNeedsTenantKey);
        var ex = await Should.ThrowAsync<LlmException>(() => maf.SendAsync(new LlmRequest("sys", "user", "gpt-4.1"), TestContext.Current.CancellationToken));
        ex.Message.ShouldBe(AzureEndpoints.TenantEndpointNeedsTenantKey);
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Maf_TenantEndpoint_WithTenantKey_UsesTheTenantKey()
    {
        var (endpoint, key) = await Maf(new FakeOverrides { AzureEndpoint = TenantEndpoint, AzureKeys = [TenantKey] })
            .ResolveCredentialsAsync(TestContext.Current.CancellationToken);
        endpoint.ShouldBe(new AzureEndpoints.Resolved(TenantEndpoint, Untrusted: true));
        key.ShouldBe(TenantKey);
    }

    [Fact]
    public async Task Maf_PlatformEndpoint_UsesThePlatformKey()
    {
        var (endpoint, key) = await Maf(new FakeOverrides()).ResolveCredentialsAsync(TestContext.Current.CancellationToken);
        endpoint.ShouldBe(new AzureEndpoints.Resolved(PlatformEndpoint, Untrusted: false));
        key.ShouldBe(PlatformKey);
    }

    // Nit from the review: "not configured" is decided on the resolved endpoint.
    [Fact]
    public async Task Maf_NothingConfigured_IsAPlainNotConfiguredError()
    {
        var maf = new MafChatClient(Options.Create(new LlmOptions()), new FakeOverrides(), NullLogger<MafChatClient>.Instance);
        var ex = await Should.ThrowAsync<LlmException>(async () => await maf.ResolveCredentialsAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not configured");
    }

    // ── The SDK transport for a Settings endpoint is SsrfGuard's ──

    [Fact]
    public async Task SettingsEndpoint_SdkClient_RefusesALoopbackNameAtConnect()
    {
        // A real listener on 127.0.0.1: "localhost" resolves to it. The hardened handler must refuse at connect,
        // so the listener never sees a connection — a plain transport would have connected.
        using var listener = new TcpListener(IPAddress.Loopback, 0);   // Dispose stops it
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var chat = AzureEndpoints.CreateClient(new AzureEndpoints.Resolved($"https://localhost:{port}", Untrusted: true), TenantKey)
            .GetChatClient("gpt-4.1").AsIChatClient();

        var ex = await Should.ThrowAsync<Exception>(() => chat.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken));

        Chain(ex).ShouldContain(e => e.Message.Contains("Refusing to connect to 'localhost'", StringComparison.Ordinal));
        listener.Pending().ShouldBeFalse();
    }

    // ── helpers ──

    private static AzureEndpoints.Resolved Untrusted() => new(TenantEndpoint, Untrusted: true);

    private static AzureEndpoints.Resolved Trusted() => new(PlatformEndpoint, Untrusted: false);

    private static AzureOpenAiOptions PlatformOptions() => new() { ApiKey = PlatformKey, Endpoint = PlatformEndpoint };

    private static MafChatClient Maf(IRuntimeOverrides overrides) =>
        new(Options.Create(new LlmOptions { AzureOpenAi = PlatformOptions() }), overrides, NullLogger<MafChatClient>.Instance);

    private static ServiceProvider Services(IRuntimeOverrides overrides)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:AzureOpenAi:ApiKey"] = PlatformKey,
            ["Llm:AzureOpenAi:Endpoint"] = PlatformEndpoint,
        }).Build();
        new LlmModule().AddServices(services, config);
        services.AddSingleton(overrides);   // last registration wins over the module's RuntimeOverrides
        return services.BuildServiceProvider();
    }

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    // IRuntimeOverrides has ValueTask members — a hand-written fake, never an NSubstitute substitute.
    private sealed class FakeOverrides : IRuntimeOverrides
    {
        public IReadOnlyList<string> AzureKeys { get; init; } = [];

        public ValueTask<IReadOnlyList<string>> GetAnthropicApiKeysAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<IReadOnlyList<string>> GetAzureApiKeysAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(AzureKeys);

        public string? ForceProvider { get; set; }

        public string? AnthropicApiKey { get; set; }

        public IReadOnlyList<string> AnthropicApiKeys { get; set; } = [];

        public string? AzureApiKey { get; set; }

        public IReadOnlyList<string> AzureApiKeys { get; set; } = [];

        public string? AzureEndpoint { get; set; }

        public string? GitHubPat { get; set; }

        public string? GitHubRepoOwner { get; set; }

        public string? GitHubRepoName { get; set; }

        public string? GitHubBaseBranch { get; set; }

        public string? GitHubBaseUrl { get; set; }
    }
}
