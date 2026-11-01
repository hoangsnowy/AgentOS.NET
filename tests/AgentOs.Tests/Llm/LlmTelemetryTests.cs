// AgentOs.Tests/Llm/LlmTelemetryTests.cs
// Verifies the gen_ai.* OpenTelemetry instrumentation around LLM calls: one chat span per call with
// GenAI semantic-convention attributes, no double-count of usage on 429 failover, and cost recorded as
// a double (never decimal). Uses an ActivityListener / MeterListener to capture emissions.
//
// The ActivitySource + Meter are process-global and xUnit runs test classes in parallel, so the global
// listeners also see LLM calls from OTHER concurrently-running test classes. Every test therefore tags
// its request with a UNIQUE model id and filters captures to that model — never asserts a raw global count.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Llm;
using AgentOs.Modules.RemoteAgent;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Llm;

public sealed class LlmTelemetryTests
{
    private sealed class RateLimitEx : Exception;

    private static IChatClient OkClient(string text)
    {
        var c = Substitute.For<IChatClient>();
        c.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 },
            });
        return c;
    }

    private static IChatClient RateLimitedClient()
    {
        var c = Substitute.For<IChatClient>();
        c.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RateLimitEx());
        return c;
    }

    private static string? Model(Activity a) => a.GetTagItem("gen_ai.request.model") as string;

    /// <summary>Captures AgentOs.Llm activities for ONE model into the sink. The listener is global, so it
    /// also fires for LLM calls in other concurrently-running test classes — filtering by the test's unique
    /// model at capture time (under lock) keeps foreign activities off the non-thread-safe list entirely.</summary>
    private static ActivityListener CollectActivities(List<Activity> sink, string model)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == LlmTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (Model(a) == model)
                {
                    lock (sink) { sink.Add(a); }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task SendAsync_Success_StartsOneChatActivityWithGenAiAttributes()
    {
        const string model = "probe-success-model";
        var mine = new List<Activity>();
        using var listener = CollectActivities(mine, model);

        var router = new ApiKeyRouter(TimeProvider.System);
        var client = new PooledChatLlmClient(
            "Claude", (_, _) => OkClient("ok"), _ => ValueTask.FromResult<IReadOnlyList<string>>(new List<string> { "k1" }), router,
            _ => false, _ => null, NullLogger.Instance);

        await client.SendAsync(new LlmRequest("s", "u", model));

        mine.Count.ShouldBe(1);
        mine[0].GetTagItem("gen_ai.operation.name").ShouldBe("chat");
        mine[0].GetTagItem("gen_ai.system").ShouldBe("anthropic");
        mine[0].GetTagItem("gen_ai.usage.input_tokens").ShouldBe(7);
        mine[0].GetTagItem("gen_ai.usage.output_tokens").ShouldBe(3);
        mine[0].Status.ShouldBe(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task SendAsync_FirstKeyRateLimited_EmitsOneSuccessSpanNotTwo()
    {
        const string model = "probe-failover-model";
        var mine = new List<Activity>();
        using var listener = CollectActivities(mine, model);

        var router = new ApiKeyRouter(TimeProvider.System);
        var map = new Dictionary<string, IChatClient> { ["k1"] = RateLimitedClient(), ["k2"] = OkClient("ok") };
        var client = new PooledChatLlmClient(
            "Claude", (key, _) => map[key], _ => ValueTask.FromResult<IReadOnlyList<string>>(new List<string> { "k1", "k2" }), router,
            ex => ex is RateLimitEx, _ => null, NullLogger.Instance, TimeSpan.FromMilliseconds(1));

        await client.SendAsync(new LlmRequest("s", "u", model));

        // Two attempts → two spans, but usage is recorded on the success span ONLY (no double-count).
        mine.Count.ShouldBe(2);
        mine.Count(a => a.GetTagItem("gen_ai.usage.input_tokens") is not null).ShouldBe(1);
        mine.Count(a => a.Status == ActivityStatusCode.Ok).ShouldBe(1);
        mine.Count(a => a.Status == ActivityStatusCode.Error).ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_RecordsCostHistogram_AsDouble()
    {
        const string model = "probe-cost-model";
        var measurements = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LlmTelemetry.SourceName && instrument.Name == "agentos.llm.cost.usd")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "gen_ai.request.model" && (tag.Value as string) == model)
                {
                    measurements.Add(value);
                }
            }
        });
        meterListener.Start();

        var router = new ApiKeyRouter(TimeProvider.System);
        var client = new PooledChatLlmClient(
            "Claude", (_, _) => OkClient("ok"), _ => ValueTask.FromResult<IReadOnlyList<string>>(new List<string> { "k1" }), router,
            _ => false, _ => null, NullLogger.Instance);

        await client.SendAsync(new LlmRequest("s", "u", model));

        measurements.Count.ShouldBe(1);
        double.IsFinite(measurements[0]).ShouldBeTrue();
        measurements[0].ShouldBeGreaterThanOrEqualTo(0d);
    }

    [Fact]
    public async Task RemoteAgent_SendAsync_EmitsSpanWithZeroCostAndEstimatedTokens()
    {
        const string model = "probe-remote-model";
        var mine = new List<Activity>();
        using var listener = CollectActivities(mine, model);

        var broker = new InProcessRemoteAgentBroker();
        using var reg = broker.RegisterRunner("conn-1", new RunnerConnection(Guid.NewGuid(), "default", "member-1"));
        broker.Dispatched += d => broker.CompleteFromServer(new RemoteExecResult(d.Request.Id, true, "// generated", null));

        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("default");
        tenant.UserId.Returns("member-1");
        var tenantServices = new ServiceCollection();
        tenantServices.AddScoped(_ => tenant);
        var client = new RemoteAgentLlmClient(
            broker, tenantServices.BuildServiceProvider(), NullLogger<RemoteAgentLlmClient>.Instance);

        await client.SendAsync(new LlmRequest("sys", "build X", model));

        mine.Count.ShouldBe(1);
        mine[0].GetTagItem("gen_ai.system").ShouldBe("agentos.remote_agent");
        // The span now carries an ESTIMATED token count (the CLI reports none), not a hard 0 — while the
        // cost tag stays 0 because the run billed nothing. Assert the estimate is present and positive.
        var inTok = mine[0].GetTagItem("gen_ai.usage.input_tokens").ShouldBeOfType<int>();
        inTok.ShouldBeGreaterThan(0);
        mine[0].Status.ShouldBe(ActivityStatusCode.Ok);
    }
}
