// The orchestrator merges only when every check passed and fixes only on a real failure: how check runs combine.

using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration;
using Octokit;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Integration;

public sealed class PrChecksCombineTests
{
    [Fact]
    public void NoRuns_IsNone() => GitHubPrCreationService.Combine([]).ShouldBe(PrChecksState.None);

    [Fact]
    public void AllSucceededOrSkipped_IsPassed() =>
        GitHubPrCreationService.Combine([(CheckStatus.Completed, CheckConclusion.Success), (CheckStatus.Completed, CheckConclusion.Skipped)])
            .ShouldBe(PrChecksState.Passed);

    [Fact]
    public void OneStillRunning_IsPending() =>
        GitHubPrCreationService.Combine([(CheckStatus.Completed, CheckConclusion.Success), (CheckStatus.InProgress, null)])
            .ShouldBe(PrChecksState.Pending);

    [Theory]
    [InlineData(CheckConclusion.Failure)]
    [InlineData(CheckConclusion.TimedOut)]
    [InlineData(CheckConclusion.Cancelled)]
    public void AnyFailed_IsFailed_EvenWhileOthersRun(CheckConclusion conclusion) =>
        GitHubPrCreationService.Combine([(CheckStatus.Queued, null), (CheckStatus.Completed, conclusion)])
            .ShouldBe(PrChecksState.Failed);
}
