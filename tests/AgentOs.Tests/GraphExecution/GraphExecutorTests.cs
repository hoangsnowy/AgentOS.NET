// Proves the visual graph actually compiles into a Microsoft Agent Framework Workflow and runs on the MAF
// in-process runtime: nodes fire in edge order, MAF's event stream drives per-node Running/Done status, and
// the QA gate's conditional fail-edge loops until the iteration cap. Agents are mocked (no LLM keys).

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Pipeline.Graph;
using AgentOs.Domain.Testing;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.GraphExecution;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.GraphExecution;

public sealed class GraphExecutorTests
{
    private static PlanNode N(string id, string type, string? role = null, bool start = false, int maxIter = 0,
        string desc = "", string input = "", string output = "")
        => new(id, type, role, id, maxIter, start, desc, input, output);

    private static PlanEdge E(string src, string tgt, string label = "")
        => new($"{src}->{tgt}", src, tgt, label);

    [Fact]
    public async Task RunAsync_LinearRequirementToEnd_CompletesAndStreamsNodeStatus()
    {
        var (exec, agents) = Build();
        var graph = new PlanGraph("g", "lin",
            [N("req", "Agent", "Requirement", start: true), N("end", "End")],
            [E("req", "end")]);

        var events = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, "build a thing", nMax: 3, "tenant-1", null, e => { events.Add(e); return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        result.FailureMessage.ShouldBeNull();
        await agents.Requirement.Received(1).RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>());
        // MAF's ExecutorInvoked/Completed events surfaced the requirement node going Running then Done.
        events.ShouldContain(e => e.NodeId == "req" && e.Phase == GraphNodePhase.Running);
        events.ShouldContain(e => e.NodeId == "req" && e.Phase == GraphNodePhase.Done);
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_BlocksBeforeAnyAgentRuns()
    {
        var guard = Substitute.For<AgentOs.Domain.Cost.IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new AgentOs.Domain.Cost.BudgetStatus(
                 CapUsd: 10m, SpentUsd: 25m, RemainingUsd: -15m, Percent: 2.5,
                 State: AgentOs.Domain.Cost.BudgetState.Exceeded, EnforceOn: true));
        var (exec, agents) = Build(qaConsistent: true, budget: guard);
        var graph = new PlanGraph("g", "lin",
            [N("req", "Agent", "Requirement", start: true), N("end", "End")],
            [E("req", "end")]);

        var result = await exec.RunAsync(graph, "build a thing", nMax: 3, "tenant-over-cap", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("Budget exceeded");
        // The gate ran BEFORE any agent — no LLM spend incurred.
        await agents.Requirement.DidNotReceive().RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_BudgetWithinCap_RunsNormally()
    {
        var guard = Substitute.For<AgentOs.Domain.Cost.IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new AgentOs.Domain.Cost.BudgetStatus(
                 CapUsd: 100m, SpentUsd: 5m, RemainingUsd: 95m, Percent: 0.05,
                 State: AgentOs.Domain.Cost.BudgetState.Ok, EnforceOn: true));
        var (exec, agents) = Build(qaConsistent: true, budget: guard);
        var graph = new PlanGraph("g", "lin",
            [N("req", "Agent", "Requirement", start: true), N("end", "End")],
            [E("req", "end")]);

        var result = await exec.RunAsync(graph, "build a thing", nMax: 3, "tenant-ok", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        await agents.Requirement.Received(1).RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_QaPassFirstIteration_RunsCodingOnce()
    {
        var (exec, agents) = Build(qaConsistent: true);

        var result = await exec.RunAsync(Sdlc(), "story", nMax: 3, "t", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        await agents.Coding.Received(1).RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_QaInconsistent_LoopsCodingUntilIterationCap()
    {
        var (exec, agents) = Build(qaConsistent: false);

        // qa cap = 2 (the Evaluator node's MaxIterations) → coding runs on iterations 1 and 2, then the
        // forward edge fires because the cap is exhausted, and the workflow terminates at End.
        var result = await exec.RunAsync(Sdlc(qaMaxIter: 2), "story", nMax: 5, "t", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        await agents.Coding.Received(2).RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UnsupportedNodeOnPath_RefusesBeforeAnyAgentRuns()
    {
        var (exec, agents) = Build();
        var graph = new PlanGraph("g", "x",
            [N("req", "Agent", "Requirement", start: true), N("hook", "Webhook"), N("end", "End")],
            [E("req", "hook"), E("hook", "end")]);

        var events = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, "s", 3, "t", null, e => { events.Add(e); return Task.CompletedTask; });

        result.Completed.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("not runnable");
        events.ShouldContain(e => e.NodeId == "hook" && e.Phase == GraphNodePhase.Skipped);
        await agents.Requirement.DidNotReceive().RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ParallelFanOutThenMerge_RunsBothBranchesAndJoins()
    {
        var (exec, _) = Build(llmReply: "ok", yieldLlm: true);   // yield → branches truly run in parallel
        // s -> P (fan-out) -> a, b ; a, b -> M (fan-in barrier) -> end
        var graph = new PlanGraph("g", "par",
            [
                N("s", "Llm", start: true), N("p", "Parallel"),
                N("a", "Llm"), N("b", "Llm"), N("m", "Merge"), N("end", "End"),
            ],
            [E("s", "p"), E("p", "a"), E("p", "b"), E("a", "m"), E("b", "m"), E("m", "end")]);

        var done = new List<string>();
        var running = new List<string>();
        var result = await exec.RunAsync(graph, "story", 3, "t", null, e =>
        {
            if (e.Phase == GraphNodePhase.Done) { done.Add(e.NodeId); }
            if (e.Phase == GraphNodePhase.Running) { running.Add(e.NodeId); }
            return Task.CompletedTask;
        });

        result.Completed.ShouldBeTrue();
        done.ShouldContain("a");
        done.ShouldContain("b");
        done.ShouldContain("m");
        done.ShouldContain("end");
        // The fan-in collapses both branches into ONE downstream emission — end must run exactly once, not
        // once per branch (regression guard for the merge double-fire).
        running.Count(n => n == "end").ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_IfElse_TakesOnlyTheChosenRoute()
    {
        // The (stubbed) router LLM answers "b" → only the b-branch runs.
        var (exec, _) = Build(llmReply: "b");
        var graph = new PlanGraph("g", "if",
            [N("gate", "IfElse", start: true), N("na", "Llm"), N("nb", "Llm")],
            [E("gate", "na", "a"), E("gate", "nb", "b")]);

        var running = new List<string>();
        var result = await exec.RunAsync(graph, "pick", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Running) { running.Add(e.NodeId); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        running.ShouldContain("nb");
        running.ShouldNotContain("na");
    }

    [Fact]
    public async Task RunAsync_IfElse_WhitespacePaddedLabel_StillRoutesToChosenBranch()
    {
        // The edge label " b " carries stray whitespace and the router LLM replies "b". The chosen route must
        // still match the padded label (both sides are trimmed). Before the fix the asymmetric trim (reply
        // trimmed, route not) failed to match and silently fired the first/default branch instead.
        var (exec, _) = Build(llmReply: "b");
        var graph = new PlanGraph("g", "if",
            [N("gate", "IfElse", start: true), N("na", "Llm"), N("nb", "Llm")],
            [E("gate", "na", "a"), E("gate", "nb", " b ")]);

        var running = new List<string>();
        var result = await exec.RunAsync(graph, "pick", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Running) { running.Add(e.NodeId); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        running.ShouldContain("nb");
        running.ShouldNotContain("na");
    }

    [Fact]
    public async Task RunAsync_Loop_RepeatsBodyUntilCapThenExits()
    {
        var (exec, _) = Build(llmReply: "x");
        // s -> L ; L --loop--> B -> L ; L --(cap)--> end. cap = 3 ⇒ body runs twice (passes 1,2), exits on 3.
        var graph = new PlanGraph("g", "loop",
            [N("s", "Llm", start: true), N("l", "Loop", maxIter: 3), N("b", "Llm"), N("end", "End")],
            [E("s", "l"), E("l", "b", "loop"), E("b", "l"), E("l", "end")]);

        var bRuns = 0;
        var result = await exec.RunAsync(graph, "go", 5, "t", null,
            e => { if (e.NodeId == "b" && e.Phase == GraphNodePhase.Running) { bRuns++; } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        bRuns.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_HumanNode_FailsClosedWhenNoOperatorAttached()
    {
        // E0.4: a checkpoint nobody can approve must stop the run, never auto-approve.
        var (exec, _) = Build(llmReply: "ok");
        var graph = new PlanGraph("g", "human",
            [N("s", "Llm", start: true), N("h", "Human"), N("end", "End")],
            [E("s", "h"), E("h", "end")]);

        var done = new List<string>();
        var result = await exec.RunAsync(graph, "go", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Done) { done.Add(e.NodeId); } return Task.CompletedTask; });

        result.Completed.ShouldBeFalse();
        result.FailureMessage.ShouldNotBeNull().ShouldContain("needs an approver");
        done.ShouldNotContain("end");
    }

    [Fact]
    public async Task RunAsync_IfElse_SubstringRoute_PicksWholeWordNotPrefix()
    {
        // routes ["no","now"]; the LLM replies "now" — must pick "now", not the earlier substring "no".
        var (exec, _) = Build(llmReply: "now");
        var graph = new PlanGraph("g", "if",
            [N("gate", "IfElse", start: true), N("na", "Llm"), N("nb", "Llm")],
            [E("gate", "na", "no"), E("gate", "nb", "now")]);

        var running = new List<string>();
        var result = await exec.RunAsync(graph, "pick", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Running) { running.Add(e.NodeId); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        running.ShouldContain("nb");
        running.ShouldNotContain("na");
    }

    [Fact]
    public async Task RunAsync_EvaluatorLoopsBackOverNonCodingPath_StillTerminatesAtCap()
    {
        // The QA gate loops back to Testing (NOT Coding), so the global coding counter never advances. The
        // per-gate counter must still bound the loop — otherwise it spins forever (regression for the
        // shared-Iteration bug). cap=2 ⇒ the gate runs twice then exits.
        var (exec, agents) = Build(qaConsistent: false);
        var graph = new PlanGraph("g", "gate",
            [
                N("req", "Agent", "Requirement", start: true), N("cod", "Agent", "Coding"),
                N("tst", "Agent", "Testing"), N("qa", "Evaluator", maxIter: 2), N("end", "End"),
            ],
            [E("req", "cod"), E("cod", "tst"), E("tst", "qa"), E("qa", "tst", "fail"), E("qa", "end", "pass")]);

        var result = await exec.RunAsync(graph, "s", nMax: 5, "t", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        await agents.Coding.Received(1).RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>());
        await agents.Testing.Received(2).RunAsync(
            Arg.Any<RequirementSpec>(), Arg.Any<CodeArtifact>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_QaBeforeCoding_FailsWithClearMessage()
    {
        var (exec, _) = Build();
        var graph = new PlanGraph("g", "x",
            [N("req", "Agent", "Requirement", start: true), N("qa", "Evaluator"), N("end", "End")],
            [E("req", "qa"), E("qa", "end")]);

        var result = await exec.RunAsync(graph, "s", 3, "t", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("Coding");
    }

    [Fact]
    public async Task RunAsync_RouterIntoBarrierMerge_SkippedBranch_DoesNotReportFalseSuccess()
    {
        // An If/Else picks ONE branch, but both branches feed a Merge (fan-in barrier needs BOTH). The skipped
        // branch never delivers, so the merge + End never run. The run must NOT report success.
        var (exec, _) = Build(llmReply: "a");
        var graph = new PlanGraph("g", "x",
            [
                N("gate", "IfElse", start: true), N("na", "Llm"), N("nb", "Llm"),
                N("m", "Merge"), N("end", "End"),
            ],
            [E("gate", "na", "a"), E("gate", "nb", "b"), E("na", "m"), E("nb", "m"), E("m", "end")]);

        var result = await exec.RunAsync(graph, "s", 3, "t", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeFalse();
        result.FailureMessage!.ShouldContain("End");
    }

    [Fact]
    public async Task RunAsync_HumanNode_OperatorRejection_StopsTheRun()
    {
        var (exec, _) = Build(llmReply: "ok");
        var graph = new PlanGraph("g", "human",
            [N("s", "Llm", start: true), N("h", "Human"), N("end", "End")],
            [E("s", "h"), E("h", "end")]);

        var events = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, "go", 3, "t", null,
            e => { events.Add(e); return Task.CompletedTask; },
            onHuman: _ => Task.FromResult(new GraphHumanReply(false, "not now")));

        result.Completed.ShouldBeFalse();
        events.ShouldContain(e => e.NodeId == "h" && e.Phase == GraphNodePhase.Failed);
        events.ShouldNotContain(e => e.NodeId == "end" && e.Phase == GraphNodePhase.Done);
    }

    // ---- leaf-node coverage: Tool / Switch / Transform / ExtractJson (the audit's untested execution paths) ----

    [Fact]
    public async Task RunAsync_ToolNode_InvokesToolThroughGatewayAndReportsOk()
    {
        // A Tool node parses its {"tool","input"} spec, resolves the tool from the registry, and invokes it
        // THROUGH the governed gateway (policy + evidence) — never the tool directly. Proves that whole seam.
        var tool = Substitute.For<ITool>();
        var registry = Substitute.For<IToolRegistry>();
        registry.Resolve("echo").Returns(tool);
        ToolInvocationRequest? seen = null;
        var gateway = Substitute.For<IToolGateway>();
        gateway.InvokeAsync(Arg.Any<ITool>(), Arg.Any<ToolInvocationRequest>(), Arg.Any<CancellationToken>())
               .Returns(ci => { seen = ci.Arg<ToolInvocationRequest>(); return Task.FromResult(new ToolGatewayResult("echoed: hi", IsError: false)); });
        var (exec, _) = Build(toolRegistry: registry, toolGateway: gateway);

        var graph = new PlanGraph("g", "tool",
            [
                N("t", "Tool", start: true, desc: """{"tool":"echo","input":{"msg":"hi"}}""", output: "r"),
                N("end", "End"),
            ],
            [E("t", "end")]);

        var done = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, "go", 3, "acme", null,
            e => { if (e.Phase == GraphNodePhase.Done) { done.Add(e); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        registry.Received(1).Resolve("echo");
        await gateway.Received(1).InvokeAsync(tool, Arg.Any<ToolInvocationRequest>(), Arg.Any<CancellationToken>());
        // The parsed tool name + the run's explicit tenant reach the gateway request.
        seen.ShouldNotBeNull();
        seen.ToolName.ShouldBe("echo");
        seen.TenantId.ShouldBe("acme");
        // A non-denied, non-error invocation reports "ok" on the node.
        done.ShouldContain(e => e.NodeId == "t" && e.Meta == "ok");
    }

    [Fact]
    public async Task RunAsync_ToolNode_GetsTheFullOutput_NeverAModelShapedCopy()
    {
        // 2.7: tool-output shaping is for the MODEL. A workflow Tool node feeds its result to ${…} / ExtractJson as
        // DATA, so the gateway the host registers (ToolsModule) must hand it the full result — here a JSON payload
        // far over the default 16 000-char shaping budget, piped into a second tool node through ${big}.
        var services = new ServiceCollection();
        new AgentOs.Modules.Tools.ToolsModule().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IToolGateway>();

        var bigJson = "[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":{i},\"name\":\"file{i}.cs\"}}")) + "]";
        bigJson.Length.ShouldBeGreaterThan(ToolOutputShaper.DefaultMaxOutputTokens * ToolOutputShaper.CharsPerToken);
        var source = new RecordingTool("big", bigJson);
        var sink = new RecordingTool("sink", "done");
        var registry = Substitute.For<IToolRegistry>();
        registry.Resolve("big").Returns(source);
        registry.Resolve("sink").Returns(sink);
        var (exec, _) = Build(toolRegistry: registry, toolGateway: gateway);

        var graph = new PlanGraph("g", "tool-data",
            [
                N("t", "Tool", start: true, desc: """{"tool":"big","input":{}}""", output: "big"),
                N("s", "Tool", desc: """{"tool":"sink","input":{"rows":"${big}"}}"""),
                N("end", "End"),
            ],
            [E("t", "s"), E("s", "end")]);

        var result = await exec.RunAsync(graph, "go", 3, "acme", null, _ => Task.CompletedTask);

        result.FailureMessage.ShouldBeNull();
        result.Completed.ShouldBeTrue();
        // Interpolation is textual: the full payload lands verbatim inside the second node's input.
        sink.LastInput.ShouldBe("{\"rows\":\"" + bigJson + "\"}");
    }

    // A hand-written tool (no substitute on the Task member) that records the input it was given.
    private sealed class RecordingTool(string name, string output) : ITool
    {
        public string? LastInput { get; private set; }

        public ToolDefinition Definition { get; } = new(name, "records its input", """{"type":"object"}""");

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default)
        {
            LastInput = request.Input;
            return Task.FromResult(ToolInvocationResult.Success(request.CallId, output));
        }
    }

    [Fact]
    public async Task RunAsync_Switch_RoutesToTheBranchMatchingTheChosenValue()
    {
        // Switch shares WireRouter/RunDecision with IfElse: the (stubbed) router LLM answers "beta" → only the
        // beta-labelled branch runs. Direct coverage of the Switch step type (previously only IfElse was tested).
        var (exec, _) = Build(llmReply: "beta");
        var graph = new PlanGraph("g", "switch",
            [N("sw", "Switch", start: true), N("na", "Llm"), N("nb", "Llm")],
            [E("sw", "na", "alpha"), E("sw", "nb", "beta")]);

        var running = new List<string>();
        var result = await exec.RunAsync(graph, "pick", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Running) { running.Add(e.NodeId); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        running.ShouldContain("nb");
        running.ShouldNotContain("na");
    }

    [Fact]
    public async Task RunAsync_TransformNode_InterpolatesRunStateIntoOutput()
    {
        // A Transform node interpolates ${…} refs against run state — here ${userstory} → the run's story text.
        // No LLM / agent runs; the node's meta carries the interpolated result.
        var (exec, _) = Build();
        var graph = new PlanGraph("g", "xform",
            [N("t", "Transform", start: true, desc: "hello ${userstory}", output: "greeting"), N("end", "End")],
            [E("t", "end")]);

        var done = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, "world", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Done) { done.Add(e); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        done.ShouldContain(e => e.NodeId == "t" && e.Meta == "hello world");
    }

    [Fact]
    public async Task RunAsync_ExtractJsonNode_StripsProseAndEmitsTheJsonObject()
    {
        // ExtractJson pulls the JSON object out of surrounding prose and writes it to state; the downstream
        // Print node echoes that cleaned value — proving the extract wrote the object, not the raw text.
        var (exec, _) = Build();
        var graph = new PlanGraph("g", "xjson",
            [
                N("x", "ExtractJson", start: true, input: "userstory", output: "j"),
                N("p", "Print", input: "j"),
                N("end", "End"),
            ],
            [E("x", "p"), E("p", "end")]);

        var done = new List<GraphNodeEvent>();
        var result = await exec.RunAsync(graph, """noise {"ok":true} tail""", 3, "t", null,
            e => { if (e.Phase == GraphNodePhase.Done) { done.Add(e); } return Task.CompletedTask; });

        result.Completed.ShouldBeTrue();
        // ShouldNotBeNull returns the non-null value, so printMeta is a non-nullable string —
        // this also clears CodeQL cs/dereferenced-value-may-be-null on the following asserts.
        var printMeta = done.Find(e => e.NodeId == "p").ShouldNotBeNull().Meta.ShouldNotBeNull();
        printMeta.ShouldContain("ok");
        printMeta.ShouldContain("true");
        printMeta.ShouldNotContain("noise");
    }

    // ---- spend metering (follow-up to PR #98: the graph must persist its own spend so the gate works) ----

    [Fact]
    public async Task RunAsync_PersistsPerNodeSpend_StampingTheExplicitTenant()
    {
        PipelineRunRecord? saved = null;
        string? savedTenant = null;
        var repo = Substitute.For<IPipelineRunRepository>();
        repo.When(r => r.SaveAsync(Arg.Any<PipelineRunRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => { saved = ci.Arg<PipelineRunRecord>(); savedTenant = ci.Arg<string>(); });
        var (exec, _) = Build(qaConsistent: true, repo: repo);

        var result = await exec.RunAsync(Sdlc(), "story", nMax: 3, "acme", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        // The crux: spend is persisted stamping the EXPLICIT tenant passed to RunAsync, not the (blank)
        // ambient ITenantContext of a Blazor circuit — otherwise the budget gate would never see it.
        savedTenant.ShouldBe("acme");
        saved.ShouldNotBeNull();
        // One run_metrics row per billable agent node, tagged with the matching KC id.
        saved.Metrics.Select(m => m.KcId).ShouldBe(["KC1", "KC2", "KC3", "KC5"], ignoreOrder: true);
        saved.Metrics.Select(m => m.AgentName).ShouldContain("CodingAgent");
        // Billed cost == the sum of the four agent nodes' stub metrics (0.0001 + 0.0002 + 0.00015 + 0.0001).
        saved.Metrics.Sum(m => m.CostUsd).ShouldBe(0.00055m);
        saved.Result.TotalMetrics.CostUsd.ShouldBe(0.00055m);
    }

    [Fact]
    public async Task RunAsync_RawLlmNodeSpend_IsPersisted()
    {
        PipelineRunRecord? saved = null;
        var repo = Substitute.For<IPipelineRunRepository>();
        repo.When(r => r.SaveAsync(Arg.Any<PipelineRunRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => saved = ci.Arg<PipelineRunRecord>());
        var (exec, _) = Build(llmReply: "ok", repo: repo, llmCost: 0.0009m);
        var graph = new PlanGraph("g", "llm",
            [N("s", "Llm", start: true), N("end", "End")],
            [E("s", "end")]);

        var result = await exec.RunAsync(graph, "go", 3, "acme", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        saved.ShouldNotBeNull();
        // A raw-LLM node calls the gateway directly (not via a typed agent); its tokens/cost must still be
        // metered or workflow LLM spend would be invisible to the budget gate.
        var llmMetric = saved.Metrics.ShouldHaveSingleItem();
        llmMetric.KcId.ShouldBe("graph");
        llmMetric.CostUsd.ShouldBe(0.0009m);
        llmMetric.TokensIn.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_BudgetBlocked_PersistsNothing()
    {
        var guard = Substitute.For<AgentOs.Domain.Cost.IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new AgentOs.Domain.Cost.BudgetStatus(
                 CapUsd: 10m, SpentUsd: 25m, RemainingUsd: -15m, Percent: 2.5,
                 State: AgentOs.Domain.Cost.BudgetState.Exceeded, EnforceOn: true));
        var repo = Substitute.For<IPipelineRunRepository>();
        var (exec, _) = Build(qaConsistent: true, budget: guard, repo: repo);

        var result = await exec.RunAsync(Sdlc(), "story", 3, "over-cap", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeFalse();
        // Blocked before any node ran → no spend incurred → nothing to persist.
        await repo.DidNotReceive().SaveAsync(
            Arg.Any<PipelineRunRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoBillableNodes_DoesNotPersist()
    {
        var repo = Substitute.For<IPipelineRunRepository>();
        var (exec, _) = Build(repo: repo);
        // Print echoes its input; no agent / LLM runs → zero spend → no run row.
        var graph = new PlanGraph("g", "print",
            [N("p", "Print", start: true), N("end", "End")],
            [E("p", "end")]);

        var result = await exec.RunAsync(graph, "hello", 3, "acme", null, _ => Task.CompletedTask);

        result.Completed.ShouldBeTrue();
        await repo.DidNotReceive().SaveAsync(
            Arg.Any<PipelineRunRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // req(Requirement,start) -> cod(Coding) -> tst(Testing) -> qa(Evaluator) -> end; qa loops to cod on fail.
    private static PlanGraph Sdlc(int qaMaxIter = 3)
        => new("g1", "SDLC",
            [
                N("req", "Agent", "Requirement", start: true),
                N("cod", "Agent", "Coding"),
                N("tst", "Agent", "Testing"),
                N("qa", "Evaluator", maxIter: qaMaxIter),
                N("end", "End"),
            ],
            [
                E("req", "cod"),
                E("cod", "tst"),
                E("tst", "qa"),
                E("qa", "end", "pass"),
                E("qa", "cod", "fail · regenerate"),
            ]);

    private sealed record Agents(IRequirementAgent Requirement, ICodingAgent Coding, ITestingAgent Testing, IQaAgent Qa);

    private static (GraphExecutor Exec, Agents Agents) Build(
        bool qaConsistent = true, string? llmReply = null, bool yieldLlm = false,
        AgentOs.Domain.Cost.IBudgetGuard? budget = null,
        IPipelineRunRepository? repo = null, TimeProvider? clock = null, decimal llmCost = 0m,
        IToolRegistry? toolRegistry = null, IToolGateway? toolGateway = null)
    {
        var req = Substitute.For<IRequirementAgent>();
        req.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(StubSpec());

        var coding = Substitute.For<ICodingAgent>();
        coding.RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>()).Returns(StubCode());

        var testing = Substitute.For<ITestingAgent>();
        testing.RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<CodeArtifact>(), Arg.Any<QaReport?>(), Arg.Any<CancellationToken>())
               .Returns(StubTests());

        var qa = Substitute.For<IQaAgent>();
        qa.RunAsync(Arg.Any<RequirementSpec>(), Arg.Any<CodeArtifact>(), Arg.Any<TestArtifact>(), Arg.Any<CancellationToken>())
          .Returns(StubQa(qaConsistent));

        // Raw-LLM / decision nodes resolve a client from the factory. When llmReply is given, every SendAsync
        // returns it (lets a router test steer the chosen route); otherwise the factory is bare.
        var factory = Substitute.For<ILlmClientFactory>();
        if (llmReply is not null)
        {
            var llm = Substitute.For<ILlmClient>();
            var resp = new LlmResponse(llmReply, 1, 1, llmCost, System.TimeSpan.Zero, "m", "Offline");
            if (yieldLlm)
            {
                // Force a real async suspension on the threadpool so fan-out branches genuinely run in
                // parallel — exercises the concurrent-state-access path (ConcurrentDictionary/interlocked).
                llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
                   .Returns(_ => Task.Run(async () => { await Task.Delay(5); return resp; }));
            }
            else
            {
                llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(resp);
            }
            factory.Create(Arg.Any<string>()).Returns(llm);
        }

        var exec = new GraphExecutor(
            req, coding, testing, qa,
            factory,
            toolRegistry ?? Substitute.For<IToolRegistry>(),
            toolGateway ?? Substitute.For<IToolGateway>(),
            Options.Create(new AgentsOptions()),
            NullLogger<GraphExecutor>.Instance,
            budget,
            repo,
            clock);

        return (exec, new Agents(req, coding, testing, qa));
    }

    private static RequirementSpec StubSpec() => new(
        Title: "T", Summary: "S", Stakeholders: [], FunctionalRequirements: [], NonFunctionalRequirements: [],
        Entities: [new EntityDescriptor("E", [])], Endpoints: [new EndpointDescriptor("GET", "/", "root")],
        AcceptanceCriteria: ["a"], Metrics: new AgentMetrics("Test", "m", 10, 5, 0.0001m, System.TimeSpan.FromMilliseconds(50)));

    private static CodeArtifact StubCode() => new(
        ProjectName: "P", Architecture: "Clean Architecture",
        Files: [new CodeFile("src/E.cs", "namespace P;")], Notes: null,
        Metrics: new AgentMetrics("Test", "m", 20, 10, 0.0002m, System.TimeSpan.FromMilliseconds(80)));

    private static TestArtifact StubTests() => new(
        Framework: "xUnit", Files: [new CodeFile("tests/ETests.cs", "namespace T;")],
        HappyPathCount: 1, EdgeCaseCount: 1, ErrorCaseCount: 1, EstimatedCoveragePercent: 60,
        Metrics: new AgentMetrics("Test", "m", 15, 8, 0.00015m, System.TimeSpan.FromMilliseconds(70)));

    private static QaReport StubQa(bool isConsistent) => new(
        Score: isConsistent ? 0.9 : 0.6, IsConsistent: isConsistent, IterationNeeded: !isConsistent,
        Issues: isConsistent ? [] : [new QaIssue("Major", "TestCoverage", "missing edge")],
        Recommendations: isConsistent ? [] : ["add edge test"],
        Metrics: new AgentMetrics("Test", "m", 12, 6, 0.0001m, System.TimeSpan.FromMilliseconds(40)));
}
