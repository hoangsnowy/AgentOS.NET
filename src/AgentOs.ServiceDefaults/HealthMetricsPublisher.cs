// E1.10 — publishes every health check's status as a metric, so the "health probe down" alert works from inside
// the process too (an outside-in blackbox probe of /health remains the primary signal — see docs/OPERATIONS.md).
// IHealthCheckPublisher runs on the HealthCheckPublisherOptions schedule (default: every 30 s after a 5 s delay),
// independently of anyone calling /health.

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
