// One project-orchestrator decision on a board ("Started #11 — #3 is done."), kept so the Autopilot tab shows the
// history across restarts. Tenant-scoped (EF filter + RLS), trimmed to the newest few hundred per board.

using System;

namespace AgentOs.Modules.Workspaces.Persistence.Entities;

public sealed class AutopilotEventEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid BoardId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Text { get; set; } = string.Empty;
}
