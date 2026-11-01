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
using AgentOs.SharedKernel.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentOs.Modules.Pipeline.Agents;

/// <summary>Implements a ticket fix via an agentic tool-use loop on the paired runner, per target repo.</summary>
public sealed class IssueWorkAgent : IIssueWorkAgent
{
    // A single agentic CLI run on the member's machine (clone → build → test → push) far outlasts the
    // RemoteAgent default 120s dispatch cap, so CLI-mode runs get a generous deadline.

    private readonly ILlmClientFactory _factory;
    private readonly AgentOptions _agentOpts;
    private readonly ILogger<IssueWorkAgent> _logger;
    private readonly ISessionRunFeed? _feed;
    private readonly IBudgetGuard? _budgetGuard;
    private readonly IAgentProfiles? _profiles;
    private readonly IRunAdmission? _admission;
    private readonly Prompts.IPromptOverrides? _prompts;
    private readonly AgentOs.Domain.Memory.IMemoryStore? _memory;
    // Ticket files ahead of a server-driven run (ADR-0015); a run on the member's computer carries them with its dispatch.
    private readonly AgentOs.Domain.Files.IRunnerFileDelivery? _fileDelivery;

    public IssueWorkAgent(
        ILlmClientFactory factory,
        IOptions<AgentsOptions> options,
        ILogger<IssueWorkAgent> logger,
        ISessionRunFeed? feed = null,
        IBudgetGuard? budgetGuard = null,
        IAgentProfiles? profiles = null,
        IRunAdmission? admission = null,
        Prompts.IPromptOverrides? prompts = null,
        AgentOs.Domain.Memory.IMemoryStore? memory = null,
        AgentOs.Domain.Files.IRunnerFileDelivery? fileDelivery = null)
    {
        _memory = memory;
        _fileDelivery = fileDelivery;
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

        // Ticket files (ADR-0015). On the member's computer they travel with each run's dispatch; a server-driven run
        // drives the computer through shell commands, so they are written there first and the prompt names the folder.
        string? filesFolder = null;
        if (!cliMode && request.Files.Count > 0)
        {
            var delivery = _fileDelivery is null
                ? new AgentOs.Domain.Files.RunnerFileDelivery(false, null, "this server can't send files to a computer")
                : await _fileDelivery.DeliverAsync(request.TenantId, request.MemberId, request.Files, ct).ConfigureAwait(false);
            if (!delivery.Ok || delivery.Folder is null)
            {
                var why = $"Couldn't copy the files attached to this ticket to your computer: {delivery.Error}";
                Emit(request, SessionRunEventKind.Step, why);
                return new IssueWorkResult(false,
                    request.Repos.Select(r => new RepoWorkOutcome(r.SessionRepoId, false, r.Owner, r.Repo, "", "", why)).ToList(), why);
            }
            filesFolder = delivery.Folder;
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
                var outcome = await RunOneAsync(llm, opts, request, repo, cliMode, filesFolder, ct).ConfigureAwait(false);
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

    private async Task<RepoWorkOutcome> RunOneAsync(ILlmClient llm, AgentOptions opts, IssueWorkRequest request, WorkRepo repo, bool cliMode,
        string? filesFolder, CancellationToken ct)
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
        // Ticket files (ADR-0015): one short section, only when there are files and they reach the agent's computer.
        var files = cliMode
            ? TicketFilesPrompt.ForRunDirectory(request.Files, TicketFilesPrompt.Use.Change)
            : filesFolder is null ? string.Empty : TicketFilesPrompt.ForFolder(request.Files, filesFolder, TicketFilesPrompt.Use.Change);
        var req = cliMode
            ? new LlmRequest(
                SystemPrompt: IssueWorkPrompt.SystemCli(request, repo, template) + rules,
                UserPrompt: IssueWorkPrompt.User(request, repo) + files + lessons,
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: [],
                Timeout: await AutopilotSettings.RunLimitForAsync(_profiles, AutopilotSettings.CodingRunMinutesKey, ct).ConfigureAwait(false),
                Cli: request.CliProfile)
            {
                AgentRole = AgentRoles.IssueWork,
                // The only request that asks the runner for hands: clone → edit → build → test → push in its own
                // per-run directory. Every other remote call stays answer-only.
                Autonomy = WorkspaceAutonomy.Scoped,
                // The runner writes them into that directory before the CLI starts.
                Files = request.Files.Count > 0 ? request.Files : null,
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
                UserPrompt: IssueWorkPrompt.User(request, repo) + files + lessons,
                Model: opts.Model,
                Temperature: opts.Temperature,
                MaxTokens: opts.MaxTokens,
                Tools: ["runner_shell"])
            {
                AgentRole = AgentRoles.IssueWork,
            };

        try
        {
            // A revision of an existing pull request pushes onto its branch, which is always there: whether the run pushed
            // anything shows only in the head moving. Recorded now, read only if the reply has to be asked for again.
            var headBefore = cliMode && IsRevision(request, repo) ? await BranchHeadAsync(request, repo, repo.ExistingBranch!, _logger, ct).ConfigureAwait(false) : null;
            var response = await llm.SendAsync(req, ct).ConfigureAwait(false);
            var content = response.Content ?? "";
            // No JSON summary — often a run that ran out of turns or context AFTER doing the work: ask once for it.
            if (!ReplyRecovery.HasJson(content, nameof(IssueWorkAgent))
                && await RecoverAsync(llm, req, response, headBefore, request, repo, ct).ConfigureAwait(false) is { } recovered)
            {
                return recovered;
            }
            return ParseOutcome(content, request, repo);
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

    // The run ended without its JSON summary (VNChess #102 lost a 20-minute migration that way): ask it once for the
    // summary of what it already did, then make sure a reported branch really reached the remote. Null = asking is
    // switched off (Settings › Autopilot) or the run can't be continued (a server-side provider) — the original "no
    // JSON" failure stands.
    private async Task<RepoWorkOutcome?> RecoverAsync(ILlmClient llm, LlmRequest original, LlmResponse response, string? headBefore,
        IssueWorkRequest request, WorkRepo repo, CancellationToken ct)
    {
        if (!await ReplyRecovery.ShouldAskAsync(_profiles, response, ct).ConfigureAwait(false)) { return null; }
        var repoLabel = $"{repo.Owner}/{repo.Repo}";
        Emit(request, SessionRunEventKind.Step, $"{repoLabel} — the run ended without its summary; asking the agent once for it.", repoLabel);
        var followUp = ReplyRecovery.FollowUp(original, response, IssueWorkPrompt.RecoverySystem, IssueWorkPrompt.RecoveryAsk(request, repo));
        var answer = await ReplyRecovery.AskAsync(llm, followUp, _logger, nameof(IssueWorkAgent), ct).ConfigureAwait(false);
        if (answer.Reply is not { } reply || !ReplyRecovery.HasJson(reply, nameof(IssueWorkAgent)))
        {
            Emit(request, SessionRunEventKind.ReplyRecovered, $"{repoLabel} — asked again, but the agent still gave no summary.", repoLabel);
            var content = response.Content ?? "";
            return new RepoWorkOutcome(repo.SessionRepoId, false, repo.Owner, repo.Repo, "",
                content.Length > 200 ? content[..200] : content, NoSummaryAfterAskingAgain + answer.Why);
        }

        var outcome = await VerifyRecoveredAsync(ParseOutcome(reply, request, repo), Pushed(reply), answer.Resumed, headBefore,
            request, repo, _logger, ct).ConfigureAwait(false);
        Emit(request, SessionRunEventKind.ReplyRecovered, outcome.Ok
            ? $"{repoLabel} — summary recovered: the run had pushed branch {outcome.BranchName}."
            : $"{repoLabel} — summary recovered: {outcome.Error}", repoLabel);
        _logger.LogInformation("IssueWorkAgent: recovered the missing summary for session {SessionId} repo {Owner}/{Repo} (ok={Ok})",
            request.SessionId, repo.Owner, repo.Repo, outcome.Ok);
        return outcome;
    }

    /// <summary>The failure when the run gave no JSON summary and asking once more gave none either.</summary>
    internal const string NoSummaryAfterAskingAgain = "Agent did not return a JSON summary (asked once more; still none).";

    /// <summary>The failure when a recovered summary names a branch that never reached the remote — worded so the
    /// Orchestrator agent's retry guidance tells the next run to push.</summary>
    internal static string NotPushed(string branch) => string.IsNullOrWhiteSpace(branch)
        ? "The work was done but not pushed — the run's commits never reached the remote. The next run must commit and push its branch."
        : $"The work was done but not pushed — branch {branch} is not on the remote (its commits stayed on the computer). "
          + $"The next run must commit and push it: git push -u origin {branch}";

    /// <summary>The failure when a recovered summary can't be trusted: the follow-up couldn't continue the run's own session
    /// and the remote couldn't confirm the work either.</summary>
    internal static string Unconfirmed(string branch) =>
        $"Agent did not return a JSON summary (asked once more, but {ReplyRecovery.NotResumedNote}, and the remote couldn't confirm "
        + (string.IsNullOrWhiteSpace(branch) ? "the work)." : $"that branch {branch} holds it).");

    /// <summary>A recovered summary counts only when the work is on the remote. The agent's own "pushed": false, a branch
    /// the remote doesn't have, or — for a revision of an existing pull request, whose branch is always there — a head that
    /// didn't move since before the run (<paramref name="headBefore"/>) is "done but not pushed". The remote confirms the
    /// work when the new branch is there, or the revision's head moved. When it can't tell (Azure DevOps, an API failure),
    /// the agent's word stands only if the follow-up continued the run's own session (<paramref name="resumed"/>); a fresh
    /// conversation has no memory of the run, so its unconfirmed summary is rejected.</summary>
    internal static async Task<RepoWorkOutcome> VerifyRecoveredAsync(RepoWorkOutcome outcome, bool? pushed, bool resumed, string? headBefore,
        IssueWorkRequest request, WorkRepo repo, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(repo);
        if (!outcome.Ok) { return outcome; }
        if (pushed == false) { return outcome with { Ok = false, Error = NotPushed(outcome.BranchName) }; }
        bool? exists = null;
        if (request.BranchExists is { } check)
        {
            try
            {
                exists = await check(repo, outcome.BranchName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "IssueWorkAgent: could not check branch {Branch} on the remote", LogSafe.Scrub(outcome.BranchName));
            }
        }
        if (exists == false) { return outcome with { Ok = false, Error = NotPushed(outcome.BranchName) }; }

        var confirmed = exists;
        if (IsRevision(request, repo))
        {
            // The branch always exists for a revision: only a moved head shows a push.
            var headAfter = headBefore is null ? null : await BranchHeadAsync(request, repo, outcome.BranchName, logger, ct).ConfigureAwait(false);
            confirmed = headBefore is null || headAfter is null ? null : !string.Equals(headBefore, headAfter, StringComparison.OrdinalIgnoreCase);
            if (confirmed == false) { return outcome with { Ok = false, Error = NotPushed(outcome.BranchName) }; }
        }
        return confirmed == true || resumed ? outcome : outcome with { Ok = false, Error = Unconfirmed(outcome.BranchName) };
    }

    // A follow-up round on an existing pull request: the run pushes onto that pull request's branch.
    private static bool IsRevision(IssueWorkRequest request, WorkRepo repo) =>
        !string.IsNullOrWhiteSpace(repo.ExistingBranch) && !string.IsNullOrWhiteSpace(request.ChangeRequest);

    // The remote head of a branch, through the caller's IPrCreationService (the board's credentials stay there); null when
    // unknown. Never throws (cancellation aside).
    private static async Task<string?> BranchHeadAsync(IssueWorkRequest request, WorkRepo repo, string branch, ILogger logger, CancellationToken ct)
    {
        if (request.BranchHead is not { } head || string.IsNullOrWhiteSpace(branch)) { return null; }
        try
        {
            return await head(repo, branch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IssueWorkAgent: could not read the head of branch {Branch} on the remote", LogSafe.Scrub(branch));
            return null;
        }
    }

    // The recovered summary's "pushed" flag (the follow-up's schema adds it); null when absent.
    private static bool? Pushed(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonExtractor.ExtractJson(reply, nameof(IssueWorkAgent)));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("pushed", out var p)
                ? p.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
        }
        catch (Exception ex) when (ex is LlmException or JsonException)
        {
            return null;
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
