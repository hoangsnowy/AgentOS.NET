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

    public IssueWorkAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILogger<IssueWorkAgent> logger,
        ISessionRunFeed? feed = null,
        IBudgetGuard? budgetGuard = null,
        IAgentProfiles? profiles = null,
        IRunAdmission? admission = null)
    {
        _admission = admission;
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
    // budget, else null (the run proceeds). Fail-open on a transient store error: a budget-store hiccup
    // must never block a run — the cap re-applies on the next run once the store recovers.
    private async Task<IssueWorkResult?> BudgetBlockedAsync(IssueWorkRequest request, CancellationToken ct)
    {
        BudgetStatus budget;
        try
        {
            budget = await _budgetGuard!.EvaluateAsync(request.TenantId, ct).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) { return BudgetEvalFailed(ex); }
        catch (System.Data.Common.DbException ex) { return BudgetEvalFailed(ex); }
        catch (TimeoutException ex) { return BudgetEvalFailed(ex); }
        catch (IOException ex) { return BudgetEvalFailed(ex); }
        catch (InvalidOperationException ex) { return BudgetEvalFailed(ex); }

        // fail-open: a transient budget-store error must not block a run — log and proceed.
        IssueWorkResult? BudgetEvalFailed(Exception ex)
        {
            _logger.LogWarning(ex, "IssueWorkAgent: budget evaluation failed for tenant {Tenant}; proceeding.", request.TenantId);
            return null;
        }

        if (budget is not { State: BudgetState.Exceeded, EnforceOn: true })
        {
            return null;
        }

        _logger.LogWarning(
            "IssueWorkAgent: blocking session {SessionId} — tenant {Tenant} over monthly LLM budget (${Spent} of ${Cap}).",
            request.SessionId, request.TenantId, budget.SpentUsd, budget.CapUsd);
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
            : await _profiles.ResolveAsync(nameof(AgentsOptions.IssueWork), _agentOpts, ct).ConfigureAwait(false);
        var providerName = request.ProviderOverride ?? opts.Provider;
        var cliMode = providerName.Trim().ToUpperInvariant() is "REMOTEAGENT" or "REMOTE" or "IDE";

        // S1 — server-token budget gate. Server-side runs spend AgentOS's OWN LLM tokens, so they pass
        // the per-tenant budget gate before the expensive agentic loop (the gap the run-level pipeline
        // gate in PersistingOrchestratorAgent never covered for this path). CLI/RemoteAgent runs execute
        // on the member's paired machine and cost ZERO server tokens, so they are exempt.
        if (!cliMode && _budgetGuard is not null)
        {
            var blocked = await BudgetBlockedAsync(request, ct).ConfigureAwait(false);
            if (blocked is not null)
            {
                return blocked;
            }
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

    private async Task<RepoWorkOutcome> RunOneAsync(ILlmClient llm, AgentOptions opts, IssueWorkRequest request, WorkRepo repo, bool cliMode, CancellationToken ct)
    {
        // Server-side (default): the LLM drives the paired runner via the runner_shell tool loop.
        // CLI mode (RemoteAgent): the dev-machine CLI has its own tools — send the CLI-mode prompt,
        // expose no server tools, and grant a long timeout for the full clone→build→test→push run.
        var req = cliMode
            ? new LlmRequest(
                SystemPrompt: IssueWorkPrompt.SystemCli(request, repo),
                UserPrompt: IssueWorkPrompt.User(request, repo),
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: [],
                Timeout: CliRunTimeout,
                Cli: request.CliProfile)
            : new LlmRequest(
                SystemPrompt: IssueWorkPrompt.System(request, repo),
                UserPrompt: IssueWorkPrompt.User(request, repo),
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: ["runner_shell"]);

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

            _logger.LogInformation("IssueWorkAgent: session {SessionId} repo {Owner}/{Repo} pushed branch '{Branch}'",
                request.SessionId, repo.Owner, repo.Repo, branch);
            return new RepoWorkOutcome(repo.SessionRepoId, true, repo.Owner, repo.Repo, branch, summary, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "IssueWorkAgent: failed to parse JSON for session {SessionId} repo {Owner}/{Repo}",
                request.SessionId, repo.Owner, repo.Repo);
            return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, "", "", "Agent response was not valid JSON.");
        }
    }
}
