// GAP-01 (docs/security/threat-model.md §15), both halves:
//   • Open self-signup — anyone reaching /signup could create a tenant and become its admin. It is now the platform
//     setting Tenants:OpenSignup (default on in Development only); when off, only an invitation creates an account, and
//     with no settings registry at all the service fails closed.
//   • The tenant-scoped Azure OpenAI endpoint — validated only as "absolute https://", so a tenant admin could aim the
//     server's calls (with the tenant's key) at any host. A Settings value must now be an Azure OpenAI resource host,
//     checked on save (the setting's validator and the /settings registry) AND at call time; the operator's bootstrap
//     endpoint stays trusted (Private Link resolves to private addresses).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Llm;
using AgentOs.Modules.AppConfig.Endpoints;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Modules.Tenants.Settings;
using AgentOs.SharedKernel.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class OpenSignupAndAzureEndpointTests
{
    private const string Password = "Correct-Horse-9-Battery!";

    // ── Open self-signup ──

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void TenantsSettings_DefaultIsOnInDevelopmentOnly(bool development, string expected)
    {
        var d = new TenantsSettings(development).Describe().ShouldHaveSingleItem();
        d.Key.ShouldBe(TenantsSettings.OpenSignupKey);
        d.DefaultValue.ShouldBe(expected);
        d.Scope.ShouldBe(SettingScope.Platform);   // a tenant admin can't open signup for the platform
    }

    [Theory]
    [InlineData(null)]          // auto-create
    [InlineData("acme")]        // explicit slug
    public async Task Signup_NewWorkspace_WhenClosed_IsRefused_AndNoUserIsCreated(string? slug)
    {
        var (service, kc) = Service(new FixedSettings("false"));

        await Should.ThrowAsync<SignupClosedException>(() =>
            service.SignupAsync(new TenantSignupRequest("alice", Password, null, slug, null, null), TestContext.Current.CancellationToken));

        await kc.DidNotReceiveWithAnyArgs().CreateUserAsync(default!, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Signup_NoSettingsRegistry_FailsClosed()
    {
        var (service, _) = Service(settings: null);
        (await service.IsOpenSignupEnabledAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        await Should.ThrowAsync<SignupClosedException>(() =>
            service.SignupAsync(new TenantSignupRequest("alice", Password, null, null, null, null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Signup_WithInvitation_WhenClosed_StillJoins()
    {
        var (service, kc) = Service(new FixedSettings("false"), existingTenant: "acme");
        var invite = service.CreateInvitation("acme", "member", null, TimeSpan.FromHours(1));

        var outcome = await service.SignupAsync(
            new TenantSignupRequest("bob", Password, null, null, null, invite.Token), TestContext.Current.CancellationToken);

        outcome.Mode.ShouldBe(SignupMode.Invite);
        outcome.TenantId.ShouldBe("acme");
        await kc.Received(1).CreateUserAsync("bob", Arg.Any<string>(), "acme", Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Password, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Signup_NewWorkspace_WhenOpen_CreatesTenantAndAdmin()
    {
        var (service, _) = Service(new FixedSettings("true"));
        (await service.IsOpenSignupEnabledAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var outcome = await service.SignupAsync(
            new TenantSignupRequest("alice", Password, null, "acme", null, null), TestContext.Current.CancellationToken);

        outcome.Mode.ShouldBe(SignupMode.Slug);
    }

    // ── Azure OpenAI endpoint allowlist ──

    [Theory]
    [InlineData("https://acme.openai.azure.com")]
    [InlineData("https://acme.openai.azure.com/")]
    [InlineData("https://ACME.OpenAI.Azure.com/openai")]
    [InlineData("https://acme.cognitiveservices.azure.com")]
    [InlineData("https://acme.services.ai.azure.com")]
    [InlineData("https://acme.openai.azure.us")]
    public void Validate_AzureResourceHost_IsAllowed(string endpoint) => AzureOpenAiEndpoint.Validate(endpoint).ShouldBeNull();

    [Theory]
    [InlineData("http://acme.openai.azure.com")]              // not https
    [InlineData("https://169.254.169.254/metadata")]          // cloud metadata
    [InlineData("https://10.0.0.5")]                          // private IP literal
    [InlineData("https://localhost")]
    [InlineData("https://internal.corp")]
    [InlineData("https://acme.openai.azure.com.evil.test")]  // suffix trick
    [InlineData("https://evilopenai.azure.com")]              // not a subdomain of openai.azure.com
    [InlineData("https://openai.azure.com")]                  // no resource name
    [InlineData("https://acme.openai.azure.com:8443")]        // non-default port
    [InlineData("https://user:pw@acme.openai.azure.com")]     // credentials in the URL
    [InlineData("not a url")]
    public void Validate_AnyOtherHost_IsRefused(string endpoint) => AzureOpenAiEndpoint.Validate(endpoint).ShouldNotBeNull();

    [Fact]
    public void SettingsRegistry_AzureEndpoint_RefusesInternalHost_AcceptsAzure()
    {
        SettingsKeyRegistry.ValidateValue(AppConfigKeys.LlmAzureEndpoint, "https://169.254.169.254").ShouldNotBeNull();
        SettingsKeyRegistry.ValidateValue(AppConfigKeys.LlmAzureEndpoint, "https://acme.openai.azure.com").ShouldBeNull();
        SettingsKeyRegistry.ValidateValue(AppConfigKeys.LlmAzureEndpoint, "").ShouldBeNull();   // clearing is always allowed
    }

    [Fact]
    public void LlmSettingDescriptor_AzureEndpoint_HasTheAllowlistValidator()
    {
        var d = System.Linq.Enumerable.Single(new LlmSettings(offlineFallbackDefault: false).Describe(), x => x.Key == AppConfigKeys.LlmAzureEndpoint);
        d.Validator.ShouldNotBeNull();
        d.Validator!("https://internal.corp").ShouldNotBeNull();
        d.Validator("https://acme.openai.azure.com").ShouldBeNull();
    }

    // A value stored before the check existed is refused when it is used, not only when it is saved.
    [Fact]
    public void Resolve_SettingsEndpointOutsideAllowlist_ThrowsAtCallTime()
        => Should.Throw<LlmException>(() => AzureEndpoints.Resolve("https://10.0.0.5", "https://ops.openai.azure.com"))
            .Message.ShouldContain("refused");

    [Fact]
    public void Resolve_SettingsEndpoint_IsUntrusted_SoItUsesTheSsrfGuardedTransport()
        => AzureEndpoints.Resolve("https://acme.openai.azure.com", null).ShouldBe(new AzureEndpoints.Resolved("https://acme.openai.azure.com", Untrusted: true));

    // The operator's own endpoint (env / user-secrets) is trusted as-is: a Private Link host resolves to a private address.
    [Fact]
    public void Resolve_NoSettingsValue_UsesBootstrapEndpoint_Trusted()
        => AzureEndpoints.Resolve(null, "https://aoai.privatelink.corp").ShouldBe(new AzureEndpoints.Resolved("https://aoai.privatelink.corp", Untrusted: false));

    // ── helpers ──

    private static (TenantSignupService Service, IKeycloakAdminClient Kc) Service(ISettings? settings, string? existingTenant = null)
    {
        var repo = Substitute.For<ITenantsRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            existingTenant is not null && ci.Arg<string>() == existingTenant ? new TenantRecord(existingTenant, existingTenant, DateTimeOffset.UtcNow) : null);
        var kc = Substitute.For<IKeycloakAdminClient>();
        kc.CreateUserAsync(default!, default!, default!, default!, default, default, default).ReturnsForAnyArgs("kc-user-1");
        var service = new TenantSignupService(repo, kc, Substitute.For<IAuditLog>(), new EphemeralDataProtectionProvider(),
            NullLogger<TenantSignupService>.Instance, settings);
        return (service, kc);
    }

    // ISettings is ValueTask-returning — a hand-written fake, never substituted.
    private sealed class FixedSettings(string? openSignup) : ISettings
    {
        public long Version => 0;

        public ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(key == TenantsSettings.OpenSignupKey && tenantId is null
                ? new SettingValue(openSignup, SettingSource.Platform)
                : new SettingValue(null, SettingSource.Default));

        public ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SettingView>>([]);

        public ValueTask<SettingWriteResult> SetAsync(
            string key, string? value, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SettingWriteResult.Success);
    }
}
