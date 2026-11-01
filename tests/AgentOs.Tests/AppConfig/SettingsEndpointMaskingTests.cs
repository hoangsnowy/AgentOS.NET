// GAP-08 (docs/security/threat-model.md §15): GET /settings/{prefix} used to return decrypted LLM keys and the GitHub
// PAT to every tenant admin. Secrets are now write-only on that endpoint too: a secret-kind value (declared Secret in
// the settings catalog, or named like one) comes back masked — "••••" + the last four characters at most — while
// non-secret values (repo owner, base branch, endpoint) still read back in full.
// Follow-up: masking FAILS CLOSED. Only a key known to be non-secret (a non-Secret catalog descriptor, or an explicitly
// listed coordinate) reads back in clear; an undeclared key (a future Github:App:PrivateKey, Llm:X:Key) is masked.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Endpoints;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.AppConfig;

public sealed class SettingsEndpointMaskingTests
{
    private const string ClaudeKey = "sk-ant-api03-SECRET-VALUE-1234";
    private const string Pat = "ghp_SECRETSECRETSECRETabcd";

    private sealed class Contributor : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            yield return new(AppConfigKeys.LlmClaudeApiKey, SettingKind.Secret, "LLM", "Anthropic API key");
            yield return new("Llm:Custom:Credential", SettingKind.Secret, "LLM", "A secret with an innocent name");
        }
    }

    private static async Task<Dictionary<string, string?>> ReadAsync(string prefix, ISettingsCatalog? catalog)
    {
        var store = new InMemoryAppConfigStore();
        await store.SetAsync(AppConfigKeys.LlmClaudeApiKey, ClaudeKey);
        await store.SetAsync(AppConfigKeys.LlmAzureApiKey, "short");
        await store.SetAsync("Llm:Custom:Credential", "custom-secret-value-9876");
        await store.SetAsync(AppConfigKeys.LlmAzureEndpoint, "https://acme.openai.azure.com");
        await store.SetAsync(AppConfigKeys.GithubPat, Pat);
        await store.SetAsync(AppConfigKeys.GithubRepoOwner, "acme");
        await store.SetAsync(AppConfigKeys.GithubBaseBranch, "main");

        var result = await SettingsEndpoints.ReadPrefixAsync(prefix, store, catalog, TestContext.Current.CancellationToken);
        return result.ShouldBeOfType<Ok<Dictionary<string, string?>>>().Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadPrefix_Llm_MasksSecrets_KeepsEndpoint()
    {
        var values = await ReadAsync("Llm", new SettingsCatalog([new Contributor()]));

        values[AppConfigKeys.LlmClaudeApiKey].ShouldBe("••••1234");
        values[AppConfigKeys.LlmAzureApiKey].ShouldBe("••••");                 // too short to show any of it
        values["Llm:Custom:Credential"].ShouldBe("••••9876");                  // secret by catalog, not by name
        values[AppConfigKeys.LlmAzureEndpoint].ShouldBe("https://acme.openai.azure.com");
        values.Values.ShouldNotContain(ClaudeKey);
    }

    // Without a catalog (or for a key no module declares) the name still marks it a secret.
    [Fact]
    public async Task ReadPrefix_Github_NoCatalog_MasksPatByName_KeepsRepoCoordinates()
    {
        var values = await ReadAsync("Github", catalog: null);

        values[AppConfigKeys.GithubPat].ShouldBe("••••abcd");
        values[AppConfigKeys.GithubRepoOwner].ShouldBe("acme");
        values[AppConfigKeys.GithubBaseBranch].ShouldBe("main");
    }

    [Fact]
    public async Task ReadPrefix_UnreadablePrefix_Is400()
    {
        var result = await SettingsEndpoints.ReadPrefixAsync("Auth", new InMemoryAppConfigStore(), null, TestContext.Current.CancellationToken);
        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(400);
    }

    [Theory]
    [InlineData("Llm:Claude:ApiKey", true)]
    [InlineData("Llm:Claude:ApiKeys", true)]
    [InlineData("Github:Pat", true)]
    [InlineData("Github:WebhookSecret", true)]
    [InlineData("Github:RepoOwner", false)]
    [InlineData("Github:RepoName", false)]
    [InlineData("Github:BaseBranch", false)]
    [InlineData("Github:BaseUrl", false)]
    [InlineData("Llm:AzureOpenAi:Endpoint", false)]
    [InlineData("Llm:ForceProvider", false)]
    public void IsSecret_NoCatalog_OnlyListedCoordinatesInClear(string key, bool secret) => SettingsKeyRegistry.IsSecret(key, null).ShouldBe(secret);

    // The bug class: a credential nobody has declared yet, with a name the old deny-list didn't anticipate.
    [Theory]
    [InlineData("Github:App:PrivateKey")]
    [InlineData("Llm:X:Key")]
    [InlineData("Github:ClientCert")]
    [InlineData("Llm:Foo:Credential")]
    [InlineData("Github:Anything")]
    public void IsSecret_UnknownKey_IsMasked(string key)
    {
        SettingsKeyRegistry.IsSecret(key, null).ShouldBeTrue();
        SettingsKeyRegistry.IsSecret(key, RealCatalog()).ShouldBeTrue();
    }

    [Theory]
    [InlineData(LlmSettings.AzureModel)]
    [InlineData(LlmSettings.AzurePricingModel)]
    [InlineData(LlmSettings.Provider)]
    [InlineData(LlmSettings.CompatEndpoint)]
    [InlineData(LlmSettings.CompatInputPrice)]
    [InlineData("Llm:OpenAi:Model")]
    public void IsSecret_DeclaredNonSecret_InClear(string key) => SettingsKeyRegistry.IsSecret(key, RealCatalog()).ShouldBeFalse();

    // Every secret the platform has today is masked, with and without the catalog.
    [Fact]
    public void IsSecret_EveryCurrentSecret_IsMasked()
    {
        var catalog = RealCatalog();
        var secrets = catalog.All.Where(d => d.IsSecret).Select(d => d.Key)
            .Concat([AppConfigKeys.LlmClaudeApiKey, AppConfigKeys.LlmClaudeApiKeys, AppConfigKeys.LlmAzureApiKey,
                     AppConfigKeys.LlmAzureApiKeys, AppConfigKeys.GithubPat])
            .ToList();
        secrets.Count.ShouldBeGreaterThan(5);
        foreach (var key in secrets)
        {
            SettingsKeyRegistry.IsSecret(key, catalog).ShouldBeTrue(key);
            SettingsKeyRegistry.IsSecret(key, null).ShouldBeTrue(key);
        }
    }

    // A descriptor that (mis)declares a credential-named key as plain text is still masked.
    [Fact]
    public void IsSecret_CredentialName_DeclaredText_StillMasked()
    {
        var catalog = new SettingsCatalog([new TextNamedLikeSecret()]);
        SettingsKeyRegistry.IsSecret("Llm:Odd:ApiKey", catalog).ShouldBeTrue();
    }

    [Fact]
    public async Task ReadPrefix_UnknownKey_ComesBackMasked()
    {
        var store = new InMemoryAppConfigStore();
        await store.SetAsync("Github:App:PrivateKey", "-----BEGIN PRIVATE KEY-----MIIEabcd");
        await store.SetAsync(AppConfigKeys.GithubRepoOwner, "acme");

        var result = await SettingsEndpoints.ReadPrefixAsync("Github", store, RealCatalog(), TestContext.Current.CancellationToken);
        var values = result.ShouldBeOfType<Ok<Dictionary<string, string?>>>().Value.ShouldNotBeNull();

        values["Github:App:PrivateKey"].ShouldBe("••••abcd");
        values[AppConfigKeys.GithubRepoOwner].ShouldBe("acme");
    }

    private static SettingsCatalog RealCatalog() => new([new LlmSettings()]);

    private sealed class TextNamedLikeSecret : ISettingsContributor
    {
        public IEnumerable<SettingDescriptor> Describe()
        {
            yield return new("Llm:Odd:ApiKey", SettingKind.Text, "LLM", "Odd");
        }
    }
}
