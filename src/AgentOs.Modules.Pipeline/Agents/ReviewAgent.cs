// Agentic review — the Reviewer agent. Between the Coding agent and the Testing agent: it reads the proposed change's
// diff against its ticket and the repository's own rules, the way a careful reviewer would, and either approves it or
// returns must-fix findings that go back to the Coding agent. It runs on the member's paired computer under scoped
// autonomy (it needs the checkout to read code in context and may build) and never edits, commits or pushes.
// Its profile (Settings → Agent profiles, role "Reviewer"), its prompt (Settings → Prompts) and the budget gate apply
// like every other agent's; the seed keeps it on the member's computer (0 server tokens).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Pipeline.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <inheritdoc cref="IReviewAgent"/>
public sealed class ReviewAgent(ILlmClientFactory factory, ILogger<ReviewAgent> logger, ISessionRunFeed? feed = null,
    AgentOs.Domain.Memory.IMemoryStore? memory = null, IOptions<AgentsOptions>? options = null, IAgentProfiles? profiles = null,
    IPromptOverrides? prompts = null, IBudgetGuard? budgetGuard = null) : IReviewAgent
{
    // Reading a diff in context is quicker than testing it; a build fits too.

    /// <inheritdoc />
    public async Task<ReviewVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Repo.ExistingBranch))
        {
            return NotRun("There is no proposed change (branch) to review.");
        }
        var repoLabel = $"{request.Repo.Owner}/{request.Repo.Repo}";

        // Live profile (Settings → Agent profiles), resolved per call.
        var profile = await BoardAgent.ProfileAsync(profiles, options, AgentRoles.Reviewer, ct).ConfigureAwait(false);
        ILlmClient llm;
        try { llm = factory.Create(profile.Provider); }
        catch (Exception ex) when (ex is LlmException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return NotRun($"No computer to review on: {ex.Message}");
        }
        var provider = AgentBudgetGate.EffectiveProvider(llm, profile.Provider);
        if (await AgentBudgetGate.BlockedAsync(budgetGuard, request.TenantId, provider, logger, nameof(ReviewAgent), ct).ConfigureAwait(false) is not null)
        {
            return NotRun(AgentBudgetGate.BlockedMessage);
        }
        Emit(request, SessionRunEventKind.Running, AgentBudgetGate.RunsOnMembersComputer(provider)
            ? $"Reviewing #{request.IssueNumber} on your computer."
            : $"Reviewing #{request.IssueNumber} ({provider}).", repoLabel);

        // Ticket files (ADR-0015) reach the run only on the member's computer — only then does the prompt mention them.
        var onComputer = AgentBudgetGate.RunsOnMembersComputer(provider);
        var llmRequest = new LlmRequest(
            SystemPrompt: await BoardAgent.SystemPromptAsync(prompts, AgentRoles.Reviewer, InstructionsTemplate, AcceptanceAgent.Values(request),
                UntrustedText.SystemRule, ct).ConfigureAwait(false),
            UserPrompt: AcceptanceAgent.User(request).Replace("Test the change against these criteria and reply with the JSON verdict.",
                "Review the change against this ticket and reply with the JSON verdict.", StringComparison.Ordinal)
                + (onComputer ? TicketFilesPrompt.ForRunDirectory(request.Files, TicketFilesPrompt.Use.Check) : string.Empty)
                + await PracticesAsync(request, ct).ConfigureAwait(false),
            Model: profile.Model,
            Temperature: profile.Temperature,
            MaxTokens: profile.MaxTokens,
            Timeout: await AutopilotSettings.RunLimitForAsync(profiles, AutopilotSettings.ReviewRunMinutesKey, ct).ConfigureAwait(false),
            Cli: request.CliProfile)
        {
            AgentRole = AgentRoles.Reviewer,
            Autonomy = WorkspaceAutonomy.Scoped,
            Files = onComputer && request.Files.Count > 0 ? request.Files : null,
            Progress = new InlineProgress<string>(step =>
            {
                if (TaskSteps.LabelFor(step) is { } label) { Emit(request, SessionRunEventKind.Step, "Reviewing — " + label, repoLabel); }
            }),
        };
        try
        {
            var response = await llm.SendAsync(llmRequest, ct).ConfigureAwait(false);
            var verdict = Parse(response.Content ?? string.Empty);
            // No verdict — usually a run that ran out of turns or context after reading the change: cheaper to ask once
            // for the verdict than to review again.
            if (verdict.Ran || !await ReplyRecovery.ShouldAskAsync(profiles, response, ct).ConfigureAwait(false)) { return verdict; }
            Emit(request, SessionRunEventKind.Step, "Reviewing — the run ended without its verdict; asking the agent once for it.", repoLabel);
            var answer = await ReplyRecovery.AskAsync(llm, ReplyRecovery.FollowUp(llmRequest, response, RecoverySystem, RecoveryAsk(request)),
                logger, nameof(ReviewAgent), ct).ConfigureAwait(false);
            var recovered = Recovered(answer.Reply, answer.Resumed, out var setAside);
            Emit(request, SessionRunEventKind.ReplyRecovered, recovered is not null
                ? $"Reviewing — verdict recovered: {(recovered.Approved ? "approved" : "changes requested")}."
                : setAside is not null
                    ? $"Reviewing — asked again, but its approval was set aside: {setAside}."
                    : "Reviewing — asked again, but the Reviewer agent still gave no verdict.", repoLabel);
            return recovered ?? NotRun(verdict.Error + $" (asked once more; {setAside ?? "still none"})" + answer.Why);
        }
        catch (Exception ex) when (ex is LlmException or TimeoutException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            logger.LogWarning(ex, "Review run failed for session {SessionId}", request.SessionId);
            return NotRun(ex.Message);
        }
    }

    /// <summary>A follow-up's reply as a verdict — only when it holds a valid one (an <c>{"error":…}</c> reply, or one
    /// whose verdict is not "approve"/"changes", means the review could not finish and stays not-run, never a silent
    /// "changes requested"). "Changes" stands as reported; an approval counts only from the run's own resumed session
    /// (<paramref name="resumed"/>) — a fresh conversation never saw the diff. A set-aside approval returns null with
    /// <paramref name="setAside"/> saying why.</summary>
    internal static ReviewVerdict? Recovered(string? reply, bool resumed, out string? setAside)
    {
        setAside = null;
        var claimed = ReplyRecovery.VerdictOf(reply, nameof(ReviewAgent), "approve", "changes");
        if (claimed is null) { return null; }
        var verdict = Parse(reply!);
        if (!verdict.Ran) { return null; }
        if (claimed == "changes" || !verdict.Approved) { return verdict with { Approved = false }; }
        if (!resumed) { setAside = ReplyRecovery.NotResumedNote; return null; }
        return verdict;
    }

    /// <summary>The system prompt of the one follow-up asking for a missing verdict (its first line is the routing key).</summary>
    internal const string RecoverySystem =
        "You are the Reviewer Agent of AgentOS.NET, finishing a review on the developer's machine that ended before it sent its verdict. You never change the code.";

    /// <summary>The follow-up's ask: the verdict of what the run already reviewed, JSON only. Names the repository and
    /// branch (from the server) and nothing else — no ticket text, files or practices again.</summary>
    internal static string RecoveryAsk(AcceptanceRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return string.Format(CultureInfo.InvariantCulture,
            """
            Your previous review of branch `{2}` on {0}/{1} (ticket #{3}) ended before you sent the JSON verdict. Do not
            start the review over and do not change anything. From what you already found, reply ONLY with this JSON (no
            other text):
            {{"verdict":"approve","findings":[{{"severity":"must-fix","file":"<path>","line":42,"comment":"<what is wrong and what to do>"}}],"summary":"<one or two sentences>"}}
            "verdict" is "approve" when there is no must-fix finding, otherwise "changes".
            If you had not reviewed enough of the change to judge it, reply ONLY: {{"error":"<why the review could not finish>"}}
            """,
            r.Repo.Owner, r.Repo.Repo, r.Repo.ExistingBranch, r.IssueNumber);
    }

    // The project's practices: a change that breaks one is a must-fix finding. Best-effort — none on a memory failure.
    private async Task<string> PracticesAsync(AcceptanceRequest request, CancellationToken ct)
    {
        var block = await ProjectPractices.BlockAsync(memory, request.TenantId, [$"{request.Repo.Owner}/{request.Repo.Repo}"], logger, ct)
            .ConfigureAwait(false);
        return block.Length == 0 ? string.Empty : block + "\nA change that breaks one of these practices is a must-fix finding.";
    }

    private static ReviewVerdict NotRun(string why) => new(false, false, [], why, why);

    private void Emit(AcceptanceRequest r, SessionRunEventKind kind, string message, string repo) =>
        feed?.Publish(new SessionRunEvent(r.TenantId, r.SessionId, kind, message, DateTimeOffset.UtcNow, repo));

    /// <summary>The system prompt for one run (the built-in template, filled).</summary>
    internal static string Instructions(AcceptanceRequest r) => PromptTemplate.Fill(InstructionsTemplate, AcceptanceAgent.Values(r));

    /// <summary>The built-in system prompt template — placeholders as <see cref="AcceptanceAgent.Placeholders"/> (its first
    /// line is the agent's routing key).</summary>
    public const string InstructionsTemplate =
        """
        You are the Reviewer Agent of AgentOS.NET. You review a proposed change before it is tested, the way a careful
        senior reviewer would on its pull request, on the developer's own machine with your native shell and file tools.
        You NEVER change the code: do not edit tracked files, do not commit, do not push, do not open or comment on pull
        requests.

        1. Get the change. The runner starts you in a fresh, empty directory made for this run:
           git clone https://github.com/{owner}/{repo}.git .
           git fetch origin {branch}
           git checkout {branch}
           git diff origin/{defaultBranch}...HEAD
        2. Read the ticket (user message) and the repository's rules (AGENTS.md, CLAUDE.md, README, docs it points to).
        3. Review the diff in context — open the files around each change. Look for, in this order:
           - correctness: does it do what the ticket asks; broken edge cases; wrong logic; regressions;
           - security and data isolation: secrets in code, injection, missing authorization, cross-tenant/school reads;
           - tests: is the new behaviour covered; are tests meaningful (not asserting nothing);
           - the repository's own conventions and rules (language, structure, naming) where it states them.
           You may build the project to confirm a suspicion. Do not run the full end-to-end stack — testing comes next.
        4. Grade each finding:
           - "must-fix": a real defect, security/isolation hole, missing behaviour the ticket requires, or a broken
             repository rule. Only these block the change.
           - "should-fix": a clear improvement that is not a defect.
           - "nit": style or taste. Keep these few.
           Do not invent problems; an approval with no findings is a good outcome.

        Reply ONLY with this JSON (no other text):
        {"verdict":"approve","findings":[{"severity":"must-fix","file":"<path>","line":42,"comment":"<what is wrong and what to do>"}],"summary":"<one or two sentences>"}
        "verdict" is "approve" when there is no must-fix finding, otherwise "changes".
        """;

    /// <summary>Reads the agent's JSON verdict. An approval is only trusted when no finding is must-fix.</summary>
    internal static ReviewVerdict Parse(string content)
    {
        var start = content.LastIndexOf("{\"verdict\"", StringComparison.Ordinal);
        if (start < 0) { start = content.IndexOf('{', StringComparison.Ordinal); }
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) { return NotRun("The Reviewer agent did not return a verdict."); }
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            var findings = root.TryGetProperty("findings", out var fs) && fs.ValueKind == JsonValueKind.Array
                ? fs.EnumerateArray().Select(f => new ReviewFinding(
                    Severity(Str(f, "severity")), Str(f, "file"),
                    f.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) && n > 0 ? n : null,
                    Str(f, "comment"))).Where(f => f.Comment.Length > 0).ToList()
                : [];
            var claimed = string.Equals(Str(root, "verdict"), "approve", StringComparison.OrdinalIgnoreCase);
            return new ReviewVerdict(true, claimed && !findings.Any(f => f.Blocks), findings, Str(root, "summary"), null);
        }
        catch (JsonException)
        {
            return NotRun("The Reviewer agent's verdict was not valid JSON.");
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static string Severity(string s) => s.Trim().ToLowerInvariant() switch
        {
            "must-fix" or "must" or "blocker" => "must-fix",
            "should-fix" or "should" => "should-fix",
            _ => "nit",
        };
    }

    /// <summary>The review as a pull-request comment (Markdown).</summary>
    public static string Comment(int issueNumber, ReviewVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var sb = new System.Text.StringBuilder();
        sb.Append(verdict.Approved ? "### ✅ AgentOS.NET review — approved" : "### 🔁 AgentOS.NET review — changes requested")
          .Append(" (#").Append(issueNumber.ToString(CultureInfo.InvariantCulture)).Append(")\n\n");
        if (!string.IsNullOrWhiteSpace(verdict.Summary)) { sb.Append(verdict.Summary.Trim()).Append("\n\n"); }
        if (verdict.Findings.Count > 0)
        {
            sb.Append("| Severity | Where | Finding |\n|---|---|---|\n");
            foreach (var f in verdict.Findings.OrderBy(f => f.Severity switch { "must-fix" => 0, "should-fix" => 1, _ => 2 }))
            {
                sb.Append("| ").Append(f.Severity).Append(" | `").Append(Cell(Where(f))).Append("` | ").Append(Cell(f.Comment)).Append(" |\n");
            }
        }
        sb.Append("\n<sub>Run by the AgentOS.NET Reviewer agent on a paired computer. It does not change the code; must-fix findings go back to the Coding agent.</sub>\n");
        return sb.ToString();

        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }

    /// <summary>The must-fix findings, as the change request sent back to the Coding agent.</summary>
    public static string ChangeRequest(ReviewVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var lines = verdict.Blocking.Select(f => $"- {Where(f)}: {f.Comment}").Prepend(verdict.Summary);
        return "Code review asked for changes. Fix every must-fix finding (or explain in the pull request why it is not a defect):\n"
            + string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private static string Where(ReviewFinding f) =>
        string.IsNullOrWhiteSpace(f.File) ? "(general)" : f.Line is { } line ? $"{f.File}:{line.ToString(CultureInfo.InvariantCulture)}" : f.File;
}
