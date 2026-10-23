// Self-improvement, step two: after a ticket merges, the Retrospective agent reads a project's raw lessons (what
// reviews, failing checks and acceptance tests said) next to its current practices and returns the project's practices
// — short, durable, actionable — plus which raw lessons they now cover. Every agent reads the practices before it
// works on the project; Insights › Delivery shows whether first-try passes go up.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Memory;

/// <summary>An item the Retrospective agent sees, by its short id (<c>L1</c> lessons, <c>P1</c> practices).</summary>
public sealed record RetrospectiveItem(string Id, string Text, bool Pinned);

/// <summary>What the Retrospective agent works from.</summary>
/// <param name="Project">The repository (owner/repo).</param>
/// <param name="MergedTicket">The ticket that just merged (title), for context.</param>
public sealed record RetrospectiveInput(string TenantId, string MemberId, string Project, string MergedTicket,
    IReadOnlyList<RetrospectiveItem> Lessons, IReadOnlyList<RetrospectiveItem> Practices);

/// <summary>The project's practices after the retrospective, and the lessons they absorbed.</summary>
public sealed record RetrospectiveResult(bool Ran, IReadOnlyList<string> Practices, IReadOnlyList<string> AbsorbedLessonIds, string Summary, string? Error)
{
    /// <summary>A retrospective that couldn't run.</summary>
    public static RetrospectiveResult NotRun(string why) => new(false, [], [], string.Empty, why);
}

/// <summary>Distils a project's lessons into its practices.</summary>
public interface IRetrospectiveAgent
{
    Task<RetrospectiveResult> RunAsync(RetrospectiveInput input, CancellationToken ct = default);
}
