// E1.10 — the metrics the dashboards + alert rules in deploy/observability read. Each test pins one instrument the
// operations docs promise (name, unit, tags): LLM tokens per billing class + agent, LLM errors by error.type (the
// 429-storm alert), runs by outcome (the failed-run alert + run-success SLO), budget refusals + utilisation (the
// over-cap alert) and the health-status gauge (the probe-down alert).
//
// Meters are process-global and xUnit runs classes in parallel, so captures filter on a value unique to the test
// (a model id, a tenant id, a health-check name) — or, for tags that can't be unique (run outcome), assert "at least".

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain;
using AgentOs.Domain.Code;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.Llm;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Cost;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Orchestration;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.ServiceDefaults;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Telemetry;

public sealed class OpsMetricsTests
{
    /// <summary>One captured measurement: instrument, unit, value and tags.</summary>
    private sealed record Measurement(string Instrument, string? Unit, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    /// <summary>Listens to one meter; keeps only measurements whose tags satisfy <paramref name="keep"/>.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _items = [];

        public Capture(string meter, Func<IReadOnlyDictionary<string, object?>, bool> keep)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meter) { l.EnableMeasurementEvents(instrument); }
            };
            void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var t in tags) { dict[t.Key] = t.Value; }
                if (!keep(dict)) { return; }
                lock (_items) { _items.Add(new Measurement(i.Name, i.Unit, v, dict)); }
            }
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Of(string instrument)
        {
            lock (_items) { return _items.Where(m => m.Instrument == instrument).ToList(); }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool> ModelIs(string model) =>
        tags => tags.TryGetValue("gen_ai.request.model", out var v) && (v as string) == model;

    private static IChatClient ClientReturning(UsageDetails usage)
    {
        var c = Substitute.For<IChatClient>();
        c.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
        return c;
    }

    private static PooledChatLlmClient Pooled(Func<string, IChatClient> byKey, params string[] keys) => new(
        "Claude", (key, _) => byKey(key), _ => ValueTask.FromResult<IReadOnlyList<string>>(keys), new ApiKeyRouter(TimeProvider.System),
        ex => ex is RateLimitException, _ => null, NullLogger.Instance, TimeSpan.FromMilliseconds(1));

    private sealed class RateLimitException : Exception
    {
        public RateLimitException() : base("429 Too Many Requests") { }
    }

    [Fact]
    public async Task LlmCall_WithCacheUsage_SplitsTokensIntoBillingClassesTaggedWithAgent()
    {
        const string model = "ops-token-classes-model";
        using var capture = new Capture(LlmTelemetry.SourceName, ModelIs(model));
        var usage = new UsageDetails
        {
            InputTokenCount = 1000,
            CachedInputTokenCount = 600,
            OutputTokenCount = 200,
            ReasoningTokenCount = 50,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { [UsageReader.CacheWriteKey] = 100 },
        };

        await Pooled(_ => ClientReturning(usage), "k1").SendAsync(new LlmRequest("s", "u", model) { AgentRole = "Coding" });

        var tokens = capture.Of(LlmTelemetry.Instruments.Tokens);
        double Class(string c) => tokens.Where(m => m.Tag(LlmTelemetry.TokenClassTag) == c).Sum(m => m.Value);
        Class("fresh_input").ShouldBe(300);
        Class("cache_read").ShouldBe(600);
        Class("cache_write").ShouldBe(100);
        Class("output").ShouldBe(200);
        Class("reasoning").ShouldBe(50);
        tokens.ShouldAllBe(m => m.Tag("gen_ai.agent.name") == "Coding" && m.Tag("gen_ai.system") == "anthropic");

        var cost = capture.Of(LlmTelemetry.Instruments.CostUsd).ShouldHaveSingleItem();
        cost.Unit.ShouldBe("{USD}"); // "USD" would become agentos_llm_cost_usd_USD in Prometheus
        cost.Tag("gen_ai.agent.name").ShouldBe("Coding");
        capture.Of(LlmTelemetry.Instruments.Calls).ShouldHaveSingleItem().Tag("gen_ai.agent.name").ShouldBe("Coding");
    }

    [Fact]
    public async Task LlmCall_RateLimitedThenOk_CountsOneRateLimitedErrorAndOneCall()
    {
        const string model = "ops-429-model";
        using var capture = new Capture(LlmTelemetry.SourceName, ModelIs(model));
        var limited = Substitute.For<IChatClient>();
        limited.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RateLimitException());
        var ok = ClientReturning(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 });

        await Pooled(key => key == "k1" ? limited : ok, "k1", "k2").SendAsync(new LlmRequest("s", "u", model));

        var error = capture.Of(LlmTelemetry.Instruments.Errors).ShouldHaveSingleItem();
        error.Tag("error.type").ShouldBe(LlmTelemetry.ErrorTypes.RateLimited);
        capture.Of(LlmTelemetry.Instruments.Calls).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "transient")]
    [InlineData(401, "auth")]
    [InlineData(400, "bad_request")]
    public void ErrorTypeOf_HttpStatus_MapsToMetricValue(int status, string expected) =>
        SdkChatClients.ErrorTypeOf(new System.Net.Http.HttpRequestException("x", null, (System.Net.HttpStatusCode)status)).ShouldBe(expected);

    [Fact]
    public void RecordRun_CountsOutcomeAndRecordsDurationOnlyForStartedRuns()
    {
        using var capture = new Capture(PipelineTelemetry.MeterName,
            tags => (tags.GetValueOrDefault("agentos.run.kind") as string) == "ops-test-kind");

        PipelineTelemetry.RecordRun("ops-test-kind", PipelineTelemetry.Outcomes.Done, TimeSpan.FromSeconds(12));
        PipelineTelemetry.RecordRun("ops-test-kind", PipelineTelemetry.Outcomes.BudgetBlocked);

        var runs = capture.Of(PipelineTelemetry.Instruments.Runs);
        runs.Select(m => m.Tag("agentos.run.outcome")).ShouldBe(["done", "budget_blocked"]);
        var duration = capture.Of(PipelineTelemetry.Instruments.RunDuration).ShouldHaveSingleItem();
        duration.Value.ShouldBe(12d);
        duration.Unit.ShouldBe("s");
    }

    [Theory]
    [InlineData(PipelineStatus.Done, "done")]
    [InlineData(PipelineStatus.MaxIterationReached, "max_iterations")]
    [InlineData(PipelineStatus.Failed, "failed")]
    public void OutcomeOf_PipelineStatus_MapsToOutcome(PipelineStatus status, string expected) =>
        PipelineTelemetry.OutcomeOf(status).ShouldBe(expected);

    [Fact]
    public async Task Orchestrator_InnerThrows_CountsFailedRunAndRethrows()
    {
        using var capture = new Capture(PipelineTelemetry.MeterName,
            tags => (tags.GetValueOrDefault("agentos.run.outcome") as string) == PipelineTelemetry.Outcomes.Failed);
        var orchestrator = Orchestrator(new ThrowingInner(), BudgetStatus.Unset);

        await Should.ThrowAsync<LlmException>(() => orchestrator.RunAsync(new UserStory("story")));

        capture.Of(PipelineTelemetry.Instruments.Runs).ShouldContain(m => m.Tag("agentos.run.kind") == "pipeline");
        capture.Of(PipelineTelemetry.Instruments.RunDuration).ShouldContain(m => m.Tag("agentos.run.kind") == "pipeline");
    }

    [Fact]
    public async Task Orchestrator_InnerFinishesAtNMax_CountsMaxIterationsRun()
    {
        using var capture = new Capture(PipelineTelemetry.MeterName,
            tags => (tags.GetValueOrDefault("agentos.run.outcome") as string) == PipelineTelemetry.Outcomes.MaxIterations);
        var orchestrator = Orchestrator(new ResultInner(PipelineStatus.MaxIterationReached), BudgetStatus.Unset);

        (await orchestrator.RunAsync(new UserStory("story"))).Status.ShouldBe(PipelineStatus.MaxIterationReached);

        capture.Of(PipelineTelemetry.Instruments.Runs).ShouldContain(m => m.Tag("agentos.run.kind") == "pipeline");
    }

    [Fact]
    public async Task Orchestrator_OverEnforcedCap_CountsBudgetBlockedRunAndRefusal()
    {
        using var capture = new Capture(PipelineTelemetry.MeterName, _ => true);
        var overCap = new BudgetStatus(10m, 12m, -2m, 1.2, BudgetState.Exceeded, EnforceOn: true);
        var orchestrator = Orchestrator(new ResultInner(PipelineStatus.Done), overCap);

        await Should.ThrowAsync<BudgetExceededException>(() => orchestrator.RunAsync(new UserStory("story")));

        capture.Of(PipelineTelemetry.Instruments.Runs).ShouldContain(m =>
            m.Tag("agentos.run.outcome") == "budget_blocked" && m.Tag("agentos.run.kind") == "pipeline");
        capture.Of(PipelineTelemetry.Instruments.BudgetBlocked).ShouldContain(m => m.Tag("agentos.budget.scope") == "run");
    }

    [Fact]
    public async Task BudgetGuard_CapSet_PublishesUtilizationForTenant()
    {
        const string tenant = "ops-budget-tenant";
        using var capture = new Capture(PipelineTelemetry.MeterName,
            tags => (tags.GetValueOrDefault("agentos.tenant.id") as string) == tenant);
        var config = new InMemoryAppConfigStore();
        await config.SetForTenantAsync(tenant, BudgetGuard.CapKey, "50");
        await config.SetForTenantAsync(tenant, BudgetGuard.EnforceKey, "true");
        var runs = Substitute.For<IPipelineRunRepository>();
        runs.GetSpendForTenantAsync(tenant, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(60m);

        await new BudgetGuard(config, runs, TimeProvider.System).EvaluateAsync(tenant);

        var gauge = capture.Of(PipelineTelemetry.Instruments.BudgetUtilization).ShouldHaveSingleItem();
        gauge.Value.ShouldBe(1.2d, tolerance: 1e-9);
        gauge.Unit.ShouldBe("1"); // Prometheus: agentos_budget_utilization_ratio
        gauge.Tag("agentos.budget.enforced").ShouldBe("True");
    }

    [Fact]
    public async Task BudgetGuard_NoCap_PublishesNothing()
    {
        const string tenant = "ops-budget-unset-tenant";
        using var capture = new Capture(PipelineTelemetry.MeterName,
            tags => (tags.GetValueOrDefault("agentos.tenant.id") as string) == tenant);

        await new BudgetGuard(new InMemoryAppConfigStore(), Substitute.For<IPipelineRunRepository>(), TimeProvider.System)
            .EvaluateAsync(tenant);

        capture.Of(PipelineTelemetry.Instruments.BudgetUtilization).ShouldBeEmpty();
    }

    [Fact]
    public async Task HealthMetricsPublisher_Report_RecordsStatusPerCheckAndOverall()
    {
        using var capture = new Capture(HealthMetricsPublisher.MeterName,
            tags => (tags.GetValueOrDefault("agentos.health.check") as string) is "ops-db" or "ops-idp" or "overall");
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["ops-db"] = new(HealthStatus.Unhealthy, "down", TimeSpan.Zero, null, null),
                ["ops-idp"] = new(HealthStatus.Degraded, "slow", TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        await new HealthMetricsPublisher().PublishAsync(report, CancellationToken.None);

        var values = capture.Of(HealthMetricsPublisher.InstrumentName)
            .Where(m => m.Tag("agentos.health.check") != "overall" || m.Value < 0.5d) // statuses are 0 / 0.5 / 1; a hosted app elsewhere may publish "overall" = 1
            .GroupBy(m => m.Tag("agentos.health.check")!)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        values["ops-db"].ShouldBe(0d);
        values["ops-idp"].ShouldBe(0.5d);
        values["overall"].ShouldBe(0d);
    }

    private static PersistingOrchestratorAgent Orchestrator(IOrchestratorAgent inner, BudgetStatus budget)
    {
        var guard = Substitute.For<IBudgetGuard>();
        guard.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(budget);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("ops-orchestrator-tenant");
        return new PersistingOrchestratorAgent(inner, Substitute.For<IPipelineRunRepository>(), guard, TimeProvider.System,
            tenant, NullLogger<PersistingOrchestratorAgent>.Instance);
    }

    private sealed class ThrowingInner : IOrchestratorAgent
    {
        public Task<PipelineResult> RunAsync(UserStory story, CancellationToken cancellationToken = default) =>
            throw new LlmException("boom", "Coding");
    }

    private sealed class ResultInner(PipelineStatus status) : IOrchestratorAgent
    {
        public Task<PipelineResult> RunAsync(UserStory story, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PipelineResult(story,
                new RequirementSpec("(test)", string.Empty, [], [], [], [], [], [], AgentMetrics.Empty),
                new CodeArtifact("(none)", "Clean Architecture", [], null, AgentMetrics.Empty),
                new TestArtifact("xUnit", [], 0, 0, 0, 0, AgentMetrics.Empty),
                [],
                status,
                AgentMetrics.Empty));
    }
}
