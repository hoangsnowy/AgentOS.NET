// Live task progress read off `claude -p --output-format stream-json --verbose` (shapes copied from a live run).

using System.Text.Json;
using AgentOs.Domain.Sessions;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class CliProgressTests
{
    private static string ToolUse(string name, object input) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { content = new object[] { new { type = "thinking", thinking = "…" }, new { type = "tool_use", id = "t1", name, input } } },
    });

    [Theory]
    [InlineData("Write", "{\"file_path\":\"src/A.cs\",\"content\":\"x\"}", "writing")]
    [InlineData("Edit", "{\"file_path\":\"src/A.cs\"}", "writing")]
    [InlineData("Read", "{\"file_path\":\"README.md\"}", "reading")]
    [InlineData("PowerShell", "{\"command\":\"git clone https://github.com/o/r.git .\"}", "getting-code")]
    [InlineData("Bash", "{\"command\":\"dotnet build TeacherHub.slnx -c Release\"}", "building")]
    [InlineData("Bash", "{\"command\":\"dotnet test --solution TeacherHub.slnx\"}", "testing")]
    [InlineData("Bash", "{\"command\":\"cd web && npm test\"}", "testing")]
    [InlineData("Bash", "{\"command\":\"git commit -m \\\"feat: x\\\"\"}", "saving")]
    [InlineData("Bash", "{\"command\":\"git push -u origin issue-7-ai-fix\"}", "sending")]
    public void Parse_ToolUse_MapsToPlainStep(string tool, string inputJson, string expected)
    {
        using var input = JsonDocument.Parse(inputJson);
        var (step, final) = CliProgress.Parse(ToolUse(tool, input.RootElement));

        step.ShouldBe(expected);
        final.ShouldBeNull();
    }

    [Theory]
    [InlineData("Bash", "{\"command\":\"pwd\"}")]
    [InlineData("TodoWrite", "{\"todos\":[]}")]
    public void Parse_ToolThatDoesNotMoveTheTask_HasNoStep(string tool, string inputJson)
    {
        using var input = JsonDocument.Parse(inputJson);
        CliProgress.Parse(ToolUse(tool, input.RootElement)).Step.ShouldBeNull();
    }

    [Fact]
    public void Parse_ResultEvent_ReturnsTheAnswerUsageAndCost()
    {
        const string line = """{"type":"result","subtype":"success","is_error":false,"result":"{\"branch\":\"issue-7-ai-fix\",\"summary\":\"Done.\"}","total_cost_usd":0.4213,"usage":{"input_tokens":12,"cache_read_input_tokens":3000,"output_tokens":900}}""";

        var (step, final) = CliProgress.Parse(line);

        step.ShouldBeNull();
        final.ShouldNotBeNull();
        final!.IsError.ShouldBeFalse();
        final.Text.ShouldBe("{\"branch\":\"issue-7-ai-fix\",\"summary\":\"Done.\"}");
        final.CostUsd.ShouldBe(0.4213m);
        final.Usage!.Value.GetProperty("cache_read_input_tokens").GetInt32().ShouldBe(3000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}")]
    [InlineData("{broken")]
    public void Parse_OtherLines_AreIgnored(string line) => CliProgress.Parse(line).ShouldBe((null, null));

    // The runner and the server must agree on the keys, or the Board drops a step it doesn't know.
    [Fact]
    public void StepKeys_RunnerAndServer_AreIdentical() =>
        CliProgress.Steps.Select(s => s.Key).ShouldBe(TaskSteps.All.Select(s => s.Key));
}
