// Agentic review: before a proposed change is tested, a second agent reads its diff against the ticket and either
// approves it or asks the Coding agent for changes — the code review a person would otherwise do on the PR.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>One review finding. <see cref="Severity"/> is <c>must-fix</c> (blocks the change), <c>should-fix</c> or <c>nit</c>.</summary>
public sealed record ReviewFinding(string Severity, string File, int? Line, string Comment)
{
    /// <summary>True for a finding that blocks the change.</summary>
    public bool Blocks => Severity == "must-fix";
}

/// <summary>The Reviewer agent's verdict. <see cref="Ran"/> is false when it could not review at all (no computer, …).</summary>
public sealed record ReviewVerdict(bool Ran, bool Approved, IReadOnlyList<ReviewFinding> Findings, string Summary, string? Error)
{
    /// <summary>The findings that block the change.</summary>
    public IEnumerable<ReviewFinding> Blocking => Findings.Where(f => f.Blocks);
}

/// <summary>Reviews a proposed change on a paired computer, read-only: it never edits, commits or pushes.</summary>
public interface IReviewAgent
{
    /// <summary>Review the change on <c>request.Repo.ExistingBranch</c> against its ticket.</summary>
    Task<ReviewVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default);
}
