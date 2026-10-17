// PipelineResult.IsOffline — a result is canned (never shippable) when ANY stage was answered by the offline
// client, including a QA pass deep in the history.

using System;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Domain;

public sealed class PipelineResultOfflineTests
{
    private static AgentMetrics M(string provider) => new(provider, "m", 1, 1, 0m, TimeSpan.Zero);

    private static PipelineResult Result(string spec = "Claude", string code = "Claude", string tests = "Claude",
        params string[] qa)
        => new(
            new UserStory("s", 1, "en"),
            new RequirementSpec("T", "S", [], [], [], [], [], [], M(spec)),
            new CodeArtifact("n", "a", [], null, M(code)),
            new TestArtifact("x", [], 0, 0, 0, 0, M(tests)),
            Array.ConvertAll(qa.Length == 0 ? ["Claude"] : qa, p => new QaReport(1, true, false, [], [], M(p))),
            PipelineStatus.Done, M("Claude"));

    [Fact]
    public void IsOffline_AllStagesLive_IsFalse() => Result().IsOffline.ShouldBeFalse();

    [Theory]
    [InlineData("spec")]
    [InlineData("code")]
    [InlineData("tests")]
    public void IsOffline_AnyArtifactStageOffline_IsTrue(string stage)
    {
        var r = stage switch
        {
            "spec" => Result(spec: LlmProviders.Offline),
            "code" => Result(code: LlmProviders.Offline),
            _ => Result(tests: LlmProviders.Offline),
        };

        r.IsOffline.ShouldBeTrue();
    }

    [Fact]
    public void IsOffline_EarlierQaPassOffline_IsTrue()
        => Result(qa: ["Claude", LlmProviders.Offline, "Claude"]).IsOffline.ShouldBeTrue();

    [Fact]
    public void IsOffline_ProviderMatchIsCaseInsensitive()
        => Result(code: "offline").IsOffline.ShouldBeTrue();
}
