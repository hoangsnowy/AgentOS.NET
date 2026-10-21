// AgentOs.Domain/Sessions/IAcceptanceAgent.cs
// Agentic Testing: accept or reject a proposed change against its ticket's acceptance criteria.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Sessions;

/// <summary>What to test: the ticket, and the repo + branch of its open pull request.</summary>
public sealed record AcceptanceRequest(
    Guid SessionId,
    string TenantId,
    string MemberId,
    WorkRepo Repo,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    string? CliProfile = null);

/// <summary>One acceptance criterion's result: <c>pass</c>, <c>fail</c> or <c>not-checked</c> (with the reason).</summary>
public sealed record AcceptanceCriterion(string Criterion, string Result, string Evidence);

/// <summary>The Testing agent's verdict. <see cref="Ran"/> is false when it could not test at all (no computer, …).</summary>
public sealed record AcceptanceVerdict(bool Ran, bool Passed, IReadOnlyList<AcceptanceCriterion> Criteria, string Summary, string? Error)
{
    /// <summary>The criteria that failed, as the findings sent back to the Coding agent.</summary>
    public IEnumerable<AcceptanceCriterion> Failures =>
        Criteria.Where(c => string.Equals(c.Result, "fail", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Tests a proposed change on a paired computer, read-only: it never edits, commits or pushes.</summary>
public interface IAcceptanceAgent
{
    Task<AcceptanceVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default);
}
