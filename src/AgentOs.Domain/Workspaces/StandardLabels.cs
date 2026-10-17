// Bootstrap — the standard label taxonomy AgentOS.NET stamps on a board's repos so generated tickets are
// classified consistently. Four axes: type (what kind of work), area (which layer), priority, and the
// ai gate (ai:ready = AgentOS.NET may auto-run it; needs-human = it must not). Lives in Domain so the
// Web can seed it and the Pipeline decomposer (slice 2) can constrain itself to these names — both
// reference Domain only. Colors are GitHub 6-hex without a leading '#'.

using System.Collections.Generic;

namespace AgentOs.Domain.Workspaces;

/// <summary>The canonical label set AgentOS.NET seeds onto a repo and decomposes tickets against.</summary>
public static class StandardLabels
{
    /// <summary>The <c>ai:ready</c> gate — a ticket the agent is allowed to pick up and run autonomously.</summary>
    public const string AiReady = "ai:ready";

    /// <summary>The <c>needs-human</c> gate — a ticket that must not be auto-run.</summary>
    public const string NeedsHuman = "needs-human";

    /// <summary>The full taxonomy, seeded as one idempotent batch.</summary>
    public static IReadOnlyList<LabelSpec> All { get; } =
    [
        // type — what kind of work
        new("type:feature", "1f883d", "New capability or user-facing behavior"),
        new("type:bug",     "d1242f", "Defect in existing behavior"),
        new("type:chore",   "8250df", "Maintenance, deps, tooling — no behavior change"),
        new("type:spike",   "bf8700", "Time-boxed investigation; outcome is knowledge"),

        // area — which layer the work touches (derived from the RequirementSpec)
        new("area:api",   "0969da", "HTTP surface / endpoints"),
        new("area:data",  "0a7a6b", "Entities, persistence, migrations"),
        new("area:ui",    "bc4c00", "User interface"),
        new("area:infra", "6e7781", "Build, deploy, configuration"),
        new("area:core",  "57606a", "Domain logic not tied to a single layer"),

        // priority
        new("p0", "cf222e", "Critical — do first"),
        new("p1", "d4a72c", "Normal priority"),
        new("p2", "54aeff", "Low priority / nice to have"),

        // ai gate
        new(AiReady,    "8957e5", "A coding agent can implement this from the ticket alone"),
        new(NeedsHuman, "cc317c", "Needs a human — do not auto-run"),
    ];
}
