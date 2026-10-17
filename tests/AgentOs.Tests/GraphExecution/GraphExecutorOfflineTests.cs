// Honesty guard: a node answered by the canned OfflineLlmClient (no API key + Llm:OfflineFallback) must be
// reported as offline on its Done event and on the run result, so the Workflow studio never paints canned
// output as a live, green run. Attribution is per node — including under a Parallel fan-out, where the
// branches run as concurrent Tasks and share one GraphState.

using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Pipeline.Graph;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.GraphExecution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.GraphExecution;

public sealed class GraphExecutorOfflineTests
{
    private static PlanNode N(string id, string type, string? role = null, bool start = false, string desc = "")
        => new(id, type, role, id, 0, start, desc, "", "");

    private static PlanEdge E(string src, string tgt) => new($"{src}->{tgt}", src, tgt, "");

    // Raw-LLM nodes send their description as the prompt; the stub answers "canned" prompts as the Offline
    // provider and everything else as a live provider. Yields so fan-out branches genuinely run in parallel.
    private static GraphExecutor Build(string agentProvider = "Claude")
    {
        var m = new AgentMetrics(agentProvider, "m", 10, 5, 0.0001m, System.TimeSpan.FromMilliseconds(5));
        var req = Substitute.For<IRequirementAgent>();
        req.RunAsync(Arg.Any<UserStory>(), Arg.Any<CancellationToken>()).Returns(new RequirementSpec(
            "T", "S", [], [], [], [new EntityDescriptor("E", [])], [new EndpointDescriptor("GET", "/", "root")], ["a"], m));

        var llm = Substitute.For<ILlmClient>();
        llm.SendAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(ci => Task.Run(async () =>
        {
            await Task.Delay(5);
            var canned = ci.Arg<LlmRequest>().UserPrompt.Contains("canned", System.StringComparison.Ordinal);
            return new LlmResponse("ok", 1, 1, 0m, System.TimeSpan.Zero, "m", canned ? LlmProviders.Offline : "Claude");
        }));
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<string>()).Returns(llm);

        return new GraphExecutor(
            req, Substitute.For<ICodingAgent>(), Substitute.For<ITestingAgent>(), Substitute.For<IQaAgent>(),
            factory, Substitute.For<IToolRegistry>(), Substitute.For<IToolGateway>(),
            Options.Create(new AgentsOptions()), NullLogger<GraphExecutor>.Instance);
    }

    private static async Task<(GraphRunResult Result, ConcurrentDictionary<string, bool> DoneOffline)> RunAsync(
        GraphExecutor exec, PlanGraph graph)
    {
        var done = new ConcurrentDictionary<string, bool>();
        var result = await exec.RunAsync(graph, "story", 3, "t", null, e =>
        {
            if (e.Phase == GraphNodePhase.Done) { done[e.NodeId] = e.Offline; }
            return Task.CompletedTask;
        });
        return (result, done);
    }

    [Fact]
    public async Task RunAsync_LlmNodeAnsweredByOfflineClient_FlagsNodeAndRunOffline()
    {
        var graph = new PlanGraph("g", "one",
            [N("s", "Llm", start: true, desc: "canned please"), N("end", "End")], [E("s", "end")]);

        var (result, done) = await RunAsync(Build(), graph);

        result.Completed.ShouldBeTrue();
        result.Offline.ShouldBeTrue();
        done["s"].ShouldBeTrue();
        done["end"].ShouldBeFalse();   // a node that called no model is not "offline"
    }

    [Fact]
    public async Task RunAsync_AllNodesLive_RunIsNotOffline()
    {
        var graph = new PlanGraph("g", "live",
            [N("req", "Agent", "Requirement", start: true), N("s", "Llm", desc: "live"), N("end", "End")],
            [E("req", "s"), E("s", "end")]);

        var (result, done) = await RunAsync(Build(), graph);

        result.Completed.ShouldBeTrue();
        result.Offline.ShouldBeFalse();
        done.Values.ShouldAllBe(v => !v);
    }

    [Fact]
    public async Task RunAsync_TypedAgentMetricsFromOfflineProvider_FlagsThatAgentNode()
    {
        var graph = new PlanGraph("g", "agent",
            [N("req", "Agent", "Requirement", start: true), N("end", "End")], [E("req", "end")]);

        var (result, done) = await RunAsync(Build(agentProvider: LlmProviders.Offline), graph);

        result.Offline.ShouldBeTrue();
        done["req"].ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_ParallelBranches_OfflineAttributedOnlyToTheCannedBranch()
    {
        // s -> P -> (a: live, b: canned) -> M -> end. Branches run concurrently on one shared GraphState;
        // the flag must land on b only — a shared "current node" field would mis-attribute it.
        var graph = new PlanGraph("g", "par",
            [
                N("s", "Llm", start: true, desc: "live"), N("p", "Parallel"),
                N("a", "Llm", desc: "live"), N("b", "Llm", desc: "canned"), N("m", "Merge"), N("end", "End"),
            ],
            [E("s", "p"), E("p", "a"), E("p", "b"), E("a", "m"), E("b", "m"), E("m", "end")]);

        var (result, done) = await RunAsync(Build(), graph);

        result.Completed.ShouldBeTrue();
        result.Offline.ShouldBeTrue();
        done["b"].ShouldBeTrue();
        done.Where(kv => kv.Key != "b").ShouldAllBe(kv => !kv.Value);
    }
}
