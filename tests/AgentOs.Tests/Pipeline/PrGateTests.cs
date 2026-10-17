// Plan 0.5 — the QA gate in front of every pull request: only a result QA PASSED on real model output may
// become a PR. Failed runs, the iteration cap with QA still failing, a sub-threshold last score, a missing QA
// report and offline canned output are all blocked, each with a reason the user can act on.

using System;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Pipeline;

public sealed class PrGateTests
{
    private static readonly AgentMetrics Real = new("Claude", "claude-sonnet-4-6", 10, 10, 0.01m, TimeSpan.Zero);

    private static PipelineResult Result(PipelineStatus status, params double[] scores)
        => new(
            new UserStory("s", 1, "en"),
            new RequirementSpec("T", "S", [], [], [], [], [], [], Real),
            new CodeArtifact("n", "a", [], null, Real),
            new TestArtifact("x", [], 0, 0, 0, 0, Real),
            Array.ConvertAll(scores, s => new QaReport(s, s >= QaThresholds.PassScore, s < QaThresholds.PassScore, [], [], Real)),
            status, Real);

    [Fact]
    public void Evaluate_QaPassedOnRealOutput_Allows()
        => PrGate.Evaluate(Result(PipelineStatus.Done, 0.6, 0.85)).ShouldBe(PrGateDecision.Allow);

    [Fact]
    public void Evaluate_IterationCapWithQaFailing_BlocksWithScore()
    {
        var gate = PrGate.Evaluate(Result(PipelineStatus.MaxIterationReached, 0.5, 0.62, 0.71));
        gate.Allowed.ShouldBeFalse();
        gate.Reason.ShouldBe("PR not opened: QA did not pass after 3 iteration(s) — last score 0.71, needs 0.80.");
    }

    [Fact]
    public void Evaluate_DoneButLastScoreUnderThreshold_Blocks()
        => PrGate.Evaluate(Result(PipelineStatus.Done, 0.79)).Allowed.ShouldBeFalse();

    [Fact]
    public void Evaluate_Failed_Blocks()
        => PrGate.Evaluate(Result(PipelineStatus.Failed, 0.9)).Reason!.ShouldContain("pipeline failed");

    [Fact]
    public void Evaluate_NoQaReport_Blocks()
        => PrGate.Evaluate(Result(PipelineStatus.Done)).Reason!.ShouldContain("no QA report");

    [Fact]
    public void Evaluate_OfflineCannedOutput_BlocksEvenWhenQaPassed()
    {
        var offline = Real with { Provider = "Offline" };
        var result = Result(PipelineStatus.Done, 0.95) with { Code = new CodeArtifact("n", "a", [], null, offline) };
        var gate = PrGate.Evaluate(result);
        gate.Allowed.ShouldBeFalse();
        gate.Reason!.ShouldContain("offline fallback");
    }
}
