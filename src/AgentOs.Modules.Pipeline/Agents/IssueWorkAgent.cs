// M5 / multi-repo — issue-work agent. Runs the agentic LLM loop (using runner_shell) for a ticket,
// once PER target repo: explore the repo, implement the fix, build, test, commit, push a branch.
// "Server thinks, runner does" — the LLM loop runs server-side; every shell command is dispatched to
// the paired dev-machine runner via IToolGateway → runner_shell. Repos run with bounded concurrency
// (Agents:IssueWork:MaxParallelRepos, default 3) — one LLM run each, reusing the proven single-repo
// prompt + parser; each repo has its own working_dir so concurrent runs don't collide.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Cost;
using AgentOs.Domain.Pipeline;
using AgentOs.Domain.Llm;
using AgentOs.Domain.Sessions;
using AgentOs.Modules.Pipeline.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Implements a ticket fix via an agentic tool-use loop on the paired runner, per target repo.</summary>
public sealed class IssueWorkAgent : IIssueWorkAgent
{
    // A single agentic CLI run on the member's machine (clone → build → test → push) far outlasts the
    // RemoteAgent default 120s dispatch cap, so CLI-mode runs get a generous deadline.
    private static readonly TimeSpan CliRunTimeout = TimeSpan.FromMinutes(20);

    private readonly ILlmClientFactory _factory;
    private readonly AgentOptions _agentOpts;
    private readonly ILogger<IssueWorkAgent> _logger;
    private readonly ISessionRunFeed? _feed;
    private readonly IBudgetGuard? _budgetGuard;
    private readonly IAgentProfiles? _profiles;
    private readonly IRunAdmission? _admission;
    private readonly Prompts.IPromptOverrides? _prompts;
    private readonly AgentOs.Domain.Memory.IMemoryStore? _memory;

    public IssueWorkAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILogger<IssueWorkAgent> logger,
        ISessionRunFeed? feed = null,
        IBudgetGuard? budgetGuard = null,
        IAgentProfiles? profiles = null,
        IRunAdmission? admission = null,
        Prompts.IPromptOverrides? prompts = null,
        AgentOs.Domain.Memory.IMemoryStore? memory = null)
    {
        _memory = memory;
        _admission = admission;
        _prompts = prompts;
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        // Resolve the LLM client lazily in RunAsync — NOT here. This agent is injected into the Board
        // desktop app, so it is constructed eagerly when that window opens. Resolving a provider client
        // in the ctor builds a pooled client (which throws when no API key is configured), and that
        // exception would crash the Blazor circuit on open. The ctor must stay side-effect-free.
        _factory = factory;
        _agentOpts = options.Value.IssueWork;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Optional: the live progress feed (null in unit tests / hosts that don't register it).
        _feed = feed;
        // S1 — optional per-tenant budget gate. Null on the no-DB standalone path / unit tests, where
        // spend is always 0 and the gate would be a no-op anyway. Storing the ref is side-effect-free.
        _budgetGuard = budgetGuard;
        // Live profile (Settings → Agent profiles): resolved per run, never frozen here.
        _profiles = profiles;
    }

    // Best-effort publish to the live session-run feed — never throws, no-op when unsubscribed.
    private void Emit(IssueWorkRequest req, SessionRunEventKind kind, string message, string? repo = null) =>
        _feed?.Publish(new SessionRunEvent(req.TenantId, req.SessionId, kind, message, DateTimeOffset.UtcNow, repo));

    // S1 — returns a fully-failed IssueWorkResult when the tenant is over its ENFORCED monthly LLM
    // budget and the run would spend server tokens on effectiveProvider, else null (the run proceeds).
    // Fail-open on a transient store error (see AgentBudgetGate).
    private async Task<IssueWorkResult?> BudgetBlockedAsync(IssueWorkRequest request, string effectiveProvider, CancellationToken ct)
    {
        if (await AgentBudgetGate.BlockedAsync(_budgetGuard, request.TenantId, effectiveProvider, _logger, nameof(IssueWorkAgent), ct)
                .ConfigureAwait(false) is not { } budget)
        {
            return null;
        }

        Emit(request, SessionRunEventKind.Step,
            $"Blocked — monthly LLM budget exceeded (${budget.SpentUsd:0.##} of ${budget.CapUsd:0.##}). " +
            "Raise the cap in Settings, or use Run-on-machine (0 server tokens).");

        var failed = request.Repos
            .Select(r => new RepoWorkOutcome(r.SessionRepoId, false, r.Owner, r.Repo, "", "", "Monthly LLM budget exceeded."))
            .ToList();
        return new IssueWorkResult(false, failed, "Monthly LLM budget exceeded — run blocked by the budget gate.");
    }

    /// <inheritdoc />
    public async Task<IssueWorkResult> RunAsync(IssueWorkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Repos.Count == 0)
        {
            return new IssueWorkResult(false, Array.Empty<RepoWorkOutcome>(), "No target repositories for this session.");
        }

        _logger.LogInformation(
            "IssueWorkAgent: starting session {SessionId} for ticket #{IssueNumber} across {RepoCount} repo(s)",
            request.SessionId, request.IssueNumber, request.Repos.Count);

        // RemoteAgent provider = route the WHOLE agentic loop to the member's paired dev-machine CLI
        // (claude-code / codex). That CLI has its own native tools (no runner_shell), so we switch to
        // the CLI-mode prompt + a long dispatch timeout, and the run costs zero server tokens.
        var opts = _profiles is null
            ? _agentOpts
            : await _profiles.ResolveAsync(AgentRoles.IssueWork, _agentOpts, ct).ConfigureAwait(false);
        var providerName = request.ProviderOverride ?? opts.Provider;

        ILlmClient llm;
        try
        {
            llm = _factory.Create(providerName);
        }
        catch (LlmException ex) { return LlmInitFailed(ex); }
        catch (ArgumentException ex) { return LlmInitFailed(ex); }
        catch (NotSupportedException ex) { return LlmInitFailed(ex); }
        catch (InvalidOperationException ex) { return LlmInitFailed(ex); }

        // Client init failed — no repo could run. Report each as failed so the caller surfaces it per repo.
        IssueWorkResult LlmInitFailed(Exception ex)
        {
            _logger.LogError(ex, "IssueWorkAgent: LLM client init failed for session {SessionId}", request.SessionId);
            Emit(request, SessionRunEventKind.Step, $"LLM client init failed: {ex.Message}");
            var failed = request.Repos
                .Select(r => new RepoWorkOutcome(r.SessionRepoId, false, r.Owner, r.Repo, "", "", $"LLM client init failed: {ex.Message}"))
                .ToList();
            return new IssueWorkResult(false, failed, $"LLM client init failed: {ex.Message}");
        }

        // CLI mode and the budget gate follow the EFFECTIVE provider (after Settings' force-provider), not the name asked for.
        var effective = AgentBudgetGate.EffectiveProvider(llm, providerName);
        var cliMode = AgentBudgetGate.RunsOnMembersComputer(effective);

        // S1 — server-token budget gate. Server-side runs spend AgentOS.NET's OWN LLM tokens, so they pass
        // the per-tenant budget gate before the expensive agentic loop (the gap the run-level pipeline
        // gate in PersistingOrchestratorAgent never covered for this path). CLI/RemoteAgent runs execute
        // on the member's paired machine and cost ZERO server tokens, so they are exempt.
        if (await BudgetBlockedAsync(request, effective, ct).ConfigureAwait(false) is { } blocked)
        {
            return blocked;
        }

        // E0.6: per-tenant admission (concurrency + burst) — applies to CLI runs too (they hold a runner + a session).
        using var lease = _admission is null ? null : await _admission.AdmitAsync(request.TenantId, ct).ConfigureAwait(false);
        if (lease is { Admitted: false })
        {
            Emit(request, SessionRunEventKind.Step, "Blocked — " + lease.Reason);
            return new IssueWorkResult(false,
                request.Repos.Select(r => new RepoWorkOutcome(r.SessionRepoId, false, r.Owner, r.Repo, "", "", lease.Reason)).ToList(),
                lease.Reason);
        }

        Emit(request, SessionRunEventKind.Running, cliMode
            ? $"Running on your machine's CLI — {request.Repos.Count} repo(s), 0 server tokens."
            : $"Dispatched to runner — {request.Repos.Count} repo(s).");

        // Run repos with bounded concurrency. Each repo is an independent LLM run with its own
        // working_dir on the runner, so they don't collide; the broker dispatches per-call (GUID
        // ToolCallId) so concurrent runner_shell calls are safe. A semaphore caps fan-out to keep the
        // dev machine sane; each task writes only its own slot in the outcomes array (order-stable,
        // no shared-state contention). MaxParallelRepos=1 reproduces the old sequential behaviour.
        var maxParallel = Math.Max(1, opts.MaxParallelRepos);
        var outcomes = new RepoWorkOutcome[request.Repos.Count];
        using var gate = new SemaphoreSlim(maxParallel, maxParallel);

        async Task RunRepoSlotAsync(int index)
        {
            var repo = request.Repos[index];
            var repoLabel = $"{repo.Owner}/{repo.Repo}";
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Emit(request, SessionRunEventKind.RepoStarted, $"{repoLabel} — implementing…", repoLabel);
                var outcome = await RunOneAsync(llm, opts, request, repo, cliMode, ct).ConfigureAwait(false);
                Emit(request, SessionRunEventKind.Step,
                    outcome.Ok
                        ? $"{repoLabel} — pushed branch {outcome.BranchName}"
                        : $"{repoLabel} — agent failed: {outcome.Error}",
                    repoLabel);
                outcomes[index] = outcome;
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, request.Repos.Count).Select(RunRepoSlotAsync)).ConfigureAwait(false);

        var ok = outcomes.All(o => o.Ok);
        return new IssueWorkResult(ok, outcomes, ok ? null : "One or more repositories failed — see per-repo errors.");
    }

    // Best-effort and bounded: six lessons at most, a memory failure is no lessons.
    private async Task<string> RecallLessonsAsync(IssueWorkRequest request, WorkRepo repo, CancellationToken ct)
    {
        if (_memory is null) { return string.Empty; }
        try
        {
            var repoKey = $"{repo.Owner}/{repo.Repo}";
            // The project's practices (what the Retrospective agent distilled) first, then the raw lessons closest to this ticket.
            var practices = await _memory.ListAsync(request.TenantId, repoKey, AgentOs.Domain.Memory.MemoryKinds.Practice, AgentOs.Modules.Pipeline.Agents.RetrospectiveAgent.MaxPractices, ct).ConfigureAwait(false);
            var found = await _memory.RecallLessonsAsync(request.TenantId, repoKey, request.IssueTitle, 6, ct).ConfigureAwait(false);
            if (found.Count > 0)
            {
                Emit(request, SessionRunEventKind.Step, $"Remembered {found.Count} lesson(s) from earlier work on {repo.Owner}/{repo.Repo}.", $"{repo.Owner}/{repo.Repo}");
            }
            return AgentOs.Domain.Memory.ProjectMemory.PracticesBlock(practices) + AgentOs.Domain.Memory.ProjectMemory.PromptBlock(found);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IssueWorkAgent: lesson recall failed for {Owner}/{Repo}", repo.Owner, repo.Repo);
            return string.Empty;
        }
    }

    private async Task<RepoWorkOutcome> RunOneAsync(ILlmClient llm, AgentOptions opts, IssueWorkRequest request, WorkRepo repo, bool cliMode, CancellationToken ct)
    {
        // Server-side (default): the LLM drives the paired runner via the runner_shell tool loop.
        // CLI mode (RemoteAgent): the dev-machine CLI has its own tools — send the CLI-mode prompt,
        // expose no server tools, and grant a long timeout for the full clone→build→test→push run.
        // E1.6: the tenant's workspace rules apply to the Quick engine too (server and CLI mode).
        var rules = _prompts is null ? string.Empty : WorkspaceRules.ToPromptBlock(await _prompts.ResolveRulesAsync(ct).ConfigureAwait(false));
        // Settings → Prompts: the tenant's override of this mode's template (or the built-in one), filled per repo.
        var template = _prompts is null
            ? null
            : cliMode
                ? await _prompts.ResolveAsync(AgentRoles.IssueWork, IssueWorkPrompt.CliTemplate, ct).ConfigureAwait(false)
                : await _prompts.ResolveAsync(IssueWorkPrompt.ServerPromptKey, IssueWorkPrompt.ServerTemplate, ct).ConfigureAwait(false);
        // Knowledge graph: the lessons earlier work on this repo left behind (reviews, failing checks, acceptance tests).
        var lessons = await RecallLessonsAsync(request, repo, ct).ConfigureAwait(false);
        var req = cliMode
            ? new LlmRequest(
                SystemPrompt: IssueWorkPrompt.SystemCli(request, repo, template) + rules,
                UserPrompt: IssueWorkPrompt.User(request, repo) + lessons,
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: [],
                Timeout: CliRunTimeout,
                Cli: request.CliProfile)
            {
                AgentRole = AgentRoles.IssueWork,
                // The only request that asks the runner for hands: clone → edit → build → test → push in its own
                // per-run directory. Every other remote call stays answer-only.
                Autonomy = WorkspaceAutonomy.Scoped,
                // What the CLI is doing right now, in plain words, on the task's live feed.
                Progress = new InlineProgress<string>(step =>
                {
                    if (TaskSteps.LabelFor(step) is { } label)
                    {
                        Emit(request, SessionRunEventKind.Step, label, $"{repo.Owner}/{repo.Repo}");
                    }
                }),
            }
            : new LlmRequest(
                SystemPrompt: IssueWorkPrompt.System(request, repo, template) + rules,
                UserPrompt: IssueWorkPrompt.User(request, repo) + lessons,
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: ["runner_shell"])
            {
                AgentRole = AgentRoles.IssueWork,
            };

        try
        {
            var response = await llm.SendAsync(req, ct).ConfigureAwait(false);
            return ParseOutcome(response.Content ?? "", request, repo);
        }
        catch (LlmException ex) { return LlmCallFailed(ex); }
        catch (System.Net.Http.HttpRequestException ex) { return LlmCallFailed(ex); }
        catch (System.Text.Json.JsonException ex) { return LlmCallFailed(ex); }
        catch (TimeoutException ex) { return LlmCallFailed(ex); }
        catch (IOException ex) { return LlmCallFailed(ex); }
        catch (InvalidOperationException ex) { return LlmCallFailed(ex); }

        RepoWorkOutcome LlmCallFailed(Exception ex)
        {
            _logger.LogError(ex, "IssueWorkAgent: LLM call failed for session {SessionId} repo {Owner}/{Repo}",
                request.SessionId, repo.Owner, repo.Repo);
            return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, "", "", $"LLM call failed: {ex.Message}");
        }
    }

    private RepoWorkOutcome ParseOutcome(string content, IssueWorkRequest request, WorkRepo repo)
    {
        // The agent may wrap the JSON in a markdown fence or emit reasoning around it. JsonExtractor
        // handles direct / fenced / brace-spanned forms; the old LastIndexOf('{') hand-roll broke as
        // soon as a '{' appeared inside a JSON string value (it grabbed an inner brace fragment).
        string json;
        try
        {
            json = JsonExtractor.ExtractJson(content, nameof(IssueWorkAgent));
        }
        catch (LlmException)
        {
            _logger.LogWarning("IssueWorkAgent: no JSON found for session {SessionId} repo {Owner}/{Repo}",
                request.SessionId, repo.Owner, repo.Repo);
            return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, "",
                content.Length > 200 ? content[..200] : content, "Agent did not return a JSON summary.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var branch = root.TryGetProperty("branch", out var bProp) ? bProp.GetString() ?? "" : "";
            var summary = root.TryGetProperty("summary", out var sProp) ? sProp.GetString() ?? "" : "";
            var error = root.TryGetProperty("error", out var eProp) ? eProp.GetString() : null;

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(branch))
            {
                _logger.LogWarning("IssueWorkAgent: agent reported failure for session {SessionId} repo {Owner}/{Repo}: {Error}",
                    request.SessionId, repo.Owner, repo.Repo, error);
                return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, branch, summary,
                    error ?? "Agent did not push a branch.");
            }

            // A follow-up pushes to the pull request's existing branch — whatever name the reply claims, that is the one.
            if (!string.IsNullOrWhiteSpace(repo.ExistingBranch) && !string.IsNullOrWhiteSpace(request.ChangeRequest)) { branch = repo.ExistingBranch; }

            // The pull request description (optional; an older or overridden prompt may not ask for it).
            var description = root.TryGetProperty("description", out var dProp) && dProp.ValueKind == JsonValueKind.String
                ? dProp.GetString() : null;

            _logger.LogInformation("IssueWorkAgent: session {SessionId} repo {Owner}/{Repo} pushed branch '{Branch}'",
                request.SessionId, repo.Owner, repo.Repo, branch);
            return new RepoWorkOutcome(repo.SessionRepoId, true, repo.Owner, repo.Repo, branch, summary, null)
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "IssueWorkAgent: failed to parse JSON for session {SessionId} repo {Owner}/{Repo}",
                request.SessionId, repo.Owner, repo.Repo);
            return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, "", "", "Agent response was not valid JSON.");
        }
    }
}
