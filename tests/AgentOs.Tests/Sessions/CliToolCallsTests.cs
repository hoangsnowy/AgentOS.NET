// Tool-call evidence read off `claude -p --output-format stream-json --verbose` (event shapes as a live run prints
// them): tool_use → tool_result (ok / error / refused by the CLI's permissions) → the closing result's
// permission_denials. Only a bounded, redacted summary of each call is kept — never file contents or output.

using System;
using System.Linq;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class CliToolCallsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    // Real-shaped lines (ids, nesting, extra fields as the CLI prints them).
    private const string BashUse = """{"type":"assistant","message":{"id":"msg_01Q","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"Cloning the repository."},{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{"command":"git clone https://github.com/o/r.git .","description":"Clone the repository"}}],"stop_reason":null,"usage":{"input_tokens":4,"output_tokens":90}},"parent_tool_use_id":null,"session_id":"5f3c"}""";
    private const string BashOk = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01A","type":"tool_result","content":"Cloning into '.'...","is_error":false}]},"parent_tool_use_id":null,"session_id":"5f3c","tool_use_result":{"stdout":"Cloning into '.'...","stderr":"","interrupted":false,"isImage":false}}""";
    private const string WriteUse = """{"type":"assistant","message":{"id":"msg_02","role":"assistant","content":[{"type":"tool_use","id":"toolu_02B","name":"Write","input":{"file_path":"/runs/abc/src/Login.cs","content":"public class Login { const string Secret = \"TOP-SECRET-CONTENT\"; }"}}]},"parent_tool_use_id":null,"session_id":"5f3c"}""";
    private const string WriteErr = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"<tool_use_error>File has not been read yet. Read it first before writing to it.</tool_use_error>","is_error":true,"tool_use_id":"toolu_02B"}]},"session_id":"5f3c"}""";
    private const string PushUse = """{"type":"assistant","message":{"id":"msg_03","role":"assistant","content":[{"type":"tool_use","id":"toolu_03C","name":"Bash","input":{"command":"git push --force origin main","description":"Push"}}]},"session_id":"5f3c"}""";
    private const string PushRefused = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Claude requested permissions to use Bash, but you haven't granted it yet.","is_error":true,"tool_use_id":"toolu_03C"}]},"session_id":"5f3c"}""";
    private const string DenyRuleUse = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_04D","name":"Read","input":{"file_path":"/home/me/.ssh/id_ed25519"}}]}}""";
    private const string DenyRuleRefused = """{"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"Permission to use Read has been denied."}],"is_error":true,"tool_use_id":"toolu_04D"}]}}""";
    private const string FileModeError = """{"type":"user","message":{"content":[{"type":"tool_result","content":"/bin/bash: ./build.sh: Permission denied","is_error":true,"tool_use_id":"toolu_01A"}]}}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"duration_ms":81234,"num_turns":12,"result":"{\"branch\":\"issue-7-ai-fix\"}","session_id":"5f3c","total_cost_usd":0.42,"usage":{"input_tokens":12,"output_tokens":900},"permission_denials":[{"tool_name":"Bash","tool_use_id":"toolu_03C","tool_input":{"command":"git push --force origin main"}},{"tool_name":"WebFetch","tool_use_id":"toolu_09Z","tool_input":{"url":"https://example.com/?token=abc123secret"}}]}""";

    [Fact]
    public void Read_ToolUse_CarriesIdToolAndSummary()
    {
        var e = CliProgress.Read(BashUse);

        e.Step.ShouldBe("getting-code");
        e.ToolUses.ShouldHaveSingleItem().ShouldBe(new CliProgress.ToolUse("toolu_01A", "Bash", "git clone https://github.com/o/r.git ."));
        e.Outcomes.ShouldBeEmpty();
    }

    [Fact]
    public void Read_WriteToolUse_RecordsThePathNeverTheContent()
    {
        var use = CliProgress.Read(WriteUse).ToolUses.ShouldHaveSingleItem();

        use.Summary.ShouldBe("/runs/abc/src/Login.cs");
        use.Summary.ShouldNotContain("TOP-SECRET-CONTENT");
    }

    [Fact]
    public void Read_ToolResult_OkErrorAndRefusal()
    {
        CliProgress.Read(BashOk).Outcomes.ShouldHaveSingleItem().ShouldBe(new CliProgress.ToolOutcome("toolu_01A", false, false));
        CliProgress.Read(WriteErr).Outcomes.ShouldHaveSingleItem().ShouldBe(new CliProgress.ToolOutcome("toolu_02B", true, false));
        CliProgress.Read(PushRefused).Outcomes.ShouldHaveSingleItem().ShouldBe(new CliProgress.ToolOutcome("toolu_03C", true, true));
        CliProgress.Read(DenyRuleRefused).Outcomes.ShouldHaveSingleItem().Denied.ShouldBeTrue();
    }

    [Fact]
    public void Read_AToolsOwnPermissionDeniedError_IsNotARefusal() =>
        CliProgress.Read(FileModeError).Outcomes.ShouldHaveSingleItem().Denied.ShouldBeFalse();

    [Fact]
    public void Read_ResultEvent_CarriesPermissionDenialsRedacted()
    {
        var denials = CliProgress.Read(Result).Final.ShouldNotBeNull().PermissionDenials.ShouldNotBeNull();

        denials.Count.ShouldBe(2);
        denials[0].ShouldBe(new CliProgress.ToolUse("toolu_03C", "Bash", "git push --force origin main"));
        denials[1].Tool.ShouldBe("WebFetch");
        denials[1].Summary.ShouldNotContain("abc123secret");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[\"Bash\"]}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":\"plain\"}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}")]
    [InlineData("[1,2]")]
    [InlineData("{ broken")]
    public void Read_LinesWithoutToolCalls_YieldNothing(string line)
    {
        var e = CliProgress.Read(line);

        e.ToolUses.ShouldBeEmpty();
        e.Outcomes.ShouldBeEmpty();
    }

    [Fact]
    public void Summarize_SecretInACommand_IsRedactedAndBounded()
    {
        using var input = System.Text.Json.JsonDocument.Parse("{\"command\":\"curl -H 'Authorization: Bearer abcdefghijklmnop' http://localhost:5000/" + new string('x', 900) + "\"}");

        var summary = CliProgress.Summarize("Bash", input.RootElement);

        summary.ShouldNotContain("abcdefghijklmnop");
        summary.Length.ShouldBeLessThanOrEqualTo(500);
    }

    [Fact]
    public void Summarize_UnknownTool_ListsFieldNamesOnly()
    {
        using var input = System.Text.Json.JsonDocument.Parse("{\"owner\":\"o\",\"body\":\"the whole issue text\"}");

        CliProgress.Summarize("mcp__github__create_issue", input.RootElement).ShouldBe("fields: owner, body");
    }

    [Fact]
    public void Tracker_FullRun_RecordsEveryCallWithOutcomeAndDuration()
    {
        var calls = new CliToolCalls(T0);
        calls.Observe(CliProgress.Read(BashUse), T0);
        calls.Observe(CliProgress.Read(BashOk), T0.AddMilliseconds(1500));
        calls.Observe(CliProgress.Read(WriteUse), T0.AddSeconds(2));
        calls.Observe(CliProgress.Read(WriteErr), T0.AddSeconds(2.1));
        calls.Observe(CliProgress.Read(PushUse), T0.AddSeconds(3));
        calls.Observe(CliProgress.Read(PushRefused), T0.AddSeconds(3.01));
        calls.Observe(CliProgress.Read(Result), T0.AddSeconds(4));

        var all = calls.Complete(T0.AddSeconds(4));

        all.Select(c => (c.ToolUseId, c.Tool, c.Ok, c.Denied)).ShouldBe(
        [
            ("toolu_01A", "Bash", true, false),
            ("toolu_02B", "Write", false, false),
            ("toolu_03C", "Bash", false, true),
            // Refused and reported only in the closing result (never got a tool_result line).
            ("toolu_09Z", "WebFetch", false, true),
        ]);
        all[0].DurationMs.ShouldBe(1500);
        all[0].StartedUtc.ShouldBe(T0);
        all.Count(c => c.ToolUseId == "toolu_03C").ShouldBe(1); // never counted twice
    }

    [Fact]
    public void Tracker_RefusalSeenOnlyAsAnErrorFirst_IsMarkedDeniedByTheResult()
    {
        const string vague = """{"type":"user","message":{"content":[{"type":"tool_result","content":"Error","is_error":true,"tool_use_id":"toolu_03C"}]}}""";
        var calls = new CliToolCalls(T0);
        calls.Observe(CliProgress.Read(PushUse), T0);
        calls.Observe(CliProgress.Read(vague), T0);
        calls.Observe(CliProgress.Read(Result), T0);

        calls.Complete(T0).First(c => c.ToolUseId == "toolu_03C").Denied.ShouldBeTrue();
    }

    [Fact]
    public void Tracker_RunStoppedMidCall_ClosesTheOpenCallAsAnError()
    {
        var calls = new CliToolCalls(T0);
        calls.Observe(CliProgress.Read(BashUse), T0);

        var last = calls.Complete(T0.AddSeconds(9)).ShouldHaveSingleItem();

        last.Ok.ShouldBeFalse();
        last.Denied.ShouldBeFalse();
        last.Summary.ShouldEndWith("(no result — the run ended first)");
        last.DurationMs.ShouldBe(9000);
    }

    [Fact]
    public void Tracker_Batches_BySizeAndByTime()
    {
        var calls = new CliToolCalls(T0);
        for (var i = 0; i < CliToolCalls.BatchSize - 1; i++)
        {
            Finish(calls, $"toolu_{i}", T0);
        }
        calls.ShouldFlush(T0).ShouldBeFalse();
        calls.ShouldFlush(T0 + CliToolCalls.FlushEvery).ShouldBeTrue(); // a while since the last batch

        Finish(calls, "toolu_last", T0);
        calls.ShouldFlush(T0).ShouldBeTrue(); // enough calls
        calls.Take(T0).Count.ShouldBe(CliToolCalls.BatchSize);
        calls.Pending.ShouldBe(0);
        calls.ShouldFlush(T0.AddHours(1)).ShouldBeFalse(); // nothing waiting
    }

    [Fact]
    public void Tracker_BeyondTheCap_CountsTheRestInOneRow()
    {
        var calls = new CliToolCalls(T0);
        for (var i = 0; i < CliToolCalls.MaxPerRun + 7; i++)
        {
            Finish(calls, $"toolu_{i}", T0);
            if (calls.ShouldFlush(T0)) { calls.Take(T0); }
        }

        var last = calls.Complete(T0);

        last[^1].Tool.ShouldBe("(more)");
        last[^1].Summary.ShouldStartWith("7 more tool call(s)");
    }

    private static void Finish(CliToolCalls calls, string id, DateTimeOffset at)
    {
        calls.Observe(new CliProgress.Event(null, null, [new CliProgress.ToolUse(id, "Read", "README.md")], []), at);
        calls.Observe(new CliProgress.Event(null, null, [], [new CliProgress.ToolOutcome(id, false, false)]), at);
    }
}
