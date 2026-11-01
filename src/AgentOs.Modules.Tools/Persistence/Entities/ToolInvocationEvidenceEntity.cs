// M1 — durable evidence row (schema tools.tool_invocations). One row per governed tool invocation
// (success, error, or policy denial). Partitioned by TenantId; the log API is tenant-explicit, so this
// context needs no global query filter.

using System;

namespace AgentOs.Modules.Tools.Persistence.Entities;

/// <summary>A persisted <c>ToolInvocationEvidence</c> row.</summary>
public sealed class ToolInvocationEvidenceEntity
{
    public Guid Id { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? RunId { get; set; }
    public string? SessionId { get; set; }
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }

    /// <summary>Null = on the server; "member-computer" = the CLI on a member's paired computer ran it.</summary>
    public string? Origin { get; set; }
    public string? AgentRole { get; set; }
    public string? RunnerId { get; set; }

    /// <summary>Null = the server's tool policy; "cli-scoped-permissions" = the CLI's own scoped permissions.</summary>
    public string? PolicySource { get; set; }
    public bool Denied { get; set; }
}
