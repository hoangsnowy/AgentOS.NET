// Epic E2 — Verifies the ITool -> AIFunction adapter that bridges the Tools subsystem into
// Microsoft.Extensions.AI's function-invocation pipeline.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Llm;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class AIToolFunctionTests
{
    [Fact]
    public void Adapter_ExposesDefinitionAsAIFunctionSurface()
    {
        var tool = FakeTool("echo", """{"type":"object","properties":{"x":{"type":"string"}}}""");

        var fn = new AIToolFunction(tool, "tenant-1", runId: "run-7");

        fn.Name.ShouldBe("echo");
        fn.Description.ShouldBe("Fake tool echo");
        fn.JsonSchema.GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public async Task Adapter_Invoke_PassesSerializedArgumentsToTool()
    {
        var tool = Substitute.For<ITool>();
        tool.Definition.Returns(new ToolDefinition("echo", "desc", """{"type":"object"}"""));
        tool.InvokeAsync(Arg.Any<ToolInvocationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ToolInvocationResult.Success(
                ci.Arg<ToolInvocationRequest>()!.CallId, "ok")));

        var fn = new AIToolFunction(tool, "tenant-9", runId: "run-3");
        var result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["x"] = "hello" }),
            CancellationToken.None);

        result?.ToString().ShouldBe("ok");
        await tool.Received(1).InvokeAsync(
            Arg.Is<ToolInvocationRequest>(r =>
                r!.ToolName == "echo" &&
                r.TenantId == "tenant-9" &&
                r.RunId == "run-3" &&
                r.Input.Contains("\"x\":\"hello\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adapter_Invoke_ToolErrorSurfacedAsResultMessage()
    {
        var tool = Substitute.For<ITool>();
        tool.Definition.Returns(new ToolDefinition("echo", "desc", """{"type":"object"}"""));
        tool.InvokeAsync(Arg.Any<ToolInvocationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ToolInvocationResult.Error(
                ci.Arg<ToolInvocationRequest>()!.CallId, "boom")));

        var fn = new AIToolFunction(tool, "tenant-1", runId: null);
        var result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            CancellationToken.None);

        result?.ToString().ShouldBe("boom");
    }

    [Fact]
    public async Task Adapter_Invoke_HandsTheModelTheShapedOutput_OverTheBudget()
    {
        // 2.7: the adapter is the model-facing path, so a result over Tools:MaxOutputTokens reaches the model capped.
        var big = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"row {i:D5}"));
        var tool = Substitute.For<ITool>();
        tool.Definition.Returns(new ToolDefinition("dump", "desc", """{"type":"object"}"""));
        tool.InvokeAsync(Arg.Any<ToolInvocationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ToolInvocationResult.Success(ci.Arg<ToolInvocationRequest>()!.CallId, big)));

        var fn = new AIToolFunction(tool, "tenant-1", runId: null);
        // ShouldNotBeNull() returns the value, so CodeQL's null analysis sees a non-null local (cs/dereferenced-value-may-be-null).
        var raw = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);
        var result = raw.ShouldNotBeNull().ToString().ShouldNotBeNull();

        result.Length.ShouldBeLessThanOrEqualTo(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
        result.ShouldStartWith("row 00000");
        result.ShouldEndWith("row 19999");
        result.ShouldContain(" lines omitted …]");
    }

    [Fact]
    public void Adapter_NullTool_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new AIToolFunction(null!, "t", runId: null));
    }

    private static ITool FakeTool(string name, string schema)
    {
        var tool = Substitute.For<ITool>();
        tool.Definition.Returns(new ToolDefinition(name, $"Fake tool {name}", schema));
        return tool;
    }
}
