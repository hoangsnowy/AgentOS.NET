// Audit-trail health. Audit writes are best-effort (IAuditLog: a write failure never rolls back the surrounding
// operation), so a row that could not be recorded must never vanish silently: it is logged as an error AND counted
// here — the same pattern as the tool-evidence `agentos.evidence.lost` counter. Exported with the host's OTel metrics
// (ServiceDefaults registers the meter); docs/OPERATIONS.md lists the instrument.

using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace AgentOs.Modules.Tenants;

/// <summary>Meter and instruments for the audit trail.</summary>
public static class AuditTelemetry
{
    /// <summary>The meter name (registered with <c>.AddMeter("AgentOs.Audit")</c> in ServiceDefaults).</summary>
    public const string MeterName = "AgentOs.Audit";

    /// <summary>Counter of audit rows that were not recorded, tagged <c>reason</c> (<see cref="Reasons"/>).</summary>
    public const string Lost = "agentos.audit.lost";

    /// <summary>Values of the <c>reason</c> tag on <see cref="Lost"/>.</summary>
    public static class Reasons
    {
        /// <summary>The entry named no tenant, so it belongs to no chain (a caller bug).</summary>
        public const string NoTenant = "no_tenant";

        /// <summary>The database refused or was unreachable.</summary>
        public const string Database = "database";
    }

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LostCounter =
        Meter.CreateCounter<long>(Lost, unit: "{event}", description: "Audit rows that were not recorded (best-effort writes), by reason.");

    /// <summary>Count one audit row that was not recorded.</summary>
    internal static void RecordLost(string reason) => LostCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
