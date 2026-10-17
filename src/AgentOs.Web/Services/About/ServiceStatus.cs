// System → About: the live status of every service the desktop depends on (Web, Api, Keycloak, Postgres,
// SMTP, LLM gateway). One immutable record per service; the snapshot is shared by every viewer for a few
// seconds (SystemStatusService), so it carries NO tenant data — the per-tenant LLM card is built per circuit.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Web.Services.About;

/// <summary>Health of one service as seen from the Web host.</summary>
public enum ServiceHealth
{
    /// <summary>Reachable and answering as expected.</summary>
    Healthy,
    /// <summary>Reachable but impaired (e.g. a sub-check failed, offline fallback serving canned answers).</summary>
    Degraded,
    /// <summary>Unreachable or failing.</summary>
    Down,
    /// <summary>Not part of this run (standalone Web, no database, email disabled). Not a failure.</summary>
    NotConfigured,
}

/// <summary>One labelled value on a service card.</summary>
public sealed record ServiceFact(string Label, string Value);

/// <summary>The status of one service. <see cref="Infra"/> holds endpoints, hosts, process ids and raw
/// errors — infrastructure detail shown only to platform admins.</summary>
public sealed record ServiceStatus(string Id, string Name, string Role, string Icon, ServiceHealth Health)
{
    /// <summary>Version the service reports, when it reports one.</summary>
    public string? Version { get; init; }

    /// <summary>Round-trip time of the probe, in milliseconds (null for the Web host itself).</summary>
    public double? LatencyMs { get; init; }

    /// <summary>Short label for the service map when a version is not the useful thing to show
    /// ("Live · Claude", "Offline · canned").</summary>
    public string? MapLabel { get; init; }

    /// <summary>One line under the name ("Realm agentic · PAR + PKCE", "Unreachable").</summary>
    public string? Summary { get; init; }

    /// <summary>Facts any signed-in user may see.</summary>
    public IReadOnlyList<ServiceFact> Facts { get; init; } = [];

    /// <summary>Platform-admin-only facts (URLs, hosts, process info, error text).</summary>
    public IReadOnlyList<ServiceFact> Infra { get; init; } = [];
}

/// <summary>Every service's status at one instant.</summary>
public sealed record SystemSnapshot(DateTimeOffset CheckedAt, IReadOnlyList<ServiceStatus> Services)
{
    /// <summary>The worst health among the services that are part of this run.</summary>
    public ServiceHealth Overall => Worst(Services);

    /// <summary>Worst-of fold over <paramref name="services"/>; <see cref="ServiceHealth.NotConfigured"/> is
    /// ignored (an absent optional service is not an outage).</summary>
    public static ServiceHealth Worst(IEnumerable<ServiceStatus> services)
    {
        var worst = ServiceHealth.Healthy;
        foreach (var s in services)
        {
            if (s.Health == ServiceHealth.Down) { return ServiceHealth.Down; }
            if (s.Health == ServiceHealth.Degraded) { worst = ServiceHealth.Degraded; }
        }
        return worst;
    }

    /// <summary>Headline for the About page ("All systems operational", "1 degraded · 1 down").</summary>
    public static string Headline(IReadOnlyCollection<ServiceStatus> services)
    {
        var down = services.Count(s => s.Health == ServiceHealth.Down);
        var degraded = services.Count(s => s.Health == ServiceHealth.Degraded);
        return (down, degraded) switch
        {
            (0, 0) => "All systems operational",
            (0, _) => $"{degraded} degraded",
            (_, 0) => $"{down} down",
            _ => $"{degraded} degraded · {down} down",
        };
    }
}
