// AgentOs.Api/Endpoints/PipelineEndpoints.cs
// Phase 4 — Minimal API endpoints for the 5 agents + pipeline.
// Phase 8 — Adds /pipeline/stream (SSE) so the Web can call this API for realtime runs.
// Boundary validation: malformed bodies return RFC 7807 ValidationProblem (400) at the API edge
// instead of surfacing as ArgumentNullException/500 from inside the agents.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Pipeline.Agents;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Pipeline;
using AgentOs.Domain.Code;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Qa;
using AgentOs.Domain.Requirements;
using AgentOs.Domain.Testing;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.Pipeline.Endpoints;

/// <summary>Maps endpoints for the 4 specialists + pipeline.</summary>
public static class PipelineEndpoints
{
    /// <summary>Maps endpoints onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        System.ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/requirement", async (UserStory? body, IRequirementAgent agent, IBudgetGuard budget, ITenantContext tenant, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForStory(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }
            if (await PipelineBudgetGate.BlockIfExceededAsync(budget, tenant, ct).ConfigureAwait(false) is { } blocked)
            {
                return blocked;
            }
            var spec = await agent.RunAsync(body!, ct).ConfigureAwait(false);
            return Microsoft.AspNetCore.Http.Results.Ok(spec);
        })
        .WithName("Requirement")
        .WithSummary("KC1 — Analyze user story → structured requirement spec")
        .WithTags("Agents")
        .Produces<RequirementSpec>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status402PaymentRequired)
        .RequirePermission(Permission.RunAgents);

        app.MapPost("/code", async (CodeRequest? body, ICodingAgent agent, IBudgetGuard budget, ITenantContext tenant, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForCode(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }
            if (await PipelineBudgetGate.BlockIfExceededAsync(budget, tenant, ct).ConfigureAwait(false) is { } blocked)
            {
                return blocked;
            }
            var artifact = await agent.RunAsync(body!.Spec, body.PreviousFeedback, ct).ConfigureAwait(false);
            return Microsoft.AspNetCore.Http.Results.Ok(artifact);
        })
        .WithName("Code")
        .WithSummary("KC2 — Generate C# Clean Architecture source code from the spec")
        .WithTags("Agents")
        .Produces<CodeArtifact>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status402PaymentRequired)
        .RequirePermission(Permission.RunAgents);

        app.MapPost("/test", async (TestRequest? body, ITestingAgent agent, IBudgetGuard budget, ITenantContext tenant, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForTest(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }
            if (await PipelineBudgetGate.BlockIfExceededAsync(budget, tenant, ct).ConfigureAwait(false) is { } blocked)
            {
                return blocked;
            }
            var artifact = await agent.RunAsync(body!.Spec, body.Code, body.PreviousFeedback, ct).ConfigureAwait(false);
            return Microsoft.AspNetCore.Http.Results.Ok(artifact);
        })
        .WithName("Test")
        .WithSummary("KC3 — Generate xUnit tests (happy/edge/error)")
        .WithTags("Agents")
        .Produces<TestArtifact>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status402PaymentRequired)
        .RequirePermission(Permission.RunAgents);

        app.MapPost("/qa", async (QaRequest? body, IQaAgent agent, IBudgetGuard budget, ITenantContext tenant, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForQa(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }
            if (await PipelineBudgetGate.BlockIfExceededAsync(budget, tenant, ct).ConfigureAwait(false) is { } blocked)
            {
                return blocked;
            }
            var report = await agent.RunAsync(body!.Spec, body.Code, body.Tests, ct).ConfigureAwait(false);
            return Microsoft.AspNetCore.Http.Results.Ok(report);
        })
        .WithName("Qa")
        .WithSummary("KC5 — Assess requirement-code-test consistency")
        .WithTags("Agents")
        .Produces<QaReport>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status402PaymentRequired)
        .RequirePermission(Permission.RunAgents);

        app.MapPost("/pipeline", async (UserStory? body, IOrchestratorAgent orchestrator, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForStory(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }
            var result = await orchestrator.RunAsync(body!, ct).ConfigureAwait(false);
            var statusCode = result.Status == PipelineStatus.Failed ? 500 : 200;
            return Microsoft.AspNetCore.Http.Results.Json(result, statusCode: statusCode);
        })
        .WithName("Pipeline")
        .WithSummary("KC4 — End-to-end pipeline with QA loop (≤ NMax iterations)")
        .WithTags("Agents")
        .Produces<PipelineResult>()
        .Produces<PipelineResult>(StatusCodes.Status500InternalServerError)
        .ProducesValidationProblem()
        .RequirePermission(Permission.RunAgents);

        // Phase 8 — Server-Sent Events stream of progress + final result.
        // Wire shape per event:   event: progress|result|error \n data: {json}\n\n
        app.MapPost("/pipeline/stream", async (UserStory? body, IPipelineClient client, HttpResponse response, CancellationToken ct) =>
        {
            if (PipelineRequestValidation.ForStory(body) is { } errors)
            {
                return Microsoft.AspNetCore.Http.Results.ValidationProblem(errors);
            }

            response.Headers.Append("Content-Type", "text/event-stream");
            response.Headers.Append("Cache-Control", "no-cache");
            response.Headers.Append("Connection", "keep-alive");
            response.Headers.Append("X-Accel-Buffering", "no"); // disable nginx buffering

            var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await foreach (var evt in client.StreamAsync(body!, ct).ConfigureAwait(false))
            {
                var (kind, payloadJson) = evt.Kind switch
                {
                    PipelineStreamEventKind.Progress => ("progress", JsonSerializer.Serialize(evt.Progress, jsonOpts)),
                    PipelineStreamEventKind.Result   => ("result",   JsonSerializer.Serialize(evt.Result,   jsonOpts)),
                    PipelineStreamEventKind.Error    => ("error",    JsonSerializer.Serialize(evt.Error,    jsonOpts)),
                    _ => ("unknown", "null"),
                };
                await response.WriteAsync($"event: {kind}\ndata: {payloadJson}\n\n", ct).ConfigureAwait(false);
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            return Microsoft.AspNetCore.Http.Results.Empty;
        })
        .WithName("PipelineStream")
        .WithSummary("KC4 — Streamed pipeline run. SSE events: progress | result | error")
        .WithTags("Agents")
        .ProducesValidationProblem()
        .RequirePermission(Permission.RunAgents);

        app.MapGet("/runs", async (IPipelineRunRepository repo, CancellationToken ct, int? limit, int? offset) =>
        {
            var take = Page.ClampLimit(limit ?? DefaultRunsPageSize);
            var skip = Page.ClampOffset(offset ?? 0);
            var runs = await repo.ListAsync(take, skip, ct).ConfigureAwait(false);
            return Microsoft.AspNetCore.Http.Results.Ok(runs);
        })
        .WithName("Runs")
        .WithSummary("Pipeline run history (summaries, newest first). ?limit (≤500) and ?offset page through it.")
        .WithTags("History")
        .Produces<IReadOnlyList<PipelineRunSummary>>()
        .RequirePermission(Permission.ViewWork);

        app.MapGet("/runs/{id:guid}", async (Guid id, IPipelineRunRepository repo, CancellationToken ct) =>
        {
            var run = await repo.GetAsync(id, ct).ConfigureAwait(false);
            return run is null
                ? Microsoft.AspNetCore.Http.Results.NotFound()
                : Microsoft.AspNetCore.Http.Results.Ok(run);
        })
        .WithName("RunById")
        .WithSummary("Details of a single pipeline run (full artifact + metrics)")
        .WithTags("History")
        .Produces<PipelineRunRecord>()
        .Produces(StatusCodes.Status404NotFound)
        .RequirePermission(Permission.ViewWork);

        return app;
    }

    /// <summary>Historical default page size for <c>GET /runs</c> when the caller passes no limit.</summary>
    private const int DefaultRunsPageSize = 50;
}

/// <summary>The per-agent budget gate. The direct-agent endpoints (<c>/requirement</c>, <c>/code</c>,
/// <c>/test</c>, <c>/qa</c>) run real billed LLM work but — unlike <c>/pipeline</c>, which goes through the
/// gated orchestrator — invoke an agent directly. Without this check an authenticated, over-cap tenant
/// could drive unbounded billed calls one agent at a time. Each endpoint calls this before invoking.</summary>
internal static class PipelineBudgetGate
{
    /// <summary>Returns <c>null</c> when the run may proceed, or a <c>402 Payment Required</c> problem when
    /// the tenant is over an <b>enforced</b> cap — the same block condition the orchestrator uses
    /// (<c>State == Exceeded &amp;&amp; EnforceOn</c>). An unset cap (the standalone / no-DB default) never
    /// blocks, so this is a no-op until a tenant configures + enforces a budget.</summary>
    public static async Task<IResult?> BlockIfExceededAsync(
        IBudgetGuard budget, ITenantContext tenant, CancellationToken ct)
    {
        System.ArgumentNullException.ThrowIfNull(budget);
        System.ArgumentNullException.ThrowIfNull(tenant);

        var status = await budget.EvaluateAsync(tenant.TenantId, ct).ConfigureAwait(false);
        if (!status.IsBlocking)
        {
            return null;
        }

        return Microsoft.AspNetCore.Http.Results.Problem(
            detail: $"Monthly LLM budget reached: spent ${status.SpentUsd:0.00} of the ${status.CapUsd:0.00} cap. "
                + "Raise the cap or turn off enforcement in the Cost app to continue.",
            statusCode: StatusCodes.Status402PaymentRequired,
            title: "LLM budget reached");
    }
}

/// <summary>Field-level request validation for the pipeline endpoints. Returns <c>null</c> when the
/// body is valid; otherwise a field → messages map for <c>Results.ValidationProblem</c>. Mirrors the
/// Domain records' <c>Validate()</c> rules so a bad body is a 400 at the boundary, never an
/// <see cref="System.ArgumentException"/> inside an agent.</summary>
internal static class PipelineRequestValidation
{
    public static Dictionary<string, string[]>? ForStory(UserStory? body)
    {
        if (body is null)
        {
            return RequestBodyRequired();
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(body.Description))
        {
            errors["description"] = ["A non-empty user story description is required."];
        }
        if (body.NMax is < 1 or > 10)
        {
            errors["nMax"] = ["NMax must be between 1 and 10."];
        }
        if (string.IsNullOrWhiteSpace(body.Locale))
        {
            errors["locale"] = ["Locale must be a non-empty language code (e.g. \"en-US\")."];
        }
        return errors.Count > 0 ? errors : null;
    }

    public static Dictionary<string, string[]>? ForCode(CodeRequest? body)
    {
        if (body is null)
        {
            return RequestBodyRequired();
        }

        var errors = new Dictionary<string, string[]>();
        if (body.Spec is null)
        {
            errors["spec"] = ["A requirement spec is required."];
        }
        return errors.Count > 0 ? errors : null;
    }

    public static Dictionary<string, string[]>? ForTest(TestRequest? body)
    {
        if (body is null)
        {
            return RequestBodyRequired();
        }

        var errors = new Dictionary<string, string[]>();
        if (body.Spec is null)
        {
            errors["spec"] = ["A requirement spec is required."];
        }
        if (body.Code is null)
        {
            errors["code"] = ["A code artifact is required."];
        }
        return errors.Count > 0 ? errors : null;
    }

    public static Dictionary<string, string[]>? ForQa(QaRequest? body)
    {
        if (body is null)
        {
            return RequestBodyRequired();
        }

        var errors = new Dictionary<string, string[]>();
        if (body.Spec is null)
        {
            errors["spec"] = ["A requirement spec is required."];
        }
        if (body.Code is null)
        {
            errors["code"] = ["A code artifact is required."];
        }
        if (body.Tests is null)
        {
            errors["tests"] = ["A test artifact is required."];
        }
        return errors.Count > 0 ? errors : null;
    }

    private static Dictionary<string, string[]> RequestBodyRequired() =>
        new() { [""] = ["A JSON request body is required."] };
}

/// <summary>Body for <c>POST /code</c>.</summary>
public sealed record CodeRequest(RequirementSpec Spec, QaReport? PreviousFeedback = null);

/// <summary>Body for <c>POST /test</c>.</summary>
public sealed record TestRequest(RequirementSpec Spec, CodeArtifact Code, QaReport? PreviousFeedback = null);

/// <summary>Body for <c>POST /qa</c>.</summary>
public sealed record QaRequest(RequirementSpec Spec, CodeArtifact Code, TestArtifact Tests);
