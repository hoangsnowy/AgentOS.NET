// Batch 1 — readiness probes in ServiceDefaults: registration is conditional (Postgres only with a
// connection string, Keycloak only outside Development) and the OIDC probe maps HTTP outcomes to
// health statuses without throwing.
using AgentOs.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.ServiceDefaults;

public class HealthChecksTests
{
    private static IReadOnlyList<string> RegisteredCheckNames(
        string environment, params (string Key, string? Value)[] config)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
        });
        builder.Configuration.AddInMemoryCollection(
            config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)));

        builder.AddDefaultHealthChecks();

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        return options.Value.Registrations.Select(r => r.Name).ToList();
    }

    [Fact]
    public void AddDefaultHealthChecks_NoExternalConfig_RegistersSelfOnly() =>
        RegisteredCheckNames("Development").ShouldBe(["self"]);

    [Fact]
    public void AddDefaultHealthChecks_WithConnectionString_RegistersPostgres() =>
        RegisteredCheckNames("Development",
            ("ConnectionStrings:DefaultConnection", "Host=localhost;Database=agentos"))
            .ShouldBe(["self", "postgres"]);

    [Fact]
    public void AddDefaultHealthChecks_AuthorityInDevelopment_SkipsKeycloak() =>
        RegisteredCheckNames("Development",
            ("Auth:Keycloak:Authority", "http://localhost:8080/realms/agentic"))
            .ShouldBe(["self"]);

    [Fact]
    public void AddDefaultHealthChecks_AuthorityInProduction_RegistersKeycloak() =>
        RegisteredCheckNames("Production",
            ("Auth:Keycloak:Authority", "https://id.example.com/realms/agentic"))
            .ShouldBe(["self", "keycloak"]);

    [Fact]
    public void AddDefaultHealthChecks_HealthMetricPublisher_RunsChecksOncePerMinute()
    {
        // E1.10: the health-status metric runs every check (incl. the Keycloak discovery fetch) per replica — not on
        // the framework's 30 s default.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.AddDefaultHealthChecks();

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckPublisherOptions>>().Value;
        options.Period.ShouldBe(TimeSpan.FromSeconds(60));
        options.Delay.ShouldBe(TimeSpan.FromSeconds(15));
        provider.GetServices<IHealthCheckPublisher>().ShouldContain(p => p is HealthMetricsPublisher);
    }

    // ── OIDC metadata probe behavior ─────────────────────────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static OidcMetadataHealthCheck Probe(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(respond)));
        return new OidcMetadataHealthCheck(
            factory, new Uri("https://id.example.com/realms/agentic/.well-known/openid-configuration"));
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "keycloak", Substitute.For<IHealthCheck>(), HealthStatus.Unhealthy, tags: null),
    };

    [Fact]
    public async Task OidcMetadataHealthCheck_Http200_ReportsHealthy()
    {
        var result = await Probe(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)).CheckHealthAsync(Context());
        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task OidcMetadataHealthCheck_Http503_ReportsUnhealthyWithStatusCode()
    {
        var result = await Probe(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))
            .CheckHealthAsync(Context());
        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("503");
    }

    [Fact]
    public async Task OidcMetadataHealthCheck_ConnectionRefused_ReportsUnhealthyWithoutThrowing()
    {
        var result = await Probe(_ => throw new HttpRequestException("refused")).CheckHealthAsync(Context());
        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }
}
