// Compiles a validated visual graph into a Microsoft Agent Framework *Workflow* and runs it on the MAF
// in-process runtime — the graph the user draws in the Workflow studio IS a MAF Workflow. One MAF Executor
// per node (id = node id), edges become MAF edges (the QA gate's pass/fail become conditional edges), and
// MAF's own ExecutorInvoked/Completed/Failed event stream drives the live canvas. Node work delegates to the
// real typed agents (Requirement/Coding/Testing/QA), the LLM gateway (Llm), and the governed tool gateway
// (Tool) — so every Tool call still passes IToolPolicy + IToolInvocationLog. Refuses to start a graph that
// isn't runnable (unsupported nodes on the path) before any executor runs.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Pipeline.Graph;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.SharedKernel.Identity;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.GraphExecution;

/// <summary>Compiles a <see cref="PlanGraph"/> into a MAF <see cref="Workflow"/> and executes it. Scoped;
/// ctor is side-effect-free (constructing the agents resolves an LLM client, so it stays out of the Web
/// component ctor — this executor is resolved lazily at run time, see <c>GraphRunnerService</c>).</summary>
public sealed class GraphExecutor : IGraphExecutor
{
    // Tenant used when the caller passes none (operator/standalone mode).
    private const string OperatorTenant = "operator";

    private readonly IRequirementAgent _requirement;
    private readonly ICodingAgent _coding;
    private readonly ITestingAgent _testing;
    private readonly IQaAgent _qa;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolGateway _toolGateway;
    private readonly AgentsOptions _agents;
    private readonly IAgentProfiles? _profiles;
    private readonly ILogger<GraphExecutor> _logger;
    // Optional: the per-tenant spend gate. Null in no-DB/standalone (nothing to meter against).
    private readonly IBudgetGuard? _budgetGuard;
    // Optional: persists the run's own per-node LLM spend to run_metrics so the budget gate actually meters
    // workflow-studio runs. Null/no-op repo in no-DB/standalone (nothing to persist to).
    private readonly IPipelineRunRepository? _runRepository;
    private readonly TimeProvider _clock;

    public GraphExecutor(
        IRequirementAgent requirement,
        ICodingAgent coding,
        ITestingAgent testing,
        IQaAgent qa,
        ILlmClientFactory llmFactory,
        IToolRegistry toolRegistry,
        IToolGateway toolGateway,
        IOptions<AgentsOptions> agents,
        ILogger<GraphExecutor> logger,
        IBudgetGuard? budgetGuard = null,
        IPipelineRunRepository? runRepository = null,
        TimeProvider? clock = null,
        IAgentProfiles? profiles = null)
    {
        _requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        _coding = coding ?? throw new ArgumentNullException(nameof(coding));
        _testing = testing ?? throw new ArgumentNullException(nameof(testing));
        _qa = qa ?? throw new ArgumentNullException(nameof(qa));
        _llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _toolGateway = toolGateway ?? throw new ArgumentNullException(nameof(toolGateway));
        ArgumentNullException.ThrowIfNull(agents);
        _agents = agents.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budgetGuard = budgetGuard;
        _runRepository = runRepository;
        _clock = clock ?? TimeProvider.System;
        _profiles = profiles;
    }

    // Raw LLM + decision nodes run on the Orchestrator profile, resolved per node so an edit in
    // Settings → Agent profiles applies to the next node without a restart.
    private ValueTask<AgentOptions> OrchestratorProfileAsync(CancellationToken ct) =>
        _profiles is null
            ? ValueTask.FromResult(_agents.Orchestrator)
            : _profiles.ResolveAsync(nameof(AgentsOptions.Orchestrator), _agents.Orchestrator, ct);

    /// <summary>Validate (LLM-free) then compile + run the graph as a MAF Workflow, pushing per-node status
    /// to <paramref name="onNode"/>. <paramref name="tenantId"/> + <paramref name="userId"/> are passed
    /// explicitly because a Blazor circuit has no <c>ITenantContext</c>; they flow through
    /// <see cref="AmbientIdentity.Resolve"/> (the shared precedence) so the per-tenant LLM key, budget gate,
    /// tool policy + evidence all resolve the signed-in tenant, not <c>default</c>.</summary>
    public async Task<GraphRunResult> RunAsync(
        PlanGraph graph,
        string userStoryText,
        int nMax,
        string tenantId,
        string? userId,
        Func<GraphNodeEvent, Task> onNode,
        Func<GraphHumanRequest, Task<GraphHumanReply>>? onHuman = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(onNode);

        var plan = GraphPlanner.Plan(graph);
        if (!plan.IsRunnable)
        {
            foreach (var v in plan.Nodes.Where(n => n.Support != NodeSupport.Supported))
            {
                await onNode(new GraphNodeEvent(v.NodeId, GraphNodePhase.Skipped, null, v.Reason)).ConfigureAwait(false);
            }
            var reasons = string.Join("; ", plan.Errors
                .Concat(plan.Nodes.Where(n => n.Support != NodeSupport.Supported).Select(n => n.Reason)));
            return new GraphRunResult(false, $"Graph not runnable: {reasons}");
        }

        // Run-level budget gate. The Workflow studio runs full multi-agent LLM work on AgentOS's OWN
        // server tokens, so it passes the same per-tenant spend check as the pipeline path (this was an
        // ungated server-token entrypoint). Returns a clean failed result — not throw — to match the
        // not-runnable channel so the canvas renders the block instead of crashing the circuit. Skipped
        // when no guard is wired (no-DB/standalone). The graph's own per-node spend is persisted to
        // run_metrics at the end of the run (see PersistRunAsync), so this gate meters workflow-only
        // tenants too — over consecutive runs their accumulated workflow spend trips the same cap.
        var budgetTenant = Coalesce(tenantId, OperatorTenant);
        if (_budgetGuard is not null)
        {
            // Fail-open on a transient budget-store fault: this runs on a Blazor circuit, and the gate already
            // returns a clean failed result (not throw) to avoid crashing the circuit — but an unguarded store
            // exception would still throw straight through it. Mirror IssueWorkAgent/PersistingOrchestratorAgent.
            BudgetStatus budget;
            try
            {
                budget = await _budgetGuard.EvaluateAsync(budgetTenant, ct).ConfigureAwait(false);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { budget = BudgetEvalFailed(ex); }
            catch (System.Data.Common.DbException ex) { budget = BudgetEvalFailed(ex); }
            catch (TimeoutException ex) { budget = BudgetEvalFailed(ex); }
            catch (System.IO.IOException ex) { budget = BudgetEvalFailed(ex); }
            catch (InvalidOperationException ex) { budget = BudgetEvalFailed(ex); }

            BudgetStatus BudgetEvalFailed(Exception ex)
            {
                _logger.LogWarning(ex, "Workflow budget evaluation failed for tenant {Tenant}; proceeding.", budgetTenant);
                return BudgetStatus.Unset;
            }

            if (budget.IsBlocking)
            {
                return new GraphRunResult(false,
                    $"Budget exceeded for this workspace — spent ${budget.SpentUsd:0.00} of ${budget.CapUsd:0.00} this month. The run was blocked before any agent ran.");
            }
        }

        // Carry the signed-in tenant + user into the run via the shared precedence. Each agent resolves its
        // API key through EfAppConfigStore.ResolveTenant, which reads AmbientIdentity FIRST; a Blazor circuit
        // has no HttpContext, so without this push ITenantContext resolves `default` and the run silently
        // executes on the platform/appsettings key while THIS tenant is still billed (the spend IS persisted
        // tenant-explicit — see PersistRunAsync). Mirrors InProcessPipelineClient on the 5-agent pipeline path.
        var runIdentity = AmbientIdentity.Resolve(tenantId, userId, context: null);
        using var _identity = AmbientIdentity.Push(runIdentity.TenantId, runIdentity.UserId);

        // Baseline: everything Pending.
        foreach (var n in graph.Nodes)
        {
            await onNode(new GraphNodeEvent(n.Id, GraphNodePhase.Pending, null, null)).ConfigureAwait(false);
        }

        var clampedNMax = Math.Clamp(nMax, 1, 10);
        var runGuid = Guid.NewGuid();
        var runId = runGuid.ToString("N");
        var state = new GraphState(userStoryText, clampedNMax, Coalesce(tenantId, OperatorTenant), runId);

        var supported = plan.Nodes
            .Where(v => v.Support == NodeSupport.Supported)
            .Select(v => v.NodeId)
            .ToHashSet(StringComparer.Ordinal);

        Workflow workflow;
        try
        {
            workflow = BuildWorkflow(graph, plan, supported, clampedNMax, onHuman);
        }
        catch (InvalidOperationException ex)
        {
            return new GraphRunResult(false, ex.Message);
        }

        string? failure = null;
        // Explicit End nodes are the designated terminals. If the graph HAS an End but the run quiesces without
        // any End completing (e.g. a fan-in barrier fed by a branch an If/Else skipped — MAF returns normally),
        // we must NOT report success. Empty when the graph has no End (then we don't second-guess completion).
        var terminals = TerminalNodeIds(graph, supported);
        var terminalReached = false;
        // Backstop against a non-terminating graph (a cyclic gate/loop the cap logic somehow doesn't bound):
        // cap total node activations generously, then abort with a clear failure rather than spin forever.
        var invoked = 0;
        var activationBudget = Math.Max(200, graph.Nodes.Count * (clampedNMax + 2) * 4);

        var startedAtUtc = _clock.GetUtcNow();
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, state, runId, ct).ConfigureAwait(false);
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            if (evt is ExecutorInvokedEvent ie && supported.Contains(ie.ExecutorId))
            {
                if (++invoked > activationBudget)
                {
                    failure ??= "Run exceeded its execution budget — a loop or join is not terminating. Check the graph's cycles/caps.";
                    try { await run.CancelRunAsync().ConfigureAwait(false); }
                    catch (InvalidOperationException) { /* already finished */ }
                    break;
                }
                await onNode(new GraphNodeEvent(ie.ExecutorId, GraphNodePhase.Running, null, null)).ConfigureAwait(false);
            }
            else if (evt is ExecutorCompletedEvent ce && supported.Contains(ce.ExecutorId))
            {
                if (terminals.Contains(ce.ExecutorId)) { terminalReached = true; }
                await onNode(new GraphNodeEvent(
                    ce.ExecutorId, GraphNodePhase.Done, state.NodeMeta.GetValueOrDefault(ce.ExecutorId), null,
                    state.OfflineNodes.ContainsKey(ce.ExecutorId))).ConfigureAwait(false);
            }
            else if (evt is ExecutorFailedEvent fe && supported.Contains(fe.ExecutorId))
            {
                failure ??= fe.Data?.Message ?? "node failed";
                await onNode(new GraphNodeEvent(fe.ExecutorId, GraphNodePhase.Failed, null, failure)).ConfigureAwait(false);
            }
        }

        if (failure is null && terminals.Count > 0 && !terminalReached)
        {
            failure = "Workflow stopped before reaching End — a branch or join did not complete (check Merge / If-Else wiring).";
        }

        // Meter the run's own LLM spend. Persisted even on failure (the tenant is billed for whatever ran),
        // stamping the explicit tenant so the budget gate sees it on the NEXT run — see PersistRunAsync.
        await PersistRunAsync(runGuid, state, budgetTenant, failure, startedAtUtc, ct).ConfigureAwait(false);

        return new GraphRunResult(failure is null, failure, state.AnyOffline);
    }

    // Persist the per-node spend collected during the run as a PipelineRunRecord (one pipeline_runs row +
    // one run_metrics row per billable node). This is what closes the loop on the pre-run budget gate: a
    // tenant who only ever uses the Workflow studio still accumulates counted spend, so its cap eventually
    // trips. Tenant-explicit (a Blazor circuit has a blank ITenantContext). Best-effort: a DB failure must
    // not fail an otherwise-successful run. No-op when no repository is wired (no-DB/standalone) or nothing
    // billable ran (a pure Transform/Print/Tool graph incurs no LLM cost).
    private async Task PersistRunAsync(
        Guid runId, GraphState state, string tenantId, string? failure, DateTimeOffset startedAtUtc, CancellationToken ct)
    {
        if (_runRepository is null)
        {
            return;
        }

        var metrics = state.Spend.ToArray();
        if (metrics.Length == 0)
        {
            return;
        }

        var record = new PipelineRunRecord(
            runId, BuildResult(state, metrics, failure), metrics, startedAtUtc, _clock.GetUtcNow());
        try
        {
            await _runRepository.SaveAsync(record, tenantId, ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { OnPersistFailed(runId, ex); }
        catch (System.Data.Common.DbException ex) { OnPersistFailed(runId, ex); }
        catch (TimeoutException ex) { OnPersistFailed(runId, ex); }
        catch (IOException ex) { OnPersistFailed(runId, ex); }
        catch (InvalidOperationException ex) { OnPersistFailed(runId, ex); }

        void OnPersistFailed(Guid id, Exception ex) =>
            _logger.LogError(ex, "Failed to persist workflow run {RunId} spend — the run result still stands.", id);
    }

    // Synthesize a PipelineResult from whatever artifacts the graph produced (a graph need not run the full
    // SDLC — fall back to empty artifacts) plus the summed spend. Only the run_metrics rows feed the budget
    // gate; this result is the run-history record + the pipeline_runs totals column.
    private static PipelineResult BuildResult(GraphState state, IReadOnlyList<RunMetric> metrics, string? failure)
    {
        decimal cost = 0m;
        var tokensIn = 0;
        var tokensOut = 0;
        var latencyMs = 0d;
        foreach (var m in metrics)
        {
            cost += m.CostUsd;
            tokensIn += m.TokensIn;
            tokensOut += m.TokensOut;
            latencyMs += m.LatencyMs;
        }

        var total = new AgentMetrics("graph", "multiple", tokensIn, tokensOut, cost, TimeSpan.FromMilliseconds(latencyMs));
        var storyText = string.IsNullOrWhiteSpace(state.Story.Description) ? "(workflow run)" : state.Story.Description;
        return new PipelineResult(
            UserStory: new UserStory(storyText, state.Story.NMax),
            Spec: state.Spec ?? EmptySpec(),
            Code: state.Code ?? EmptyCode(),
            Tests: state.Tests ?? EmptyTests(),
            QaHistory: state.LastQa is null ? [] : [state.LastQa],
            Status: failure is null ? PipelineStatus.Done : PipelineStatus.Failed,
            TotalMetrics: total);
    }

    private static RequirementSpec EmptySpec()
        => new("(workflow)", string.Empty, [], [], [], [], [], [], AgentMetrics.Empty);

    private static CodeArtifact EmptyCode()
        => new("(none)", "Clean Architecture", [], null, AgentMetrics.Empty);

    private static TestArtifact EmptyTests()
        => new("xUnit", [], 0, 0, 0, 0, AgentMetrics.Empty);

    // ---- compile PlanGraph -> MAF Workflow ----

    private Workflow BuildWorkflow(
        PlanGraph graph, GraphValidationResult plan, HashSet<string> supported, int nMax,
        Func<GraphHumanRequest, Task<GraphHumanReply>>? onHuman)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Per-node distinct outgoing edge labels — the route options a decision (If/Else, Switch) node picks
        // among. Edge labels are authoritative (the runner must choose a route an edge actually carries);
        // node.Routes is the fallback when no edge is labelled.
        var outLabels = graph.Nodes.ToDictionary(
            n => n.Id,
            n => (IReadOnlyList<string>)graph.Edges
                .Where(e => e.SourceId == n.Id && !string.IsNullOrWhiteSpace(e.Label))
                .Select(e => e.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.Ordinal);

        // One MAF executor per supported node. The executor id IS the node id, so the canvas can correlate
        // MAF's ExecutorInvoked/Completed/Failed events back to the node that emitted them.
        var exec = new Dictionary<string, NodeExecutor>(StringComparer.Ordinal);
        foreach (var id in supported)
        {
            var node = byId[id];
            exec[id] = new NodeExecutor(id, RunnerFor(node, onHuman, outLabels.GetValueOrDefault(id) ?? []));
        }

        var start = exec[plan.StartNodeId!];
        var builder = new WorkflowBuilder(start);

        // A Merge node's INCOMING edges become a single MAF fan-in barrier (the merge waits for every branch
        // to arrive). Those edges are wired here and EXCLUDED from the per-source pass below so they're never
        // double-wired.
        var mergeIds = graph.Nodes
            .Where(n => supported.Contains(n.Id) && Is(n, "Merge"))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mergeId in mergeIds)
        {
            var sources = graph.Edges
                .Where(e => e.TargetId == mergeId && exec.ContainsKey(e.SourceId) && e.SourceId != mergeId)
                .Select(e => e.SourceId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (sources.Count > 0)
            {
                builder.AddFanInBarrierEdge(
                    sources.Select(id => (ExecutorBinding)exec[id]).ToList(), exec[mergeId]);
            }
        }

        foreach (var node in graph.Nodes.Where(n => supported.Contains(n.Id)))
        {
            // Outgoing edges to supported targets, EXCLUDING edges into a Merge (handled by its fan-in above).
            var outs = graph.Edges
                .Where(e => e.SourceId == node.Id && exec.ContainsKey(e.TargetId) && !mergeIds.Contains(e.TargetId))
                .ToList();
            if (outs.Count == 0)
            {
                continue;
            }

            if (Is(node, "Parallel"))
            {
                // Fan-out: the SAME GraphState is delivered to every branch, and MAF's off-thread runtime runs
                // the branches as CONCURRENT tasks within one superstep — so the shared state must be (and is)
                // thread-safe (see GraphState's concurrent collections + interlocked counters).
                builder.AddFanOutEdge(
                    exec[node.Id], outs.Select(e => (ExecutorBinding)exec[e.TargetId]).ToList());
            }
            else if (Is(node, "Evaluator") || RoleOf(node) == "Qa")
            {
                WireGate(builder, exec, node, outs, nMax);
            }
            else if (Is(node, "IfElse") || Is(node, "Switch"))
            {
                WireRouter(builder, exec, node, outs);
            }
            else if (Is(node, "Loop"))
            {
                WireLoop(builder, exec, node, outs, nMax);
            }
            else if (Is(node, "Merge"))
            {
                var branches = graph.Edges
                    .Where(e => e.TargetId == node.Id && exec.ContainsKey(e.SourceId) && e.SourceId != node.Id)
                    .Select(e => e.SourceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                WireMergeOut(builder, exec, node, outs, Math.Max(1, branches));
            }
            else
            {
                foreach (var e in outs)
                {
                    builder.AddEdge(exec[node.Id], exec[e.TargetId]);
                }
            }
        }

        // Output from the End node(s); fall back to the last node in linear order so a graph without an
        // explicit End still produces a terminating output rather than idling.
        var ends = TerminalNodeIds(graph, supported).Select(id => exec[id]).ToList();
        if (ends.Count == 0 && plan.LinearOrder.Count > 0)
        {
            var lastId = plan.LinearOrder.LastOrDefault(supported.Contains);
            if (lastId is not null)
            {
                ends.Add(exec[lastId]);
            }
        }
        foreach (var end in ends)
        {
            builder.WithOutputFrom(end);
        }

        return builder.Build();
    }

    // QA / Evaluator gate: a "fail/loop/regenerate"-labelled edge loops back while inconsistent and under the
    // iteration cap; the forward edge(s) fire once consistent OR the cap is exhausted — so exactly one branch
    // is always taken and the workflow terminates. The cap counts THIS gate's own passes (s.Counters[gateId],
    // bumped by RunQaAsync), NOT the global coding count — so a gate whose loop-back path has no Coding node
    // still terminates, and two gates in series each get their own independent cap.
    private static void WireGate(
        WorkflowBuilder builder, Dictionary<string, NodeExecutor> exec, PlanNode node, List<PlanEdge> outs, int nMax)
    {
        var nodeId = node.Id;
        var cap = node.MaxIterations > 0 ? Math.Min(nMax, node.MaxIterations) : nMax;
        var loop = outs.FirstOrDefault(e => IsLoopLabel(e.Label));
        if (loop is null)
        {
            foreach (var e in outs)
            {
                builder.AddEdge(exec[node.Id], exec[e.TargetId]);
            }
            return;
        }

        builder.AddEdge<GraphState>(exec[node.Id], exec[loop.TargetId],
            s => s is not null && !s.LastConsistent && s.Counters.GetValueOrDefault(nodeId) < cap);
        foreach (var e in outs.Where(e => e != loop))
        {
            builder.AddEdge<GraphState>(exec[node.Id], exec[e.TargetId],
                s => s is not null && (s.LastConsistent || s.Counters.GetValueOrDefault(nodeId) >= cap));
        }
    }

    // A "router" node (If/Else, Switch) sends the state to the one outgoing edge whose label matches the
    // route the node chose (case-insensitive). Unlabelled edges act as the default branch (taken when the
    // chosen route matches no labelled edge). With no labelled edges at all the node is a pass-through.
    private static void WireRouter(
        WorkflowBuilder builder, Dictionary<string, NodeExecutor> exec, PlanNode node, List<PlanEdge> outs)
    {
        var nodeId = node.Id;
        var labeled = outs.Where(e => !string.IsNullOrWhiteSpace(e.Label)).ToList();
        if (labeled.Count == 0)
        {
            foreach (var e in outs)
            {
                builder.AddEdge(exec[node.Id], exec[e.TargetId]);
            }
            return;
        }

        foreach (var e in labeled)
        {
            // Trim the edge label so it matches the trimmed route the decision node stores (RunDecisionAsync
            // trims its routes and MatchRoute trims the LLM reply). Leaving labels untrimmed made " proceed "
            // never equal the chosen "proceed", silently firing the default branch instead of the labelled one.
            var label = e.Label?.Trim();
            builder.AddEdge<GraphState>(exec[node.Id], exec[e.TargetId],
                s => s is not null && s.Routes.TryGetValue(nodeId, out var r)
                    && string.Equals(r, label, StringComparison.OrdinalIgnoreCase));
        }

        var labels = labeled.Select(e => e.Label?.Trim()).ToList();
        foreach (var e in outs.Where(e => string.IsNullOrWhiteSpace(e.Label)))
        {
            builder.AddEdge<GraphState>(exec[node.Id], exec[e.TargetId],
                s => s is not null && (!s.Routes.TryGetValue(nodeId, out var r)
                    || !labels.Exists(l => string.Equals(l, r, StringComparison.OrdinalIgnoreCase))));
        }
    }

    // A Loop node repeats its loop-labelled back-edge while a per-node counter is under the cap, then takes
    // the forward edge(s). The Loop runner increments the counter on each pass, so exactly one branch fires
    // and the loop always terminates at the cap. With no loop-labelled edge the node is a pass-through.
    private static void WireLoop(
        WorkflowBuilder builder, Dictionary<string, NodeExecutor> exec, PlanNode node, List<PlanEdge> outs, int nMax)
    {
        var nodeId = node.Id;
        var cap = node.MaxIterations > 0 ? Math.Min(nMax, node.MaxIterations) : nMax;
        var back = outs.FirstOrDefault(e => IsLoopLabel(e.Label));
        if (back is null)
        {
            foreach (var e in outs)
            {
                builder.AddEdge(exec[node.Id], exec[e.TargetId]);
            }
            return;
        }

        builder.AddEdge<GraphState>(exec[node.Id], exec[back.TargetId],
            s => s is not null && s.Counters.GetValueOrDefault(nodeId) < cap);
        foreach (var e in outs.Where(e => e != back))
        {
            builder.AddEdge<GraphState>(exec[node.Id], exec[e.TargetId],
                s => s is not null && s.Counters.GetValueOrDefault(nodeId) >= cap);
        }
    }

    // A Merge's fan-in barrier delivers one message per branch, so the merge executor fires once per branch.
    // Its outgoing edges fire only on the LAST delivery of each wave of N (counter % branches == 0) — collapsing
    // the N branch messages into a SINGLE downstream emission. Using the monotonic counter modulo the branch
    // count (rather than `>= branches`) keeps it correct when the merge sits inside a loop and is re-entered:
    // wave 1 fires at N, wave 2 at 2N, etc., instead of firing on every delivery after the first wave.
    private static void WireMergeOut(
        WorkflowBuilder builder, Dictionary<string, NodeExecutor> exec, PlanNode node, List<PlanEdge> outs, int branches)
    {
        var nodeId = node.Id;
        foreach (var e in outs)
        {
            builder.AddEdge<GraphState>(exec[node.Id], exec[e.TargetId],
                s => s is not null && s.Counters.GetValueOrDefault(nodeId) is var c && c > 0 && c % branches == 0);
        }
    }

    private static bool Is(PlanNode node, string type) => string.Equals(node.StepType, type, StringComparison.Ordinal);

    // The explicit End nodes that are supported — the designated workflow terminals.
    private static HashSet<string> TerminalNodeIds(PlanGraph graph, HashSet<string> supported)
        => graph.Nodes
            .Where(n => supported.Contains(n.Id) && string.Equals(n.StepType, "End", StringComparison.Ordinal))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsLoopLabel(string label)
        => label.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || label.Contains("loop", StringComparison.OrdinalIgnoreCase)
        || label.Contains("again", StringComparison.OrdinalIgnoreCase)
        || label.Contains("repeat", StringComparison.OrdinalIgnoreCase)
        || label.Contains("retry", StringComparison.OrdinalIgnoreCase)
        || label.Contains("regenerate", StringComparison.OrdinalIgnoreCase);

    // ---- per-node work (the executor body) ----

    private Func<GraphState, CancellationToken, ValueTask<string?>> RunnerFor(
        PlanNode node, Func<GraphHumanRequest, Task<GraphHumanReply>>? onHuman, IReadOnlyList<string> routeOptions)
    {
        if (string.Equals(node.StepType, "Evaluator", StringComparison.Ordinal))
        {
            return (s, ct) => RunQaAsync(node.Id, s, ct);
        }
        if (string.Equals(node.StepType, "End", StringComparison.Ordinal))
        {
            return (_, _) => new ValueTask<string?>("complete");
        }
        return node.StepType switch
        {
            "Agent" => RoleOf(node) switch
            {
                "Requirement" => (s, ct) => RunRequirementAsync(s, ct),
                "Coding" => (s, ct) => RunCodingAsync(s, ct),
                "Testing" => (s, ct) => RunTestingAsync(s, ct),
                "Qa" => (s, ct) => RunQaAsync(node.Id, s, ct),
                _ => (_, _) => throw new LlmException($"Agent role '{node.AgentRole}' is not runnable."),
            },
            "Llm" => (s, ct) => RunLlmAsync(node, s, ct),
            "Tool" => (s, ct) => RunToolAsync(node, s, ct),
            "Transform" => (s, _) => new ValueTask<string?>(RunTransform(node, s)),
            "ExtractJson" => (s, _) => new ValueTask<string?>(RunExtractJson(node, s)),
            "Print" => (s, _) => new ValueTask<string?>(ResolveInput(node.Input, s)),
            // Control-flow: the runner produces the *decision* (route / counter / approval); the MAF edges
            // wired in BuildWorkflow act on the state the runner mutates. Parallel/Merge are pass-throughs —
            // the fan-out / fan-in-barrier topology IS their behaviour.
            "IfElse" or "Switch" => (s, ct) => RunDecisionAsync(node, routeOptions, s, ct),
            "Loop" => (s, _) => new ValueTask<string?>(RunLoop(node, s)),
            "Parallel" => (_, _) => new ValueTask<string?>("fork"),
            "Merge" => (s, _) => new ValueTask<string?>(RunMerge(node, s)),
            "Human" => (s, ct) => RunHumanAsync(node, onHuman, s, ct),
            _ => (_, _) => throw new LlmException($"Node type '{node.StepType}' is not runnable."),
        };
    }

    // If/Else + Switch: choose one route. The LLM is asked to pick among the available route labels; any
    // failure (no provider, no match) falls back to the FIRST route so the branch always advances. The
    // chosen route is stored in state for the conditional edges (see WireRouter).
    private async ValueTask<string?> RunDecisionAsync(
        PlanNode node, IReadOnlyList<string> routeOptions, GraphState s, CancellationToken ct)
    {
        var source = routeOptions.Count > 0 ? routeOptions : node.Routes ?? [];
        var routes = source.Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (routes.Count == 0)
        {
            return "no routes";
        }

        var chosen = routes[0];
        try
        {
            var sys = "You are a routing function in a workflow. Read the context and choose EXACTLY ONE "
                + "option from this list, replying with ONLY that word: " + string.Join(", ", routes) + ".";
            var question = Interpolate(string.IsNullOrWhiteSpace(node.Description) ? node.Title : node.Description, s);
            var prompt = $"{question}\n\nContext: {Truncate(ResolveInput(node.Input, s), 1500)}"
                + $"\n\nReply with exactly one of: {string.Join(", ", routes)}";
            var opt = await OrchestratorProfileAsync(ct).ConfigureAwait(false);
            var request = new LlmRequest(sys, prompt, opt.Model, opt.Temperature, opt.MaxTokens);
            request.Validate();
            var response = await _llmFactory.Create(opt.Provider).SendAsync(request, ct).ConfigureAwait(false);
            RecordLlmSpend(s, node, response);
            var match = MatchRoute(response.Content, routes);
            if (match is not null)
            {
                chosen = match;
            }
        }
        catch (LlmException)
        {
            // No provider / unparseable answer — keep the deterministic first-route default.
        }

        s.Routes[node.Id] = chosen;
        WriteOut(node, s, chosen);
        return $"→ {chosen}";
    }

    // Pick the route an LLM reply names. Tiered so a route that is a substring of another (e.g. "no" vs
    // "now", "pass" vs "passfail") is never mis-selected: exact match → whole-word match → longest substring.
    private static string? MatchRoute(string? content, List<string> routes)
    {
        var reply = (content ?? string.Empty).Trim();
        if (reply.Length == 0)
        {
            return null;
        }
        return routes.Find(r => string.Equals(reply, r, StringComparison.OrdinalIgnoreCase))
            ?? routes.Find(r => System.Text.RegularExpressions.Regex.IsMatch(
                reply, $@"\b{System.Text.RegularExpressions.Regex.Escape(r)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            ?? routes.Where(r => reply.Contains(r, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Length).FirstOrDefault();
    }

    private static string RunLoop(PlanNode node, GraphState s) => $"pass {s.Bump(node.Id)}";

    // A Merge sits behind a MAF fan-in barrier, which delivers ONE message per branch — so the executor runs
    // once per branch. We count those deliveries (atomically — branches resume on the threadpool); the merge's
    // outgoing edges fire once per WAVE of N (see WireMergeOut), so downstream runs exactly once each pass even
    // when the merge sits inside a loop.
    private static string RunMerge(PlanNode node, GraphState s) => $"joined {s.Bump(node.Id)}";

    // Human checkpoint: pause the run and ask the operator. With no callback (headless Api / tests) it
    // auto-approves so the workflow still terminates; a rejection stops the run at this node.
    private static async ValueTask<string?> RunHumanAsync(
        PlanNode node, Func<GraphHumanRequest, Task<GraphHumanReply>>? onHuman, GraphState s, CancellationToken ct)
    {
        // Check cancellation BEFORE the onHuman null-branch so a cancelled run does not silently auto-approve
        // when no operator is attached (both paths must honour the token).
        ct.ThrowIfCancellationRequested();
        var question = Interpolate(string.IsNullOrWhiteSpace(node.Description) ? node.Title : node.Description, s);
        var context = ResolveInput(node.Input, s);
        if (onHuman is null)
        {
            WriteOut(node, s, "approved (no operator attached)");
            return "auto-approved";
        }

        var reply = await onHuman(new GraphHumanRequest(node.Id, node.Title, question, context)).ConfigureAwait(false);
        var note = reply.Note?.Trim();
        if (!reply.Approved)
        {
            WriteOut(node, s, string.IsNullOrEmpty(note) ? "rejected" : $"rejected: {note}");
            throw new LlmException(
                $"Human checkpoint '{node.Title}' was rejected" + (string.IsNullOrEmpty(note) ? "." : $": {note}"));
        }

        WriteOut(node, s, string.IsNullOrEmpty(note) ? "approved" : note);
        return string.IsNullOrEmpty(note) ? "approved" : $"approved · {Truncate(note, 40)}";
    }

    private async ValueTask<string?> RunRequirementAsync(GraphState s, CancellationToken ct)
    {
        s.Spec = await _requirement.RunAsync(s.Story, ct).ConfigureAwait(false);
        s.Bag["spec"] = s.Spec.Title;
        RecordAgentSpend(s, "RequirementAgent", "KC1", s.Spec.Metrics);
        return Metric(s.Spec.Metrics);
    }

    private async ValueTask<string?> RunCodingAsync(GraphState s, CancellationToken ct)
    {
        s.IncrementIteration();   // a coding pass starts each iteration of the QA loop
        EnsureSpec(s);
        s.Code = await _coding.RunAsync(s.Spec!, s.LastQa, ct).ConfigureAwait(false);
        s.Bag["code"] = $"{s.Code.Files.Count} files";
        RecordAgentSpend(s, "CodingAgent", "KC2", s.Code.Metrics);
        return Metric(s.Code.Metrics);
    }

    private async ValueTask<string?> RunTestingAsync(GraphState s, CancellationToken ct)
    {
        EnsureSpec(s);
        EnsureCode(s, "Testing");
        s.Tests = await _testing.RunAsync(s.Spec!, s.Code!, s.LastQa, ct).ConfigureAwait(false);
        s.Bag["tests"] = $"{s.Tests.TotalCount} tests";
        RecordAgentSpend(s, "TestingAgent", "KC3", s.Tests.Metrics);
        return Metric(s.Tests.Metrics);
    }

    // nodeId lets a QA/Evaluator GATE count its own passes (s.Counters[nodeId]) so its loop cap is per-gate,
    // independent of the global coding count and of any other gate.
    private async ValueTask<string?> RunQaAsync(string nodeId, GraphState s, CancellationToken ct)
    {
        EnsureSpec(s);
        EnsureCode(s, "QA");
        if (s.Tests is null)
        {
            throw new LlmException("A QA node ran before a Testing node produced tests — wire a Testing node upstream first.");
        }
        s.LastQa = await _qa.RunAsync(s.Spec!, s.Code!, s.Tests!, ct).ConfigureAwait(false);
        s.Bag["qa"] = s.LastQa.Score.ToString("0.00", CultureInfo.InvariantCulture);
        RecordAgentSpend(s, "QaAgent", "KC5", s.LastQa.Metrics);
        s.Bump(nodeId);
        return $"{Metric(s.LastQa.Metrics)} · consistent={s.LastQa.IsConsistent}";
    }

    private async ValueTask<string?> RunLlmAsync(PlanNode node, GraphState s, CancellationToken ct)
    {
        var prompt = Interpolate(string.IsNullOrWhiteSpace(node.Description) ? node.Title : node.Description, s);
        var opt = await OrchestratorProfileAsync(ct).ConfigureAwait(false);
        var request = new LlmRequest(string.Empty, prompt, opt.Model, opt.Temperature, opt.MaxTokens);
        request.Validate();
        var response = await _llmFactory.Create(opt.Provider).SendAsync(request, ct).ConfigureAwait(false);
        RecordLlmSpend(s, node, response);
        WriteOut(node, s, response.Content);
        return $"{response.InputTokens}→{response.OutputTokens} tok · ${response.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}";
    }

    private async ValueTask<string?> RunToolAsync(PlanNode node, GraphState s, CancellationToken ct)
    {
        var (toolName, input) = ParseToolSpec(node, s);
        var tool = _toolRegistry.Resolve(toolName)
            ?? throw new LlmException($"Tool '{toolName}' is not registered.");

        var request = new ToolInvocationRequest(
            ToolName: toolName,
            CallId: Guid.NewGuid().ToString("N"),
            Input: input,
            TenantId: s.TenantId,
            RunId: s.RunId);
        request.Validate();

        var result = await _toolGateway.InvokeAsync(tool, request, ct).ConfigureAwait(false);
        WriteOut(node, s, result.Output);
        return result.Denied ? "denied by policy" : result.IsError ? "tool error" : "ok";
    }

    private static string RunTransform(PlanNode node, GraphState s)
    {
        var template = string.IsNullOrWhiteSpace(node.Description) ? "${" + node.Input.Trim() + "}" : node.Description;
        var value = Interpolate(template, s);
        WriteOut(node, s, value);
        return Truncate(value, 80);
    }

    private static string RunExtractJson(PlanNode node, GraphState s)
    {
        var json = JsonExtractor.ExtractJson(ResolveInput(node.Input, s), "ExtractJson");
        WriteOut(node, s, json);
        return "json ok";
    }

    // ---- state helpers ----

    private static void WriteOut(PlanNode node, GraphState s, string value)
    {
        var key = string.IsNullOrWhiteSpace(node.Output) ? node.Id : node.Output.Trim();
        s.Bag[key] = value;
    }

    // Resolve "in:" — the first comma-separated key, looked up in the bag, then the well-known artifacts.
    private static string ResolveInput(string inputSpec, GraphState s)
    {
        var key = (inputSpec ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return Lookup(key, s) ?? inputSpec ?? string.Empty;
    }

    // Replace ${key} placeholders with bag values or well-known artifact summaries.
    private static string Interpolate(string template, GraphState s)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return template ?? string.Empty;
        }
        return System.Text.RegularExpressions.Regex.Replace(
            template, @"\$\{([^}]+)\}", m => Lookup(m.Groups[1].Value.Trim(), s) ?? m.Value);
    }

    private static string? Lookup(string key, GraphState s)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        if (s.Bag.TryGetValue(key, out var v))
        {
            return v;
        }
        return key.ToLowerInvariant() switch
        {
            "userstory" or "input" or "story" => s.Story.Description,
            "spec" => s.Spec?.Title,
            "code" => s.Code is null ? null : $"{s.Code.Files.Count} files",
            "tests" => s.Tests is null ? null : $"{s.Tests.TotalCount} tests",
            "qa" => s.LastQa?.Score.ToString("0.00", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static (string Tool, string Input) ParseToolSpec(PlanNode node, GraphState s)
    {
        // A Tool node's Description is {"tool":"<name>","input":{...}}; input values may use ${state} refs.
        if (string.IsNullOrWhiteSpace(node.Description))
        {
            throw new LlmException($"Tool node '{node.Title}' has no spec — set Description to {{\"tool\":\"name\",\"input\":{{…}}}}.");
        }
        try
        {
            using var doc = JsonDocument.Parse(node.Description);
            var root = doc.RootElement;
            var name = root.TryGetProperty("tool", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new LlmException($"Tool node '{node.Title}' spec is missing a \"tool\" name.");
            }
            var input = root.TryGetProperty("input", out var i) ? i.GetRawText() : "{}";
            return (name!, Interpolate(input, s));
        }
        catch (JsonException ex)
        {
            throw new LlmException($"Tool node '{node.Title}' spec is not valid JSON: {ex.Message}");
        }
    }

    private static string? RoleOf(PlanNode node)
        => string.Equals(node.StepType, "Evaluator", StringComparison.Ordinal) ? "Qa" : AgentRoleMap.Canonical(node.AgentRole);

    private static void EnsureSpec(GraphState s)
    {
        if (s.Spec is null)
        {
            throw new LlmException("A Coding/Testing/QA node ran before a Requirement node — wire Requirement first.");
        }
    }

    private static void EnsureCode(GraphState s, string who)
    {
        if (s.Code is null)
        {
            throw new LlmException($"A {who} node ran before a Coding node produced code — wire a Coding node upstream first.");
        }
    }

    // ---- spend metering (feeds the budget gate via run_metrics) ----

    // Record a typed-agent node's spend from its returned artifact metrics. KcId mirrors the pipeline
    // convention (KC1/KC2/KC3/KC5) so workflow spend lines up with pipeline spend in the cost dashboard.
    private void RecordAgentSpend(GraphState s, string agentName, string kcId, AgentMetrics m)
        => s.Record(new RunMetric(
            s.RunId, kcId, s.Iteration, agentName, m.Model, m.Provider,
            m.InputTokens, m.OutputTokens, m.Latency.TotalMilliseconds, m.CostUsd, true, null, _clock.GetUtcNow()));

    // Record a raw-LLM / decision node's spend from its LlmResponse. These call the gateway directly (not
    // through a typed agent), so without this their tokens would never reach run_metrics.
    private void RecordLlmSpend(GraphState s, PlanNode node, LlmResponse r)
        => s.Record(new RunMetric(
            s.RunId, "graph", 0, NodeLabel(node), r.Model, r.Provider,
            r.InputTokens, r.OutputTokens, r.Latency.TotalMilliseconds, r.CostUsd, true, null, _clock.GetUtcNow()));

    // run_metrics.AgentName is capped at 64 chars; a node title can be longer, so clamp it.
    private static string NodeLabel(PlanNode node)
        => Truncate(string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title, 64);

    private static string Metric(AgentMetrics m)
        => $"{m.InputTokens}→{m.OutputTokens} tok · ${m.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static string Coalesce(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    // ---- the message threaded along every edge + the MAF executor wrapper ----

    /// <summary>Mutable run state passed node-to-node along the workflow edges (one instance per run).
    /// IMPORTANT: MAF's default in-process runtime is OFF-THREAD — a Parallel fan-out delivers this SAME
    /// instance to every branch and runs the branches as concurrent Tasks within one superstep, and the
    /// event-watcher reads <see cref="NodeMeta"/> on a different thread from the run loop. So every shared
    /// collection here is concurrent and the pass counter is interlocked — plain Dictionary / <c>++</c>
    /// would corrupt/throw under that concurrency.</summary>
    internal sealed class GraphState(string userStory, int nMax, string tenantId, string runId)
    {
        public UserStory Story { get; } = new(userStory, NMax: nMax);
        public string TenantId { get; } = tenantId;
        public string RunId { get; } = runId;

        // Coding-pass count, bumped from executor bodies that may run concurrently → interlocked field.
        private int _iteration;
        public int Iteration => Volatile.Read(ref _iteration);
        public int IncrementIteration() => Interlocked.Increment(ref _iteration);

        public RequirementSpec? Spec { get; set; }
        public CodeArtifact? Code { get; set; }
        public TestArtifact? Tests { get; set; }
        public QaReport? LastQa { get; set; }
        public bool LastConsistent => LastQa?.IsConsistent ?? false;
        public ConcurrentDictionary<string, string> Bag { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string?> NodeMeta { get; } = new(StringComparer.Ordinal);

        /// <summary>Decision-node id → the route it chose (drives If/Else + Switch conditional edges).</summary>
        public ConcurrentDictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);

        /// <summary>Gate / Loop / Merge node id → monotonic pass count (drives loop caps + the merge join).</summary>
        public ConcurrentDictionary<string, int> Counters { get; } = new(StringComparer.Ordinal);

        /// <summary>Atomically increment a node's counter and return the new value.</summary>
        public int Bump(string nodeId) => Counters.AddOrUpdate(nodeId, 1, static (_, v) => v + 1);

        /// <summary>Per-node LLM spend collected during the run (one entry per billable node). A
        /// <see cref="ConcurrentBag{T}"/> because a Parallel fan-out records from several threads at once.
        /// Persisted to run_metrics at the end of the run so the budget gate meters workflow spend.</summary>
        public ConcurrentBag<RunMetric> Spend { get; } = new();

        /// <summary>Nodes answered by the canned offline client (no model reached). Keyed by node id.</summary>
        public ConcurrentDictionary<string, bool> OfflineNodes { get; } = new(StringComparer.Ordinal);

        /// <summary>True when any node in this run was answered by the offline client.</summary>
        public bool AnyOffline => !OfflineNodes.IsEmpty;

        // The node whose body is executing on this async flow. AsyncLocal, so concurrent Parallel branches
        // (separate Tasks) each see their own node — a shared field would mis-attribute under fan-out.
        private readonly AsyncLocal<string?> _currentNode = new();

        /// <summary>Set by <see cref="NodeExecutor"/> around a node body so spend can be attributed to it.</summary>
        public string? CurrentNode
        {
            get => _currentNode.Value;
            set => _currentNode.Value = value;
        }

        /// <summary>Append one node's spend (thread-safe), flagging the node when the answer was canned.</summary>
        public void Record(RunMetric metric)
        {
            Spend.Add(metric);
            if (LlmProviders.IsOffline(metric.Provider) && CurrentNode is { } node)
            {
                OfflineNodes[node] = true;
            }
        }
    }

    /// <summary>A MAF executor whose body is the node's work delegate. Returns the (mutated) state so MAF
    /// forwards it along the outgoing edges; the terminal executor's return value is harvested as the
    /// workflow output via <c>WithOutputFrom</c>. Stashes the node's display meta for the live canvas.</summary>
    private sealed class NodeExecutor(string id, Func<GraphState, CancellationToken, ValueTask<string?>> run)
        : Executor<GraphState, GraphState>(id)
    {
        public override async ValueTask<GraphState> HandleAsync(GraphState state, IWorkflowContext context, CancellationToken ct)
        {
            state.CurrentNode = Id;
            try
            {
                state.NodeMeta[Id] = await run(state, ct).ConfigureAwait(false);
            }
            finally
            {
                state.CurrentNode = null;
            }
            return state;
        }
    }
}
