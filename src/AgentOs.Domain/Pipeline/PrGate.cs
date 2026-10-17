// The QA gate in front of every pull request (plan 0.5). A pipeline result may become a PR only when QA PASSED
// on real model output. Blocked: a failed run, a run that hit the iteration cap with QA still failing, a last
// QA score under the pass threshold, and anything answered by the offline fallback (canned text must never
// ship). Enforced at the single PR seam (IGitHubPrService) and checked up front by callers for a clear status.

using System.Globalization;
using AgentOs.Domain.Qa;

namespace AgentOs.Domain.Pipeline;

/// <summary>Whether a pipeline result may be opened as a pull request, and why not.</summary>
/// <param name="Allowed">True when the PR may be opened.</param>
/// <param name="Reason">Why it was blocked (null when allowed) — shown to the user and logged.</param>
public sealed record PrGateDecision(bool Allowed, string? Reason)
{
    /// <summary>The allowed decision.</summary>
    public static PrGateDecision Allow { get; } = new(true, null);
}

/// <summary>Evaluates <see cref="PrGateDecision"/> for a <see cref="PipelineResult"/>.</summary>
public static class PrGate
{
    /// <summary>Decide whether <paramref name="result"/> may become a pull request.</summary>
    public static PrGateDecision Evaluate(PipelineResult result)
    {
        System.ArgumentNullException.ThrowIfNull(result);

        if (result.Status == PipelineStatus.Failed)
        {
            return Block("the pipeline failed");
        }
        if (result.IsOffline)
        {
            return Block("the offline fallback answered with canned text — add an LLM key or pair a runner, then run again");
        }

        var last = result.QaHistory is { Count: > 0 } history ? history[^1] : null;
        if (last is null)
        {
            return Block("no QA report was produced");
        }
        if (result.Status != PipelineStatus.Done || last.Score < QaThresholds.PassScore)
        {
            return Block(string.Create(CultureInfo.InvariantCulture,
                $"QA did not pass after {result.IterationCount} iteration(s) — last score {last.Score:0.00}, needs {QaThresholds.PassScore:0.00}"));
        }
        return PrGateDecision.Allow;
    }

    private static PrGateDecision Block(string reason) => new(false, "PR not opened: " + reason + ".");
}

/// <summary>Thrown by the PR seam when <see cref="PrGate"/> blocks a result.</summary>
public sealed class PrGateBlockedException : System.InvalidOperationException
{
    public PrGateBlockedException(string message) : base(message) { }

    public PrGateBlockedException() { }

    public PrGateBlockedException(string message, System.Exception innerException) : base(message, innerException) { }
}
