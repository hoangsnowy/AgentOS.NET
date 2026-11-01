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

    /// <summary>Full tool-output length in chars (2.7 shaping evidence); null on rows written before it.</summary>
    public int? OutputChars { get; set; }

    /// <summary>Length the model received after shaping; null on rows written before it.</summary>
    public int? ShapedOutputChars { get; set; }

    /// <summary>The Tools:MaxOutputTokens budget the result was shaped against; null when unshaped and on older rows.</summary>
    public int? MaxOutputTokens { get; set; }
}
