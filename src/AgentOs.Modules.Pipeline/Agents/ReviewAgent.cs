// Agentic review — the Reviewer agent. Between the Coding agent and the Testing agent: it reads the proposed change's
// diff against its ticket and the repository's own rules, the way a careful reviewer would, and either approves it or
// returns must-fix findings that go back to the Coding agent. It runs on the member's paired computer under scoped
// autonomy (it needs the checkout to read code in context and may build) and never edits, commits or pushes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Pipeline.Agents;

/// <inheritdoc cref="IReviewAgent"/>
public sealed class ReviewAgent(ILlmClientFactory factory, ILogger<ReviewAgent> logger, ISessionRunFeed? feed = null) : IReviewAgent
{
    // Reading a diff in context is quicker than testing it; a build fits too.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

    /// <inheritdoc />
    public async Task<ReviewVerdict> RunAsync(AcceptanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Repo.ExistingBranch))
        {
            return NotRun("There is no proposed change (branch) to review.");
        }
        var repoLabel = $"{request.Repo.Owner}/{request.Repo.Repo}";
        Emit(request, SessionRunEventKind.Running, $"Reviewing #{request.IssueNumber} on your computer.", repoLabel);

        ILlmClient llm;
        try { llm = factory.Create("RemoteAgent"); }
        catch (Exception ex) when (ex is LlmException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return NotRun($"No computer to review on: {ex.Message}");
        }

        var llmRequest = new LlmRequest(
            SystemPrompt: Instructions(request) + "\n\n" + UntrustedText.SystemRule,
            UserPrompt: AcceptanceAgent.User(request).Replace("Test the change against these criteria and reply with the JSON verdict.",
                "Review the change against this ticket and reply with the JSON verdict.", StringComparison.Ordinal),
            Model: "claude-sonnet-4-6",
            MaxTokens: 32768,
            Timeout: RunTimeout,
            Cli: request.CliProfile)
        {
            Autonomy = WorkspaceAutonomy.Scoped,
            Progress = new InlineProgress<string>(step =>
            {
                if (TaskSteps.LabelFor(step) is { } label) { Emit(request, SessionRunEventKind.Step, "Reviewing — " + label, repoLabel); }
            }),
        };
        try
        {
            var response = await llm.SendAsync(llmRequest, ct).ConfigureAwait(false);
            return Parse(response.Content ?? string.Empty);
        }
        catch (Exception ex) when (ex is LlmException or TimeoutException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            logger.LogWarning(ex, "Review run failed for session {SessionId}", request.SessionId);
            return NotRun(ex.Message);
        }
    }

    private static ReviewVerdict NotRun(string why) => new(false, false, [], why, why);

    private void Emit(AcceptanceRequest r, SessionRunEventKind kind, string message, string repo) =>
        feed?.Publish(new SessionRunEvent(r.TenantId, r.SessionId, kind, message, DateTimeOffset.UtcNow, repo));

    internal static string Instructions(AcceptanceRequest r) => string.Format(
        CultureInfo.InvariantCulture,
        """
        You are the Reviewer Agent of AgentOS.NET. You review a proposed change before it is tested, the way a careful
        senior reviewer would on its pull request, on the developer's own machine with your native shell and file tools.
        You NEVER change the code: do not edit tracked files, do not commit, do not push, do not open or comment on pull
        requests.

        1. Get the change. The runner starts you in a fresh, empty directory made for this run:
           git clone https://github.com/{0}/{1}.git .
           git fetch origin {2}
           git checkout {2}
           git diff origin/{3}...HEAD
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
        {{"verdict":"approve","findings":[{{"severity":"must-fix","file":"<path>","line":42,"comment":"<what is wrong and what to do>"}}],"summary":"<one or two sentences>"}}
        "verdict" is "approve" when there is no must-fix finding, otherwise "changes".
        """,
        r.Repo.Owner, r.Repo.Repo, r.Repo.ExistingBranch, r.Repo.DefaultBranch);

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
