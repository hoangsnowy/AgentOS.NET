// A keyless, deterministic ILlmClient for the offline / standalone-dev path. It NEVER calls a provider:
// it inspects the request's system prompt (whose opening line "You are the <X> Agent …" is the routing key
// the pipeline already relies on) and returns a canned, SCHEMA-VALID JSON payload for each specialist agent
// (Requirement / Coding / Testing / QA), or a short text echo for raw LLM / decision nodes. This lets the
// whole Workflow studio + 5-agent pipeline actually RUN with no API key (demo, E2E, first-launch), while the
// real providers still win whenever a key is configured — the factory only fails over to this client when a
// real provider throws LlmException (e.g. "no API key"). See LlmClientFactory + LlmOptions.OfflineFallback.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;

namespace AgentOs.Modules.Llm;

/// <summary>Deterministic, keyless <see cref="ILlmClient"/> that returns canned schema-valid output so the
/// pipeline + Workflow studio run with no provider key. Selected as a failover by <c>LlmClientFactory</c>
/// when <c>Llm:OfflineFallback</c> is enabled and the real provider has no key.</summary>
public sealed class OfflineLlmClient : ILlmClient
{
    /// <summary>Canonical provider key this client registers under.</summary>
    public const string ProviderName = LlmProviders.Offline;

    /// <summary>
    /// True when <paramref name="answeredBy"/> is this canned client standing in for a DIFFERENT provider
    /// the caller actually asked for — i.e. the real provider failed and the offline safety net answered.
    /// <para>
    /// Callers that report success to a human must consult this. The gateway appends Offline to the end of
    /// every failover chain when <c>Llm:OfflineFallback</c> is on, so a call can "succeed" with canned text
    /// while nothing reached a model. A connectivity probe that renders that as a green OK is telling the
    /// operator the opposite of the truth — which is worse than the failure it hides.
    /// </para>
    /// </summary>
    public static bool IsSubstituteFor(string? requestedProvider, string? answeredBy) =>
        LlmProviders.IsOffline(answeredBy)
        && !string.Equals(requestedProvider, ProviderName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Provider => ProviderName;

    /// <inheritdoc />
    public Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var content = Render(request);
        // Rough, deterministic token estimate (≈4 chars/token) — keeps the cost/metric columns non-zero in
        // the UI without pretending to be a billed call. Offline is always $0.
        var inTok = Estimate(request.SystemPrompt) + Estimate(request.UserPrompt);
        var outTok = Estimate(content);
        var response = new LlmResponse(content, inTok, outTok, 0m, TimeSpan.Zero, request.Model, ProviderName);
        return Task.FromResult(response);
    }

    // Route to the canned payload for the agent behind this request. Prefer the out-of-band AgentRole hint
    // (set by LlmAgentBase from its fixed PromptKey) so routing survives a tenant prompt override; fall back
    // to the system prompt's OPENING line ("You are the <X> Agent …" — the documented routing key) for callers
    // that don't set the hint. Match the full opening phrase, not a bare mention, so a prompt that references
    // another agent in its body (e.g. Testing's "code produced by the Coding Agent") is not mis-routed.
    private static string Render(LlmRequest request)
    {
        switch (request.AgentRole)
        {
            case "Requirement": return RequirementJson(request.UserPrompt);
            case "Coding": return CodeJson();
            case "Testing": return TestJson();
            case "Qa": return QaJson();
            default: break;
        }

        var sys = request.SystemPrompt ?? string.Empty;
        if (sys.Contains("You are the Requirement Agent", StringComparison.OrdinalIgnoreCase))
        {
            return RequirementJson(request.UserPrompt);
        }
        if (sys.Contains("You are the Coding Agent", StringComparison.OrdinalIgnoreCase))
        {
            return CodeJson();
        }
        if (sys.Contains("You are the Testing Agent", StringComparison.OrdinalIgnoreCase))
        {
            return TestJson();
        }
        if (sys.Contains("You are the QA Agent", StringComparison.OrdinalIgnoreCase))
        {
            return QaJson();
        }
        // Raw LLM / decision / unknown nodes: a short, deterministic echo. Decision nodes fall back to their
        // first declared route when this doesn't match a route (see GraphExecutor.RunDecisionAsync).
        return "[offline] " + Truncate(Collapse(request.UserPrompt), 280);
    }

    // Canned payloads are stored as raw JSON literals (not anonymous-object serialization) so they're
    // computed once with no per-call array allocation, and the camelCase keys exactly match each agent's
    // schema (requirement-spec.v1 / code-artifact.v1 / test-artifact.v1) and DTO contract.
    private static string RequirementJson(string userPrompt)
    {
        // JSON-escape the echo so an arbitrary user story (quotes, newlines) can't break the payload.
        var summary = JsonSerializer.Serialize(
            "Generated offline (no LLM key configured) for: " + Truncate(Collapse(userPrompt), 200));
        return $$"""
            {
              "title": "Offline demo specification",
              "summary": {{summary}},
              "stakeholders": ["End user", "Operator"],
              "functionalRequirements": ["Provide the requested capability", "Persist and retrieve state"],
              "nonFunctionalRequirements": ["Runs offline without any provider API key"],
              "entities": [{ "name": "Item", "fields": ["id", "name"], "notes": null }],
              "endpoints": [{ "method": "GET", "path": "/items", "purpose": "List items", "authRequired": false }],
              "acceptanceCriteria": [
                "Given a user story, when the pipeline runs, then a structured spec is produced",
                "The flow completes with no provider API key configured",
                "Each node reports its status to the canvas"
              ]
            }
            """;
    }

    private const string CodeJsonText = """
        {
          "projectName": "OfflineDemo",
          "architecture": "Clean Architecture",
          "files": [
            { "path": "src/Item.cs", "content": "namespace OfflineDemo;\n\npublic sealed record Item(int Id, string Name);", "language": "csharp" }
          ],
          "notes": "Generated offline (no LLM key). Configure a real provider key for genuine code generation."
        }
        """;

    private const string TestJsonText = """
        {
          "framework": "xUnit",
          "files": [
            { "path": "tests/ItemTests.cs", "content": "namespace OfflineDemo.Tests;\n\npublic sealed class ItemTests { }", "language": "csharp" }
          ],
          "happyPathCount": 2,
          "edgeCaseCount": 1,
          "errorCaseCount": 1,
          "estimatedCoveragePercent": 70
        }
        """;

    private const string QaJsonText = """
        {
          "score": 0.86,
          "isConsistent": true,
          "iterationNeeded": false,
          "issues": [],
          "recommendations": ["Add more edge-case tests once a real provider key is configured"]
        }
        """;

    private static string CodeJson() => CodeJsonText;

    private static string TestJson() => TestJsonText;

    private static string QaJson() => QaJsonText;

    private static int Estimate(string? text)
        => string.IsNullOrEmpty(text) ? 1 : Math.Max(1, text.Length / 4);

    private static string Collapse(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
