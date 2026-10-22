// The Autopilot log keeps decisions in memory and writes them through to the database; the first read merges the two.
// A decision made before that read is in both, stamped separately — it must show once.

using System;
using AgentOs.Web.Services.Orchestration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Web;

public sealed class OrchestratorLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_SameDecisionStampedTwice_ShowsOnce()
    {
        var stored = new[] { (T0.AddMilliseconds(40), "Started #32."), (T0.AddMinutes(-9), "Merged #13.") };
        var memory = new[] { (T0, "Started #32.") };

        var merged = OrchestratorLog.Merge(stored, memory, 50);

        merged.Count.ShouldBe(2);
        merged[0].Text.ShouldBe("Started #32.");
        merged[1].Text.ShouldBe("Merged #13.");
    }

    [Fact]
    public void Merge_SameTextLater_IsANewLine()
    {
        var stored = new[] { (T0, "Checked: nothing to do right now.") };
        var memory = new[] { (T0.AddMinutes(5), "Checked: nothing to do right now.") };

        OrchestratorLog.Merge(stored, memory, 50).Count.ShouldBe(2);
    }

    [Fact]
    public void Merge_NewestFirstAndCapped()
    {
        var stored = new[] { (T0.AddMinutes(1), "b"), (T0, "a") };
        var memory = new[] { (T0.AddMinutes(2), "c") };

        OrchestratorLog.Merge(stored, memory, 2).ShouldBe([(T0.AddMinutes(2), "c"), (T0.AddMinutes(1), "b")]);
    }
}
