// The model an agent's profile picked reaches the paired runner's CLI: `--model <id>` for Claude Code, `-m <id>` for
// Codex; nothing when the request carries no model, the CLI cannot serve it, or the operator's own args name one.

using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class CliModelTests
{
    private static readonly string[] ClaudeDefaults = ["-p"];
    private static readonly string[] CodexDefaults = ["exec"];

    [Theory]
    [InlineData("claude", "claude-sonnet-4-6")]
    [InlineData("claude", "claude-opus-5-5")]
    [InlineData("claude", "sonnet")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", "claude-haiku-5-5")]
    [InlineData("/usr/local/bin/claude", "claude-fable-1")]
    public void Claude_GetsDashDashModel(string command, string model) =>
        CliModel.Flags(command, model, ClaudeDefaults).ShouldBe(["--model", model]);

    [Theory]
    [InlineData("codex", "gpt-5.1-codex")]
    [InlineData(@"C:\npm\codex.cmd", "o4-mini")]
    public void Codex_GetsDashM(string command, string model) =>
        CliModel.Flags(command, model, CodexDefaults).ShouldBe(["-m", model]);

    [Theory]
    [InlineData("claude", "")]
    [InlineData("claude", "   ")]
    [InlineData("claude", null)]
    [InlineData("codex", "claude-sonnet-4-6")]        // a model codex cannot serve
    [InlineData("my-agent", "claude-sonnet-4-6")]     // an unknown CLI: never guess its flags
    [InlineData("claude", "--dangerously-skip-permissions")]   // never let a model id become a flag
    [InlineData("claude", "claude sonnet")]
    public void NoFlag_WhenThereIsNothingTheCliCanUse(string command, string? model) =>
        CliModel.Flags(command, model, ClaudeDefaults).ShouldBeEmpty();

    // A non-Claude profile model on claude runs as the Claude fallback, never silently as the CLI default (a slow
    // thinking model ran Workflow Coding steps past the answer deadline on the lab, 2026-11-04).
    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4o-mini")]
    public void Claude_NonClaudeModel_RunsAsTheClaudeFallback(string model)
    {
        CliModel.Flags("claude", model, ClaudeDefaults).ShouldBe(["--model", CliModel.DefaultClaudeFallback]);
        CliModel.Flags("claude", model, ClaudeDefaults, "claude-haiku-4-5").ShouldBe(["--model", "claude-haiku-4-5"]);
        CliModel.Flags("claude", model, ClaudeDefaults, "").ShouldBeEmpty();                        // empty = the CLI default
        CliModel.Flags("claude", model, ClaudeDefaults, "--yolo").ShouldBeEmpty();                  // never a flag
        CliModel.Flags("claude", model, ["-p", "--model", "opus"], "sonnet").ShouldBeEmpty();      // operator args win
    }

    [Theory]
    [InlineData("--model", "opus")]
    [InlineData("-m", "x")]
    [InlineData("--model=opus")]
    public void OperatorArgs_ThatNameAModel_Win(params string[] existing) =>
        CliModel.Flags("claude", "claude-sonnet-4-6", ["-p", .. existing]).ShouldBeEmpty();

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData(@"C:\npm\Claude.CMD", "claude")]
    [InlineData("/opt/codex", "codex")]
    [InlineData("", "")]
    public void CommandName_IsTheBareCliName_OnEveryOs(string command, string expected) =>
        CliModel.CommandName(command).ShouldBe(expected);
}
