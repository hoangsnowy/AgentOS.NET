// Agentic Testing — the Testing agent. It does what a reviewer did by hand on the lab: check out a proposed change,
// build it, run its tests, check each acceptance criterion the way the criterion says (bringing the app up for an
// end-to-end check), and report evidence. It runs on the member's paired computer (it needs a real machine: a
// browser, containers, the repo's own toolchain) under scoped autonomy, and never edits, commits or pushes.
// Its profile (Settings → Agent profiles, role "Acceptance" — "Testing (tickets)"), its prompt (Settings → Prompts) and
// the budget gate apply like every other agent's; the seed keeps it on the member's computer (0 server tokens).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Memory;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Pipeline.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <inheritdoc cref="IAcceptanceAgent"/>
public sealed class AcceptanceAgent(ILlmClientFactory factory, ILogger<AcceptanceAgent> logger, ISessionRunFeed? feed = null,
    IOptions<AgentsOptions>? options = null, IAgentProfiles? profiles = null, IPromptOverrides? prompts = null,
    IBudgetGuard? budgetGuard = null, IMemoryStore? memory = null) : IAcceptanceAgent
{
    // Building, starting a stack and running end-to-end specs takes longer than writing the change.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    public async Task<AcceptanceVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Repo.ExistingBranch))
        {
            return NotRun("There is no proposed change (branch) to test.");
        }
        var repoLabel = $"{request.Repo.Owner}/{request.Repo.Repo}";

        // Live profile (Settings → Agent profiles), resolved per call.
        var profile = await BoardAgent.ProfileAsync(profiles, options, AgentRoles.Acceptance, ct).ConfigureAwait(false);
        ILlmClient llm;
        try { llm = factory.Create(profile.Provider); }
        catch (Exception ex) when (ex is LlmException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return NotRun($"No computer to test on: {ex.Message}");
        }
        var provider = AgentBudgetGate.EffectiveProvider(llm, profile.Provider);
        if (await AgentBudgetGate.BlockedAsync(budgetGuard, request.TenantId, provider, logger, nameof(AcceptanceAgent), ct).ConfigureAwait(false) is not null)
        {
            return NotRun(AgentBudgetGate.BlockedMessage);
        }
        Emit(request, SessionRunEventKind.Running, AgentBudgetGate.RunsOnMembersComputer(provider)
            ? $"Testing #{request.IssueNumber} on your computer."
            : $"Testing #{request.IssueNumber} ({provider}).", repoLabel);

        var llmRequest = new LlmRequest(
            SystemPrompt: await BoardAgent.SystemPromptAsync(prompts, AgentRoles.Acceptance, InstructionsTemplate, Values(request),
                UntrustedText.SystemRule, ct).ConfigureAwait(false),
            UserPrompt: User(request)
                + await ProjectPractices.BlockAsync(memory, request.TenantId, [repoLabel], logger, ct).ConfigureAwait(false),
            Model: profile.Model,
            Temperature: profile.Temperature,
            MaxTokens: profile.MaxTokens,
            Timeout: RunTimeout,
            Cli: request.CliProfile)
        {
            AgentRole = AgentRoles.Acceptance,
            Autonomy = WorkspaceAutonomy.Scoped,
            Progress = new InlineProgress<string>(step =>
            {
                if (TaskSteps.LabelFor(step) is { } label) { Emit(request, SessionRunEventKind.Step, "Testing — " + label, repoLabel); }
            }),
        };
        try
        {
            var response = await llm.SendAsync(llmRequest, ct).ConfigureAwait(false);
            return Parse(response.Content ?? string.Empty);
        }
        catch (Exception ex) when (ex is LlmException or TimeoutException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            logger.LogWarning(ex, "Acceptance run failed for session {SessionId}", request.SessionId);
            return NotRun(ex.Message);
        }
    }

    private static AcceptanceVerdict NotRun(string why) => new(false, false, [], why, why);

    private void Emit(AcceptanceRequest r, SessionRunEventKind kind, string message, string repo) =>
        feed?.Publish(new SessionRunEvent(r.TenantId, r.SessionId, kind, message, DateTimeOffset.UtcNow, repo));

    /// <summary>The placeholders <see cref="InstructionsTemplate"/> (and an override of it) may use.</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["owner", "repo", "branch", "defaultBranch"];

    /// <summary>The per-run values of <see cref="Placeholders"/>.</summary>
    internal static Dictionary<string, string> Values(AcceptanceRequest r) => new(StringComparer.Ordinal)
    {
        ["owner"] = r.Repo.Owner,
        ["repo"] = r.Repo.Repo,
        ["branch"] = r.Repo.ExistingBranch ?? string.Empty,
        ["defaultBranch"] = r.Repo.DefaultBranch,
    };

    /// <summary>The system prompt for one run (the built-in template, filled).</summary>
    internal static string Instructions(AcceptanceRequest r) => PromptTemplate.Fill(InstructionsTemplate, Values(r));

    /// <summary>The built-in system prompt template (its first line is the agent's routing key).</summary>
    public const string InstructionsTemplate =
        """
        You are the Testing Agent of AgentOS.NET. You ACCEPT or REJECT a proposed change against its ticket's
        acceptance criteria, on the developer's own machine, with your native shell and file tools. You NEVER change
        the code: do not edit tracked files, do not commit, do not push, do not open or comment on pull requests.

        1. Get the change. The runner starts you in a fresh, empty directory made for this run:
           git clone https://github.com/{owner}/{repo}.git .
           git fetch origin {branch}
           git checkout {branch}
           (default branch: {defaultBranch})
        2. Read the ticket (user message), the repository's agent instructions (AGENTS.md, CLAUDE.md, README) and
           docs/ACCEPTANCE.md if present — criterion IDs such as F-4 or LIB-1 refer to it.
        3. Build and run the unit tests the way the repository does (e.g. `dotnet build` + `dotnet test`,
           `npm ci` + `npm test` in each web folder).
        4. Check EVERY acceptance criterion of the ticket the way the criterion says — a Playwright spec, an
           integration test, a data check, a measurement. When a criterion needs the running app, start the
           repository's documented local stack in the background (e.g. `dotnet run --project infra/<Name>.AppHost`),
           wait until its web endpoint answers, run the relevant end-to-end specs (`npx playwright test <spec>`),
           then stop the stack gracefully (Ctrl+C) so it removes its own containers. Never delete Docker volumes,
           images or any data outside this directory.
        5. A criterion you cannot check here — a data set or secret that is not available, an external service —
           is "not-checked" with the reason. Never report "pass" for something you did not run.
        6. When a test fails, check whether it already fails on the default branch: `git worktree add ../base origin/{defaultBranch}`,
           run the same test there. If it fails the same way, it is a pre-existing failure, not this change's:
           list it under "baseFailures" and do not let it fail the verdict.

        Reply ONLY with this JSON (no other text):
        {"verdict":"pass","criteria":[{"criterion":"<ID or the criterion's words>","result":"pass","evidence":"<command and the key output line, or why it was not checked>"}],"baseFailures":[{"test":"<fully qualified test name>","evidence":"<the failure, and that it fails the same on {defaultBranch}>"}],"summary":"<one or two sentences>"}
        "verdict" is "pass" only when the build passed, every failing test is a pre-existing failure, and no criterion
        failed; otherwise "fail".
        "result" is one of "pass", "fail", "not-checked".
        """;

    internal static string User(AcceptanceRequest r) => string.Format(
        CultureInfo.InvariantCulture,
        """
        Repository: {0}/{1}  — proposed change on branch `{2}` for ticket #{3}.

        Ticket #{3} — title:
        {4}

        Ticket #{3} — description and acceptance criteria:
        {5}

        Test the change against these criteria and reply with the JSON verdict.
        """,
        r.Repo.Owner, r.Repo.Repo, r.Repo.ExistingBranch, r.IssueNumber,
        UntrustedText.Wrap("ticket-title", r.IssueTitle, maxChars: 512),
        string.IsNullOrWhiteSpace(r.IssueBody) ? "(no description provided)" : UntrustedText.Wrap("ticket-body", r.IssueBody));

    /// <summary>Reads the agent's JSON verdict (the last JSON object in the reply). A pass is only trusted when no
    /// criterion failed — a "pass" verdict with a failing criterion is a fail.</summary>
    internal static AcceptanceVerdict Parse(string content)
    {
        var start = content.LastIndexOf("{\"verdict\"", StringComparison.Ordinal);
        if (start < 0) { start = content.IndexOf('{', StringComparison.Ordinal); }
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) { return NotRun("The Testing agent did not return a verdict."); }
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            var criteria = root.TryGetProperty("criteria", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.EnumerateArray().Select(c => new AcceptanceCriterion(
                    Str(c, "criterion"), Str(c, "result").ToLowerInvariant() is "pass" or "fail" or "not-checked" ? Str(c, "result").ToLowerInvariant() : "not-checked",
                    Str(c, "evidence"))).ToList()
                : [];
            var claimed = string.Equals(Str(root, "verdict"), "pass", StringComparison.OrdinalIgnoreCase);
            var passed = claimed && criteria.All(c => c.Result != "fail");
            var baseFailures = root.TryGetProperty("baseFailures", out var bf) && bf.ValueKind == JsonValueKind.Array
                ? bf.EnumerateArray().Select(b => new AcceptanceCriterion(Str(b, "test"), "fail", Str(b, "evidence")))
                    .Where(b => b.Criterion.Length > 0).ToList()
                : [];
            return new AcceptanceVerdict(true, passed, criteria, Str(root, "summary"), null) { BaseFailures = baseFailures };
        }
        catch (JsonException)
        {
            return NotRun("The Testing agent's verdict was not valid JSON.");
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>The verdict as a pull-request comment (Markdown), evidence per criterion.</summary>
    public static string Comment(int issueNumber, AcceptanceVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var sb = new System.Text.StringBuilder();
        sb.Append(verdict.Passed ? "### ✅ AgentOS.NET acceptance — passed" : "### ❌ AgentOS.NET acceptance — failed")
          .Append(" (#").Append(issueNumber.ToString(CultureInfo.InvariantCulture)).Append(")\n\n");
        if (!string.IsNullOrWhiteSpace(verdict.Summary)) { sb.Append(verdict.Summary.Trim()).Append("\n\n"); }
        if (verdict.Criteria.Count > 0)
        {
            sb.Append("| Criterion | Result | Evidence |\n|---|---|---|\n");
            foreach (var c in verdict.Criteria)
            {
                var icon = c.Result switch { "pass" => "✅ pass", "fail" => "❌ fail", _ => "➖ not checked" };
                sb.Append("| ").Append(Cell(c.Criterion)).Append(" | ").Append(icon).Append(" | ").Append(Cell(c.Evidence)).Append(" |\n");
            }
        }
        if (verdict.BaseFailures.Count > 0)
        {
            sb.Append("\n**Already failing on the base branch** (not caused by this change — a separate fix ticket is opened):\n");
            foreach (var b in verdict.BaseFailures) { sb.Append("- `").Append(Cell(b.Criterion)).Append("` — ").Append(Cell(b.Evidence)).Append('\n'); }
        }
        sb.Append("\n<sub>Run by the AgentOS.NET Testing agent on a paired computer. It does not change the code.</sub>\n");
        return sb.ToString();

        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }

    /// <summary>The findings to send back to the Coding agent.</summary>
    public static string Findings(AcceptanceVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var failed = verdict.Failures.ToList();
        var lines = failed.Count == 0
            ? [verdict.Summary]
            : failed.Select(c => $"- {c.Criterion}: {c.Evidence}").Prepend(verdict.Summary);
        return "Acceptance testing failed. Make these criteria pass (fix the code, or the test only if the test itself is wrong):\n"
            + string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }
}
