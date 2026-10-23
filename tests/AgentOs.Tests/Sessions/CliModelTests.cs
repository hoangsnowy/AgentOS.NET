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
    [InlineData("claude", "gpt-5")]                  // a model claude cannot serve: its default applies
    [InlineData("codex", "claude-sonnet-4-6")]        // a model codex cannot serve
    [InlineData("my-agent", "claude-sonnet-4-6")]     // an unknown CLI: never guess its flags
    [InlineData("claude", "--dangerously-skip-permissions")]   // never let a model id become a flag
    [InlineData("claude", "claude sonnet")]
    public void NoFlag_WhenThereIsNothingTheCliCanUse(string command, string? model) =>
        CliModel.Flags(command, model, ClaudeDefaults).ShouldBeEmpty();

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
