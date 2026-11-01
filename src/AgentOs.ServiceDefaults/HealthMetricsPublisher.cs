// E1.10 — publishes every health check's status as a metric, so the "health probe down" alert works from inside
// the process too (an outside-in blackbox probe of /health remains the primary signal — see docs/OPERATIONS.md).
// IHealthCheckPublisher runs on the HealthCheckPublisherOptions schedule — set in AddDefaultHealthChecks to every
// PublishPeriod (60 s) after FirstPublishDelay — independently of anyone calling /health.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentOs.ServiceDefaults;

/// <summary>Records <c>agentos.health.status</c> (1 healthy · 0.5 degraded · 0 unhealthy) per check.</summary>
internal sealed class HealthMetricsPublisher : IHealthCheckPublisher
{
    /// <summary>Meter name — registered in <see cref="Extensions.ConfigureOpenTelemetry"/>.</summary>
    internal const string MeterName = "AgentOs.Health";

    /// <summary>Gauge name (Prometheus: <c>agentos_health_status</c>).</summary>
    internal const string InstrumentName = "agentos.health.status";

    /// <summary>How often the checks run for this metric. Each run executes EVERY check (Postgres, the Keycloak
    /// discovery document) on every replica, so it stays well above the framework's 30 s default.</summary>
    internal static readonly TimeSpan PublishPeriod = TimeSpan.FromSeconds(60);

    /// <summary>Delay before the first publish after start-up.</summary>
    internal static readonly TimeSpan FirstPublishDelay = TimeSpan.FromSeconds(15);

    private static readonly Meter Meter = new(MeterName);
    private static readonly Gauge<double> Status = Meter.CreateGauge<double>(
        InstrumentName, unit: "{status}", description: "Health check status: 1 healthy, 0.5 degraded, 0 unhealthy.");

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (name, entry) in report.Entries)
        {
            Status.Record(ValueOf(entry.Status), new TagList { { "agentos.health.check", name } });
        }
        Status.Record(ValueOf(report.Status), new TagList { { "agentos.health.check", "overall" } });
        return Task.CompletedTask;
    }

    internal static double ValueOf(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 1d,
        HealthStatus.Degraded => 0.5d,
        _ => 0d,
    };
}
