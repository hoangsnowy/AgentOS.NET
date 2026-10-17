// Aspire service defaults: OpenTelemetry, health checks, service discovery, HTTP resilience.
// Shared by AgentOs.Api and AgentOs.Web; wired by the AppHost.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgentOs.ServiceDefaults;

/// <summary>Aspire-compatible defaults shared by the API + Web hosts.</summary>
public static class Extensions
{
    /// <summary>Adds OpenTelemetry, default health checks, service discovery, HTTP client resilience,
    /// and forwarded-headers handling (for TLS-terminating reverse proxies / Container Apps ingress).</summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddAgentOsLogging();
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        // Behind Azure Container Apps (and any TLS-terminating ingress) the app receives plain HTTP with
        // X-Forwarded-Proto: https. Without honouring it the OIDC middleware builds an http:// redirect_uri
        // (Keycloak then rejects it) and CookieSecurePolicy.Always drops the auth cookie. The proxy IP is
        // not known ahead of time in ACA, so the default KnownNetworks/KnownProxies allow-list (loopback
        // only) would discard the headers — clear it and trust the single hop in front of us.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // `localhost` shares ONE cookie jar across every dev app on the box, so the Cookie header to a
        // dev host can blow past Kestrel's 32 KB default → HTTP 431 (Request Header Fields Too Large)
        // before the request even reaches auth. 128 KB still wasn't enough on a heavily-used dev box
        // (many localhost apps piling cookies into the one jar), so this is 512 KB — kept in lockstep
        // with Keycloak's QUARKUS_HTTP_LIMITS_MAX_HEADER_SIZE in the AppHost so the whole OIDC round-trip
        // survives without clearing cookies or using an incognito window. Cloud requests carry no such
        // bloat, so the larger ceiling is harmless there.
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
            o.Limits.MaxRequestHeadersTotalSize = 512 * 1024);

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Applies forwarded headers as the FIRST middleware so every downstream component (OIDC,
    /// cookie policy, link generation) sees the original https scheme + client IP. Call before any auth.</summary>
    public static WebApplication UseAgentOsForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        return app;
    }

    /// <summary>Configures OpenTelemetry logging, metrics, and tracing (+ OTLP export when configured).</summary>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            // Mask credential-shaped substrings on every exported record (OTLP/App Insights).
            logging.AddProcessor(new SecretMaskingLogProcessor());
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("AgentOs.Llm") // SharedKernel.Telemetry.LlmTelemetry.SourceName — kept literal to avoid coupling this Aspire-shared project.
                .AddMeter("AgentOs.Tools")) // evidence health: inline writes + lost rows (BufferedToolInvocationLog.MeterName)
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("AgentOs.Llm"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Adds a "self" liveness check plus real readiness probes: Postgres when
    /// <c>ConnectionStrings:DefaultConnection</c> is configured, and the OIDC discovery document when
    /// <c>Auth:Keycloak:Authority</c> is set outside Development (dev/standalone runs must stay healthy
    /// with no external services — the Aspire dashboard already covers dependency health in dev).</summary>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var checks = builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            checks.Add(new HealthCheckRegistration(
                "postgres",
                _ => new PostgresHealthCheck(connectionString),
                HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(5)));
        }

        // Probe the discovery document over the backend-reachable URL: behind ACA the public
        // authority is not hairpin-reachable from inside the environment, so an explicit
        // MetadataAddress (internal http) wins over the public Authority when configured.
        var metadataAddress = builder.Configuration["Auth:Keycloak:MetadataAddress"];
        var authority = builder.Configuration["Auth:Keycloak:Authority"];
        var probeUrl = !string.IsNullOrWhiteSpace(metadataAddress)
            ? metadataAddress
            : string.IsNullOrWhiteSpace(authority) ? null : authority.TrimEnd('/') + "/.well-known/openid-configuration";
        if (!builder.Environment.IsDevelopment()
            && !string.IsNullOrWhiteSpace(probeUrl)
            && Uri.TryCreate(probeUrl, UriKind.Absolute, out var metadataUri))
        {
            builder.Services.AddHttpClient(OidcMetadataHealthCheck.ClientName);
            checks.Add(new HealthCheckRegistration(
                "keycloak",
                sp => new OidcMetadataHealthCheck(sp.GetRequiredService<IHttpClientFactory>(), metadataUri),
                HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(10)));
        }

        return builder;
    }

    /// <summary>Maps /health (readiness — all checks) and /alive (liveness — live-tagged checks),
    /// both with a JSON body naming each check and its status. Mapped UNCONDITIONALLY: Container Apps /
    /// k8s liveness+readiness probes must work in Production, not just Development.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = HealthJsonWriter.WriteAsync,
        });
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = HealthJsonWriter.WriteAsync,
        });

        return app;
    }
}
