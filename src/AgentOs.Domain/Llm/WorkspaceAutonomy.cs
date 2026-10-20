// AgentOs.Domain/Llm/WorkspaceAutonomy.cs
// The autonomy levels a request can ask a paired runner for (LlmRequest.Autonomy).

namespace AgentOs.Domain.Llm;

/// <summary>Values for <see cref="LlmRequest.Autonomy"/>. Absent = answer only.</summary>
public static class WorkspaceAutonomy
{
    /// <summary>Clone, edit, build, test and push inside a fresh per-run directory; history rewrites, settings and
    /// credential stores stay off limits.</summary>
    public const string Scoped = "scoped";
}
