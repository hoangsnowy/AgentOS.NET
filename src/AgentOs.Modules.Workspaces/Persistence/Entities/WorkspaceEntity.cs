// Board reshape — a Workspace is now a connected PLANNING BOARD (GitHub Projects v2 today, an Azure
// DevOps board later) that spans MANY repos. The internal type keeps the name WorkspaceEntity (so
// the table/schema and the sessions.WorkspaceId reference are unchanged); the UI labels it "Board".
// The repos under the board live in WorkspaceRepoEntity. Stores only a CredentialRef (a key into the
// encrypted AppConfig store), never the access token. Tenant-stamped on write; reads tenant-filtered.

using System;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Workspaces.Persistence.Entities;

/// <summary>A persisted workspace = a connected planning board + how to find its credentials. Spans many <see cref="WorkspaceRepoEntity"/>.</summary>
public sealed class WorkspaceEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SourceProviderKind Kind { get; set; }

    // ── Board binding ───────────────────────────────────────────────────────────────────────────
    /// <summary>Login of the org/user that owns the board (GitHub), or the Azure DevOps organization.</summary>
    public string ProjectOwner { get; set; } = string.Empty;

    /// <summary><c>org</c> or <c>user</c> — how the board owner is resolved on GitHub.</summary>
    public string ProjectScope { get; set; } = "user";

    /// <summary>Board number (GitHub Projects v2). Null for a degenerate repo-only board with no board attached yet (e.g. a migrated pre-board workspace).</summary>
    public int? ProjectNumber { get; set; }

    /// <summary>Cached GraphQL node id of the board, resolved at connect time.</summary>
    public string? ProjectNodeId { get; set; }

    /// <summary>Azure DevOps project that owns the board (ignored by GitHub).</summary>
    public string? Project { get; set; }

    // ── Legacy single-repo coordinates (pre-board). Nullable + vestigial after the reshape fold,
    //    which copies them into a WorkspaceRepoEntity row. New connects leave these null. ──────────
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string? DefaultBranch { get; set; }
    public string? RemoteUrl { get; set; }

    /// <summary>Key into the encrypted AppConfig store where the access token lives. Never the secret itself.</summary>
    public string CredentialRef { get; set; } = string.Empty;

    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Status { get; set; } = "Connected";

    // Autopilot — what the project orchestrator may do on this board unattended: "Off", "Build" (start approved tickets
    // in dependency order, fix failing checks) or "BuildAndMerge" (and merge once every check passes). It acts as the
    // member who switched it on (their computer, their AI plan).
    public string Autopilot { get; set; } = "Off";
    public string? AutopilotUserId { get; set; }
}
