# AgentOS — Agentic SDLC v2: implementation plan

> **Status:** Draft for approval · **Created:** 2026-10-17 · **Owner:** @hoangsnowy
> **Scope:** take AgentOS from a demo-grade 5-agent pipeline to a governed, measurable, full-SDLC
> agentic platform. This is the engineering plan; [ROADMAP.md](../../ROADMAP.md) stays the product
> roadmap. Each phase ships as small PRs, each PR is verified in the running desktop (per CLAUDE.md),
> and **nothing is "done" until it moves a metric we can see in Insights.**

## 0. Principles (non-negotiable for every PR in this plan)

1. **Metrics first.** Every capability emits events into the metrics ledger *before or with* the
   feature. A change that claims to save tokens or raise quality must show before/after numbers from
   the eval suite (§2.6). No number → not merged.
2. **Token thrift by default.** Stable-prefix prompts (cacheable), references instead of re-sent
   content, diffs instead of whole files, the cheapest model that holds quality on the eval.
3. **Shared context, governed.** Agents (ours *and* delegated external agents) read and write one
   tenant-scoped memory through the same `IToolGateway` → policy → evidence seam.
4. **Ecosystem over reinvention.** Adopt the market standards — MCP servers, Agent Skills (SKILL.md),
   best-in-class coding agents as executors — and add what they lack: tenancy, governance, evidence,
   cost, metrics.
5. **Honest UI.** Degraded/offline/canned paths are labelled as such; no green tick for a canned answer.
6. **Configured in the UI, not in appsettings.** `appsettings.json` is MVP scaffolding and shrinks to
   *bootstrap only* (connection strings, identity provider, data protection, logging, plugin path).
   Every product setting — providers, models, agent profiles, pricing, budgets, policies, MCP servers,
   sandbox limits, memory, skills — is a typed, validated, audited setting edited in the desktop and
   applied **without a restart** (Phase C). A new feature that adds a knob registers it in the
   settings registry and gets a UI field in the same PR.
7. **Performance by design — the right data structure and algorithm, measured.** Every hot path names
   its complexity and its data structure in the PR (see §2.7 playbook), ships a BenchmarkDotNet case
   when it is on a request path, and is guarded by a perf budget in CI. No O(n²) over runs, events,
   files or memories; no unbounded in-memory collections; no per-request reflection or regex rebuilds.
   Use the .NET 9/10 primitives built for this: `FrozenDictionary/FrozenSet`, `SearchValues<T>`,
   `System.Threading.Lock`, `Task.WhenEach`, `HybridCache`, `TimeProvider`, `TensorPrimitives` (SIMD
   similarity), `Microsoft.Extensions.AI`/`VectorData`, and source generators (`[LoggerMessage]`,
   `[GeneratedRegex]`, System.Text.Json) — performance is the top non-functional requirement.

## 1. Where we start (grounded 2026-10-17)

| Area | Today | Evidence |
|---|---|---|
| Build | `main` red — NuGet audit advisories published 2026-10-13/15 | fix staged locally (see PR-0.1) |
| Quality engine | 4 single-shot prompt→JSON calls, no repo context, whole-file output, C# only; tests never executed; QA reads 300-char excerpts | `LlmAgentBase.cs:87-106`, `QaPrompt.cs:50`, `TestingPrompt.cs:42` |
| Quick engine | real tool loop, but only `runner_shell` on an unsandboxed dev machine; edits via `echo`/heredoc | `IssueWorkAgent.cs:220`, `IssueWorkPrompt.cs:36-38` |
| Context between agents | typed objects in memory, single-turn calls, only the last `QaReport` carried | `PipelineOrchestrator.cs:108-136`, `PooledChatLlmClient.cs:118-123` |
| Memory | **none** — no history, summarization, embeddings, pgvector | — |
| Plugins | `IAgentOsPlugin` (code manifest) contributes tools / LLM providers / windows | `SharedKernel/Plugins/*` |
| MCP | process-wide client (no per-tenant, no HTTP auth), server at `/mcp` (3 tools) | `McpOptions.cs`, `PipelineMcpTools.cs` |
| Skills / ACP / A2A | none | — |
| Tokens | no prompt caching, no streaming, no cache-token accounting; Testing re-sends all files each iteration; Coding regenerates everything | `LlmResponse.cs:16`, `TestingPrompt.cs:59` |
| Cost | 4 hard-coded prices, unknown model = $0 (bypasses `BudgetGuard`); Quick engine records no `RunMetric` | `CostCalculator.cs:15-40` |
| Metrics | per-LLM-call `run_metrics` + OTel GenAI spans; Insights = runs, success %, QA rounds, cost | `RunMetric.cs`, `InsightsApp.razor` |
| Not measured | PR outcome (merged/closed/review rounds), human interventions, cache hits, tool latency, build/test pass, failure taxonomy, lead time, session-run cost | grounding report |
| Honesty | Offline canned client answers while the Workflow UI says "Live → Anthropic" | `OrchestrationStudio.razor:746` |
| Security | issue title/body interpolated raw into a shell-capable agent prompt; runner gate is one global bool | `IssueWorkPrompt.cs:127-143`, `ConfigGatedRemoteExecApprover.cs` |

## 2. Target architecture (what we are building toward)

```
                 ┌───────────────────────── AgentOS control plane ─────────────────────────┐
 Ticket / Issue  │  Spec agents ─► Plan/Tasks ─► Executor ─► Verify ─► Review ─► PR ─► Ops   │
 Webhook / UI ──►│      │              │            │           │          │         │       │
                 │      ▼              ▼            ▼           ▼          ▼         ▼       │
                 │  ┌──────────── Shared Context (blackboard + project memory) ───────────┐ │
                 │  └──────────────────────────────────────────────────────────────────────┘ │
                 │  IToolGateway ─ policy ─ evidence      LLM gateway ─ cache ─ router       │
                 │  Skills catalog  ·  MCP catalog (per tenant)  ·  Executor plugins         │
                 │  Metrics ledger (agent_events) ─► rollups ─► Insights / OTel / export     │
                 └────────────────────────────────────────────────────────────────────────────┘
                     Executors: built-in loop (sandbox) · Claude Code · Codex · Copilot agent
```

### 2.1 New / changed modules

| Module | Schema | Responsibility |
|---|---|---|
| `AgentOs.Modules.Metrics` *(new)* | `metrics` | Append-only event ledger, rollups, KPI queries, eval result store, PR-outcome sync |
| `AgentOs.Modules.Memory` *(new)* | `memory` | Run blackboard, project memory (pgvector + full-text), episodic run index, repo map, **bi-temporal knowledge graph** (nodes/edges, CSR + PPR retrieval), memory MCP tools, Memory app visualisers ([ADR-0008](../adr/0008-memory-and-knowledge-graph.md)) |
| `AgentOs.Modules.Skills` *(new)* | `skills` | SKILL.md catalog (built-in + tenant + git-imported), progressive disclosure tool |
| `AgentOs.Modules.Mcp` *(changed)* | `mcp` *(new tables)* | Per-tenant server registry, curated catalog, HTTP auth/OAuth, deferred tool loading |
| `AgentOs.Modules.Executors` *(new)* | — (uses `sessions`) | `ICodingExecutor` plugins: built-in loop, Claude Code, Codex, Copilot coding agent |
| `AgentOs.Modules.Llm` *(changed)* | — | Official Anthropic SDK, prompt caching, cache-token usage, model router, pricing table v2 |

Contracts go in `AgentOs.Domain` (BCL-only) so modules stay decoupled: `IAgentEventSink`,
`IRunContext`, `IMemoryStore`, `ISkillCatalog`, `ICodingExecutor`, `IModelRouter`.

### 2.2 ADRs to write (one per phase entry, before code)

- **ADR-0006** Metrics event ledger + KPI definitions
- **ADR-0007** Official Anthropic C# SDK migration + prompt-caching strategy
- **ADR-0008** Shared context memory architecture (blackboard / project / episodic / code)
- **ADR-0009** Ecosystem: per-tenant MCP catalog + Agent Skills + plugin manifest v2
- **ADR-0010** Executor delegation (built-in loop vs external coding agents) + governance hooks
- **ADR-0011** UI-first configuration: settings registry, scopes, precedence, hot reload, audit

---

## 2.3 Metrics model (the spine of the plan)

### Event ledger — `metrics.agent_events` (append-only)

One row per fact; every KPI is a query over it. Columns: `Id, TenantId, WorkItemId, RunId, SessionId?,
Engine (quality|quick|cli|graph|executor:<name>), Stage (spec|design|plan|code|test|review|security|ci|deploy|ops),
AgentRole, Type, OccurredUtc, DurationMs?, Model?, Provider?, TokensIn, TokensOut, CacheReadTokens,
CacheWriteTokens, CostUsd, Outcome?, FailureClass?, PromptVersion?, Attributes jsonb`.

Event types (v1): `RunStarted`, `RunFinished`, `LlmCall`, `ToolCall`, `PolicyDenied`, `HumanGateRequested`,
`HumanGateResolved`, `BuildResult`, `TestResult`, `MemoryRead`, `MemoryWrite`, `SkillLoaded`,
`PrOpened`, `PrReviewed`, `PrMerged`, `PrClosed`, `CiResult`, `Reverted`, `EvalCaseResult`.

`metrics.work_items` — one row per unit of work (ticket / story / incident) linking runs, sessions, PRs:
`Id, TenantId, Source (board|workflow|mcp|webhook|eval), ExternalRef (owner/repo#N), Title, Kind,
CreatedUtc, FirstPrUtc?, MergedUtc?, ClosedUtc?, Status`.

`FailureClass` taxonomy: `llm_error, rate_limited, timeout, budget_exceeded, parse_error, policy_denied,
build_failed, tests_failed, review_rejected, ci_failed, human_rejected, executor_crashed, cancelled`.

### KPI catalogue (formulas are over the ledger; all filterable by tenant / engine / stage / model / prompt version / date)

| Tier | KPI | Definition |
|---|---|---|
| ⭐ North star | **Cost per accepted change** | Σ`CostUsd` of work items with `PrMerged` ÷ #merged |
| ⭐ North star | **First-pass acceptance rate** | merged with 0 `changes_requested` reviews and 0 follow-up agent runs ÷ PRs opened |
| Outcome | Task success rate | runs with `RunFinished.Outcome=success` (build ✓, tests ✓, PR opened) ÷ runs |
| Outcome | PR merge rate / close-unmerged rate | `PrMerged` ÷ `PrOpened`; `PrClosed` w/o merge ÷ `PrOpened` |
| Outcome | Lead time | `work_items.MergedUtc − CreatedUtc` (p50/p90) |
| Outcome | Time to first PR | `FirstPrUtc − CreatedUtc` |
| Outcome | Review rounds / rework | # `PrReviewed(changes_requested)` per PR; agent runs after first PR |
| Outcome | Change-fail rate | merged PRs followed by `Reverted` or failing `CiResult` on main within 7 d ÷ merged |
| Efficiency | Tokens per successful task | Σ(in+out) ÷ successful runs |
| Efficiency | **Cache hit ratio** | Σ`CacheReadTokens` ÷ Σ(`TokensIn` + `CacheReadTokens`) |
| Efficiency | $ per run / per stage / per model | Σ`CostUsd` grouped |
| Efficiency | Context tokens per LLM turn | avg `TokensIn + CacheRead` per `LlmCall` |
| Efficiency | Tool calls per task, tool latency p90 | from `ToolCall` |
| Quality | Build pass rate / test pass rate | `BuildResult` / `TestResult` |
| Quality | QA calibration | agreement between QA verdict and real outcome (merged & CI green) |
| Human | Interventions per task, approval latency, override rate | `HumanGate*` |
| Memory | Memory hit rate, retrieved tokens, memory-assisted Δsuccess | `MemoryRead` + eval A/B |
| Reliability | Failure mix, 429 rate, retries | `FailureClass` |
| Eval | pass@1 per suite × prompt version × model; $ per solved case | `EvalCaseResult` |

### Instrumentation rules

- `IAgentEventSink` (Domain) with a buffered Postgres implementation **that never drops silently**
  (the current tool-evidence queue drops oldest at 4096 — fix: backpressure + `agentos.events.dropped`
  counter + alert).
- OTel: keep GenAI semconv spans; migrate `gen_ai.system` → `gen_ai.provider.name`, add cache-token
  usage per current semconv, add `agentos.agent`, `agentos.engine`, `agentos.stage` dimensions.
- Prompt versions: every system prompt gets a content hash → `PromptVersion` on each `LlmCall`, so
  metric changes can be attributed to prompt edits.

### 2.6 Eval harness (the yardstick for every later phase)

`tests/AgentOs.Evals` (new project, not in the default `dotnet test` run; nightly + on-demand workflow):

- **Suites:** `greenfield-csharp` (from today's KC fixtures, upgraded to hidden tests), `repo-fix-dotnet`
  (20–50 real issues on 3–5 pinned OSS .NET repos at a fixed commit, each with hidden tests that fail
  before / pass after), later `multi-lang`.
- **Grading:** objective first (build + hidden tests in the sandbox), LLM-judge only for spec/design
  quality with a rubric, judge model ≠ executor model.
- **Output:** `EvalCaseResult` events into the same ledger → Insights "Evals" tab: pass@1, $/solved,
  tokens/solved, cache hit, by model × prompt version × engine × memory on/off.
- **Cost control:** hard `$` cap per run (`EVAL_MAX_USD`), Batch API for non-interactive judge calls,
  response cache for unchanged (prompt, model, temperature=0) pairs.

### 2.7 Performance & data-structure playbook

Concrete choices per component. "Budget" values are p95 targets enforced by the benchmark/perf CI job
(`tests/AgentOs.Benchmarks`, added in PR 1.9).

| Component | Data structure / algorithm | Why | Budget |
|---|---|---|---|
| Event sink (`IAgentEventSink`) | `System.Threading.Channels` bounded channel (MPSC) → micro-batches flushed by size/time → Npgsql **binary COPY** | O(1) enqueue, no lock contention, one round-trip per batch; backpressure instead of silent drops | enqueue < 2 µs; 10k events/s sustained |
| Ledger storage | append-only table, **BRIN** index on `OccurredUtc`, composite B-tree `(TenantId, OccurredUtc)`, monthly partitions | BRIN is tiny for time-ordered appends; partitions prune scans and make retention a `DROP PARTITION` | KPI query < 150 ms over 10M rows |
| KPI rollups | incremental **hourly/daily rollup tables** (upsert on flush), percentiles via **DDSketch/t-digest** sketches merged per bucket | O(buckets) dashboard reads instead of O(events); mergeable quantiles without storing every sample | Insights load < 300 ms |
| Settings registry, pricing table, tool registry, provider map | **`FrozenDictionary`/`FrozenSet`** rebuilt on change (copy-on-write), longest-prefix model match via a **sorted prefix array + binary search** (or trie) | lock-free reads on every LLM call; O(log n) price lookup | lookup < 100 ns |
| Run blackboard / artifacts | **content-addressed store** (SHA-256 → blob), handles `kind@hash`; immutable versions | dedupe identical files across iterations/runs; agents pass handles, not content (token + memory win) | put/get O(1) |
| Iteration diffs | **Myers diff** (line-level) for file edits and QA deltas; `apply_patch` with hunk fuzz | send only changed hunks to the model | diff 10k lines < 20 ms |
| Project memory retrieval | pgvector **HNSW** index + Postgres **GIN tsvector** (BM25-style) → **Reciprocal Rank Fusion**, recency decay, **top-k min-heap** selection under a token budget (greedy knapsack) | sub-linear ANN + exact keyword recall; fused ranking beats either alone | retrieve top-20 < 50 ms |
| Knowledge-graph traversal | **CSR adjacency** (`int[]` offsets/targets + `FrozenDictionary` key→index) per tenant+repo, versioned invalidation; **personalised PageRank** by push approximation (ε-bounded) from retrieval seeds | O(edges touched) expansion, no per-hop SQL round trip; bounded work per query | 2-hop PPR on 100k edges < 10 ms |
| Topic / module clusters | **Leiden** community detection (incremental per commit), summaries generated lazily and cached | GraphRAG-style global answers without an LLM pass at index time | recluster 100k edges < 2 s |
| Memory dedupe | **SimHash/MinHash + LSH** buckets on write | near-duplicate lessons merged in O(1) expected instead of O(n) pairwise | write < 20 ms |
| Embedding / response cache | **HybridCache** (L1 memory LRU + L2 Postgres/Redis) keyed by content hash | no re-embedding unchanged text; free reruns for temp-0 calls | hit < 1 ms |
| Repo map / code index | per-commit **Merkle tree of file hashes** (re-index only changed files), symbol **trie** for prefix lookup, **PageRank** over the import/call graph to rank files (Aider-style) | incremental indexing O(changed files); most relevant symbols first within a token budget | re-index 1 changed file < 200 ms |
| Tool search (MCP + skills) | in-memory **inverted index + BM25** over tool/skill names + descriptions | O(query terms) lookup; enables deferred tool loading | search < 1 ms for 1k tools |
| Graph executor | **Kahn topological sort** for linear order, **Tarjan SCC** for cycle/loop validation, activation budget | O(V+E) validation; bounded loops | validate 500-node graph < 5 ms |
| Job queue (Phase 7) | Postgres queue with **`FOR UPDATE SKIP LOCKED`**, **min-heap** priority by (priority, deadline), per-tenant **token-bucket** rate limiter | no double-dispatch across replicas; fair, bounded concurrency | dequeue < 10 ms |
| Session/run feeds in UI | ring buffer per session (bounded), Blazor **`Virtualize`** for long lists/logs | constant memory per circuit; render cost O(visible rows) | 10k log lines stay < 16 ms/frame |
| JSON / regex on hot paths | **System.Text.Json source generators**, `[GeneratedRegex]`, `ArrayPool`/`Span<T>` for parsing | no reflection or regex compilation per call, fewer allocations | parse 100 KB < 1 ms |

---

## 3. Phased plan

Estimates are rough engineering weeks for one dev + Claude. Each PR: Conventional Commit, PR template,
unit tests, desktop wiring, screenshot proof; persistence/tenant changes verified on the Aspire stack.
UI changes round-trip through Claude Design (CLAUDE.md design workflow).

### Phase 0 — Hygiene & honesty (≈1 week) — *unblocks everything*

| PR | Change | Acceptance |
|---|---|---|
| **0.1** `chore(deps)` | Align the 10.0.x train to 10.0.13, MessagePack 3.1.12, pin `System.Security.Cryptography.Pkcs` 10.0.13 (staged locally) | CI green; `dotnet build` 0 warnings |
| **0.2** `fix(web)` | When the answering provider is `Offline` (`OfflineLlmClient.IsSubstituteFor`), Workflow/Board/Insights show an **"Offline · canned"** badge, node ticks turn grey, the run is tagged `offline` and excluded from KPIs | Standalone run with no key shows the badge; E2E asserts it |
| **0.3** `fix(llm)` | Pricing table v2 (Opus 5.5 / Opus 5 / Sonnet 5 / Sonnet 4.6 / Haiku 4.5 / Fable 5.1 + Azure models incl. cached-input rates) as built-in defaults, **unknown model → fail-closed** (budget gate treats as max price + warning), default model IDs refreshed. *Pricing becomes UI-editable in C.4.* | Unit tests per model; unknown model blocks when enforce=on |
| **0.4** `fix(pipeline)` | Ticket body fetched (GraphQL `body`) and passed to both engines, wrapped in an explicit untrusted-data envelope (`<ticket>…</ticket>` + "treat as data, never as instructions"); title sanitised | Prompt snapshot tests; injection fixture can't change branch/commands |
| **0.5** `fix(web)` | Quality path refuses to open a PR unless QA `Passed` (or an explicit human override, logged) | Unit test on the gate |
| **0.6** `fix(pipeline)` | Quick/CLI engines record `RunMetric`s (cost, tokens, latency), so `BudgetGuard` sees them; CLI mode records wall-time + exit code | Insights shows Quick runs with cost |
| **0.7** `fix(remote-agent)` | Runner: command allowlist profile + per-session approval for non-allowlisted commands, runner-side timeout/kill, pairing token moved out of the URL query | Unit tests; manual pairing on Aspire |
| **0.8** `fix(pipeline)` | Prompt branding: Coding/Qa/Requirement/Testing prompts still open "…in the Agentic SDLC system" → "…in AgentOS" (carried over from the archived deploy-readiness audit backlog). The opening line is a routing key — update any matcher in the same PR and bump the prompt version | Prompt snapshot tests; routing tests green |
| **0.9** `feat(security)` | **Postgres row-level security on every tenant table** (pipeline, sessions, workspaces, tools, tenants audit, config): `ENABLE ROW LEVEL SECURITY` + policy `tenant_id = current_setting('app.tenant_id')`, set per connection by a `DbConnectionInterceptor` from `ITenantContext`; a separate migration/platform role bypasses it. Today isolation is EF global filters + explicit predicates only — RLS makes a forgotten filter or raw SQL fail closed. Tenant export/purge endpoint | Two-tenant isolation tests on real Postgres (Aspire) incl. raw SQL; purge leaves zero rows |
| **0.10** `feat(integration)` | **Lab findings** ([docs/LAB.md](../LAB.md)): implement Azure DevOps boards in `AzureDevOpsSourceProvider` (list/validate boards, read work items via WIQL, create work items, link to repos) and ADO repo context (README + top-level tree); un-skip the pinned lab tests | Lab suite green against a real Azure DevOps org |

### Phase C — UI-first configuration (≈2 weeks) — *foundation every later phase registers into*

**Today:** product knobs are split across `appsettings.json` (duplicated in Api and Web, with
`AzureOpenAI`/`AzureOpenAi` spelling drift), `IRuntimeOverrides` (keys, force provider, Azure endpoint,
GitHub PAT) and ad-hoc AppConfig keys (`budget/*`, `tools/*`). Not editable in the UI: per-agent
provider/model/temperature/max tokens, pipeline `MaxIterations`, offline fallback, timeouts, failover
chain, MCP servers, build-sandbox limits, `RemoteAgent:AutoApprove`, `Tools:EnforceByDefault`,
`Workspaces:AllowedHosts`.

**Target model — three tiers:**

| Tier | Where it lives | Who edits | Examples |
|---|---|---|---|
| **Bootstrap** | appsettings / env / Key Vault only | operator at deploy time | `ConnectionStrings:*`, `Auth:Keycloak:*`, DataProtection, logging, `Plugins:Path`, `Persistence:RequireDatabase`, `AllowedHosts` |
| **Platform** | AppConfig, platform scope | platform admin (System settings) | provider endpoints + default models, pricing table, sandbox image/limits, security defaults (auto-approve, enforce, allowed hosts), MCP catalog, feature flags, eval budget |
| **Tenant** | AppConfig, tenant scope | tenant admin (Settings) | API keys, agent profiles (provider/model/temperature/max tokens/effort per role), pipeline NMax, budget cap, tool allowlist, enabled MCP servers, memory + skills policy |

Precedence: **tenant → platform → appsettings seed → code default**. appsettings values become
*seeds* only (imported once on first boot). The operator can still pin a hard **ceiling/lock** from
bootstrap config for security-critical keys (for example, `RemoteAgent:AutoApprove` cannot be turned on in
Production from the UI unless the operator allows it).

| PR | Change | Acceptance |
|---|---|---|
| **C.1** `docs(adr)` | ADR-0011: tiers, precedence, key naming (`area.sub.key`), secrets, locks, hot reload, audit, export/import | Reviewed |
| **C.2** `feat(config)` | **Settings registry** in SharedKernel: `SettingDescriptor` (key, type, scope, default, validation, secret, lock, UI group/label/help, restart-required), modules register descriptors in `IModule`; typed reader `ISettings` with per-scope cache, change notifications (no restart), seed-from-appsettings importer; `IRuntimeOverrides` + `budget/*` + `tools/*` re-implemented on top of it (no behaviour change) | Unit tests for precedence, locks, validation; existing Settings screens unchanged |
| **C.3** `feat(config)` | **Settings audit + versioning**: every change records who, when, scope, before/after (secrets redacted) into Tenants audit; JSON **export/import** (GitOps-friendly, secrets excluded) | Change a key → audit row; round-trip export/import |
| **C.3b** `feat(llm)` | **Model catalog**: per-model context window, max output, capabilities (sampling params, thinking/effort, long-context price tier) — seeded from the price table, refreshed from the Anthropic Models API (`max_input_tokens`, `max_tokens`, `capabilities`) where a key exists. **1M-token context is supported** (Claude 5.x, GPT long-context): limits are validated against the catalog, never a hard-coded cap; 1M is a ceiling, not a default — prompts still load context under a token budget | Picking a 1M model in the UI allows MaxTokens up to its 128K output and shows its context window |
| **C.4** `feat(web)` | **Agent profiles & models UI**: per-role provider/model/temperature/max tokens/effort, pipeline NMax, failover chain, offline fallback, timeouts, **pricing table editor**; "test connection" uses the real provider and refuses the Offline substitute | Change Coding's model in the UI → next run uses it (Aspire), no restart |
| **C.5** `feat(web)` | **Platform/System settings UI**: security defaults (auto-approve, enforce-by-default, allowed hosts), build-sandbox limits, MCP servers (moves from `Mcp:Servers`; becomes the per-tenant registry in P4.2), feature flags | Toggle enforce-by-default in UI → policy gate reflects it immediately |
| **C.6** `refactor(config)` | **Trim appsettings to bootstrap**: delete product sections from Api/Web appsettings (moved to seeds/defaults), one shared seed file for both hosts, fix the casing drift; docs/CONFIG.md rewritten as "what you set at deploy vs what you set in the UI" | `appsettings.json` contains only bootstrap keys; fresh standalone boot still runs with sane defaults |

Standalone/no-DB mode: settings fall back to an in-memory store and the Settings UI shows a
**"not persisted — standalone"** badge (same honesty rule as Offline).
Every UI change round-trips through Claude Design (CLAUDE.md design workflow).

### Phase 1 — Metrics foundation (≈2–3 weeks) — **priority #1**

| PR | Change | Acceptance |
|---|---|---|
| **1.1** `docs(adr)` | ADR-0006 — ledger, KPI definitions (§2.3), retention, privacy (no prompt bodies in the ledger) | Reviewed |
| **1.2** `feat(metrics)` | New `AgentOs.Modules.Metrics`: `metrics.agent_events`, `metrics.work_items`, migrations, `IAgentEventSink` (Domain) + buffered Postgres sink with backpressure, no-op fallback when no DB | Tenant query filter + isolation test on **Testcontainers-Postgres** (new CI job — also un-skips the 8 Dapper tests) |
| **1.3** `feat(llm)` | `LlmResponse` gains `CacheReadTokens`, `CacheWriteTokens`, `PromptVersion`; `PooledChatLlmClient` emits `LlmCall` events; OTel dims + semconv update | Unit tests; Aspire dashboard shows new dims |
| **1.4** `feat(tools)` | `ToolCall` / `PolicyDenied` events with duration + output size; evidence gets `DurationMs`, `MemberId`, secret redaction | Unit tests |
| **1.5** `feat(pipeline)` | `RunStarted/RunFinished` with `Outcome` + `FailureClass` for all engines (Quality, Quick, CLI, Graph); `HumanGate*` from Graph human nodes; `BuildResult`/`TestResult` from `build_verifier` | Every run in the ledger has exactly one RunFinished |
| **1.6** `feat(integration)` | PR outcome sync: Octokit poller (every 10 min, per board) → `PrReviewed/PrMerged/PrClosed/CiResult`; PR body gets `Closes #N`; webhook receiver added later (Phase 7) | Merge a test PR → KPI updates within 10 min |
| **1.7** `feat(web)` | **Insights v2**: north-star tiles (cost per accepted change, first-pass acceptance), funnel (runs → green → PR → merged), efficiency (tokens/success, cache hit, $ by stage/model), quality, human, failure mix; CSV/JSON export; Claude Design round-trip | Screenshot light+dark; numbers match SQL spot checks |
| **1.8** `test(evals)` | `tests/AgentOs.Evals` + `evals.yml` workflow (manual + nightly, `$` cap); suite `greenfield-csharp` with hidden tests; **record the baseline** | Baseline report committed to `docs/evals/baseline-2026-xx.md` |

| **1.9** `perf(bench)` | `tests/AgentOs.Benchmarks` (BenchmarkDotNet) for the sink, ledger KPI queries, pricing/settings lookups; `perf.yml` (manual + nightly) compares against the stored baseline and fails on > 15% regression of a §2.7 budget | Baseline committed; a deliberate O(n²) regression fails the job |

**Exit:** a single dashboard answers "what does an accepted change cost, how often is it accepted
first time, and where do tokens go" — with a recorded baseline that every later phase is compared to.

### Phase 2 — Token-saving quick wins (≈2 weeks) — measured against the Phase 1 baseline

| PR | Change | Expected effect (verify on eval) |
|---|---|---|
| **2.1** `docs(adr)` | ADR-0007 — official `Anthropic` C# SDK (has `IChatClient` integration) replaces community `Anthropic.SDK` 5.10 | — |
| **2.2** `refactor(llm)` | Migrate Claude client to the official SDK behind the existing `IChatClient` seam; read cache usage (`cache_read_input_tokens`/`cache_creation_input_tokens`) and Azure cached-token usage | parity on eval |
| **2.3** `feat(llm)` | **Prompt caching:** prompts restructured stable-first (system → skills/tool defs → project memory → run context → volatile ask); `cache_control` breakpoints on system + tools + memory block; 5-min TTL default, 1-h for nightly evals; guard against silent invalidators (no timestamps/ids in prefix, sorted JSON) | cache hit ≥ 60% on iterative runs; input $ ↓ 40–70% on QA loops |
| **2.4** `feat(pipeline)` | **Structured outputs** for every JSON agent (schema-constrained) → kill parse failures/retries; QA gets a schema | `parse_error` → ~0 |
| **2.5** `feat(llm)` | **Model router** (`IModelRouter`): per stage + difficulty + effort; defaults: Haiku 4.5 for classify/triage/summaries/reflection, Sonnet 5 for coding/testing, Opus 5.5 for spec/design/hard review; effort `low` for sub-tasks; all overridable per tenant | $/accepted change ↓ without pass@1 drop |
| **2.6** `feat(pipeline)` | **Iteration diet:** Coding iteration receives previous files + targeted QA issues (not regen from spec); Testing receives only changed files; QA receives full code (not 300-char excerpts) but via cached prefix; stop early when QA score doesn't improve between rounds | tokens/success ↓, QA calibration ↑ |
| **2.7** `feat(tools)` | Tool-output shaping: build/test logs trimmed to errors + summary, MCP outputs capped + summarised by cheap model when > N tokens | tool tokens ↓ |
| **2.8** `feat(llm)` | Token streaming (`GetStreamingResponseAsync`) for long calls + progress UI; `HybridCache` response cache for deterministic (temp 0) calls keyed by (prompt hash, model) | p50 time-to-first-token visible; eval reruns ~free |

**Exit:** eval shows ≥ 40% lower $/solved at equal or better pass@1, cache-hit tile live in Insights.

### Phase 3 — Shared context memory (≈3–4 weeks)

Four layers, one module (`AgentOs.Modules.Memory`), one governance path:

| Layer | Scope | Lifetime | Store | Used for |
|---|---|---|---|---|
| **Run blackboard** (working memory) | run | run (+archive) | `memory.run_items` jsonb | agents share artifacts *by reference* (`spec@v2`, `file:src/X.cs@sha`, `qa#3`) instead of re-sending content; Graph `StateSchema` becomes real |
| **Project memory** (semantic) | tenant → repo → role | long-lived, decays | `memory.items` + **pgvector** + Postgres FTS (hybrid) | conventions, decisions/ADRs, architecture facts, "lessons" from failures and review comments |
| **Episodic memory** | tenant → repo | long-lived | `memory.episodes` (embedding of ticket + outcome summary) | "a similar ticket was solved in PR #123 — here's how" |
| **Code knowledge** | repo @ commit | per commit | `memory.repo_maps` | structural repo map (symbols, signatures, deps) — read before files |

| PR | Change |
|---|---|
| **3.1** `docs(adr)` | [ADR-0008](../adr/0008-memory-and-knowledge-graph.md) — layers, **bi-temporal knowledge graph in Postgres**, deterministic-first extraction, retrieval pipeline, policies, visualisation; `IMemoryStore` with built-in pgvector default and Mem0 / Zep-Graphiti / Letta as plugins — *written* |
| **3.2** `feat(memory)` | Module skeleton (schema `memory`, uuidv7 ids, tenant RLS + EF filter) + **run blackboard** (`IRunContext`): content-addressed artifacts by handle with versions; Quality/Graph engines switched to handles; `MemoryRead/Write` events |
| **3.3** `infra` | Postgres 18 + pgvector 0.8 (`halfvec`, HNSW, iterative scans) in AppHost and the Azure `azure.extensions` allow-list (`VECTOR`); `Pgvector.EntityFrameworkCore`; embeddings via MEAI `IEmbeddingGenerator` — provider is a UI setting (Voyage `voyage-code-4`/`voyage-4`, Azure OpenAI, local ONNX), stored 1024-d `halfvec`, `HybridCache` by content hash |
| **3.4** `feat(memory)` | **Graph core**: `memory.nodes` / `memory.edges` (bi-temporal: `valid_from/valid_to` + `recorded_at/retracted_at`, provenance), invalidate-not-overwrite, in-process **CSR adjacency** cache per repo, recursive-CTE fallback; property tests + benchmarks |
| **3.5** `feat(memory)` | **Deterministic extractors**: Roslyn (C#) + tree-sitter repo map → `File/Symbol` nodes and `calls/implements/depends_on/contains` edges per commit; git/PR/issue links (`Ticket -solved_by-> PR -touches-> File`); ADR/doc ingestion; tools `repo_map`, `find_symbol` |
| **3.6** `feat(memory)` | **Project memory + retrieval**: items (`convention|decision|fact|lesson|preference|doc`), confidence, decay, pin; seed = halfvec k-NN (binary-quantised first pass + re-rank) ∥ FTS (`pg_search` BM25 when self-hosted) → RRF; **personalised PageRank** expansion (1–2 hops, task-weighted relations); optional reranker; min-heap/knapsack packing under the budget into the cached prefix; full trace in `memory.retrievals`; SimHash+LSH dedupe |
| **3.7** `feat(memory)` | **Reflection** after each run (small model): lessons/decisions from failures, QA issues, review comments → candidates (auto-accept above threshold, else curation queue); **episodic index** of finished work; lazy **Leiden community** summaries (generated on first use, cached) |
| **3.8** `feat(mcp)` | **MCP tools** on `/mcp` (`memory.search`, `memory.write`, `graph.neighbors`, `graph.path`, `context.get`) + Anthropic memory-tool backend + context editing for long delegated sessions → **Claude Code / Codex share the same memory** |
| **3.9** `feat(web)` | **Memory app — visualise everything** (designed in Claude Design first): **Graph Explorer** (sigma.js v3 + graphology WebGL, ForceAtlas2 in a Web Worker, filters, expand, community hulls, *as-of* time slider), **Retrieval Inspector** (seeds → expansion path on the graph → packed token bars vs budget → used), **Run Blackboard** timeline + version diffs + tokens saved, **Knowledge base** table + curation queue, **Code Map**, **Memory metrics**; table alternative for every graph view, reduced-motion, vendored JS (no CDN) |
| **3.10** `test(evals)` | Eval A/B: memory off / hybrid only / hybrid + graph → publish Δpass@1, Δtokens, Δ$/solved; ship each step only if it wins; tune budgets |

Guardrails: secret/PII redaction before write, per-tenant isolation tests on real Postgres, max
memory tokens per prompt (budgeted, placed in the cached prefix when stable), every read/write is an
evidence row.

**Exit:** memory-on beats memory-off on the eval (success ↑ and/or tokens ↓), and a Claude Code
session in CLI mode demonstrably reads a lesson an AgentOS run wrote.

### Phase 4 — Ecosystem: MCP catalog, Agent Skills, executor plugins (≈3–4 weeks)

| PR | Change |
|---|---|
| **4.1** `docs(adr)` | ADR-0009 (catalog + skills + manifest v2) and ADR-0010 (executor delegation + governance hooks) |
| **4.2** `feat(mcp)` | **Per-tenant MCP registry** (`mcp.servers`): stdio/http, auth headers + OAuth, secrets encrypted in AppConfig, enable per tenant, per-agent-role tool allowlist, health + latency metrics |
| **4.3** `feat(mcp)` | **Curated catalog** (one-click, versioned manifests): GitHub (official), Azure DevOps (official), Playwright (browser/E2E verify), Context7 (library docs), Microsoft Learn docs, Sentry (incidents), Atlassian (Jira/Confluence), Slack; Serena (LSP symbol tools) as optional code-intelligence |
| **4.4** `feat(mcp)` | **Progressive tool disclosure**: deferred loading + a `tool_search` tool (Anthropic tool search when on Claude; our BM25 over tool descriptions otherwise) so 100+ MCP tools don't bloat every prompt |
| **4.5** `feat(skills)` | **Agent Skills (SKILL.md)** module: built-in + tenant + git-imported skill packs (e.g. `anthropics/skills`), only name+description in the prompt, body loaded on demand (`load_skill`), skill usage metrics; our agent prompts re-expressed as skills |
| **4.6** `feat(plugins)` | Plugin manifest v2 (`agentos-plugin.json`): contributes tools, windows, providers **+ skills, MCP server definitions, workflow templates, executors**; SHA-256/signature check on load |
| **4.7** `feat(executors)` | `ICodingExecutor` + executors: **Claude Code** (CLI/Agent SDK on the paired runner), **Codex CLI**, **GitHub Copilot coding agent** (assign issue via API, track PR); each launched with AgentOS MCP (memory + tools) and **hooks that stream every tool call back as evidence/events**; budget + approval policy enforced at launch |
| **4.8** `feat(web)` | **Marketplace app** (MCP + skills + plugins + executors) and executor picker on Board; Claude Design round-trip |

**Exit:** a tenant enables GitHub + Playwright MCP and a skill pack from the desktop, runs a ticket
through the Claude Code executor, and every tool call, token and dollar of that external run shows
up in Insights next to built-in runs.

### Phase 5 — Built-in repo-grounded executor (≈3–4 weeks) — *runs after P2 (D-A)*

Unify Quality + Quick into one agent loop — **the primary executor** (D-A), and the only one for air-gapped tenants:
sandbox container per run (reuse ADR-0005 runner; ACA Jobs in cloud), structured tools (`read_file`,
`search`, `repo_map`, `apply_patch`, `run_build`, `run_tests`, `git`), context editing (clear stale
tool results) + compaction for long loops, task budgets, **QA on real signals** (build, tests,
coverage delta, LLM review of the diff), multi-language via repo detection. Exit: built-in executor's
pass@1 / $ per solved on `repo-fix-dotnet` reported against Claude Code executor.

### Phase 6 — Spec-driven front of the SDLC (≈2–3 weeks)

Requirement agent with clarifying questions (human answers in Board), Design agent (`design.md`,
ADR, Mermaid diagrams written to the repo), Tasks agent (dependency-ordered tickets with acceptance
criteria pushed to GitHub/ADO), **human gates** at spec / design / PR (Graph human node without
auto-approve), all artifacts stored in the blackboard + project memory. Metrics: spec rework rate,
gate approval latency, downstream success by spec quality.

### Phase 7 — Event-driven loop (≈3 weeks)

GitHub/ADO **webhooks** (issue assigned/labelled → run; PR review comment → revise; check-run failed
→ **Fix-CI agent**), **Code-review agent** on every PR (agent or human authored), **Security agent**
(CodeQL/SARIF + dependency alerts → triage + fix PR), durable job queue replacing in-process
`Task.Run`, Slack/Teams notifications. Metrics: review rounds, CI-fix success, MTTR for red main.

### Phase 8 — Release & operations (≈3–4 weeks)

CI/CD agent (author/repair workflows), Deploy agent (`azd`, approval gate, rollback), **SRE/incident
agent** (App Insights / Sentry alert → triage → fix PR → Slack thread), **Dependency agent** (fix
Dependabot bumps that break the build — exactly today's failure mode), Docs agent (README/API docs
from merged changes). Metrics: change-fail rate, MTTR, deploy frequency (DORA, measured on outcomes,
not agent activity).

---

### The software-factory view (owner request 2026-10-17)

Reference: "software factory explained by a Senior Engineer" (theSeniorDev). The goal is the same as this
plan — *build the factory, not the code*: a chain of agent **stations** that turns raw signals into merged,
verified changes, and engineers improve the factory when output is wrong. Station → where it lands here:

| Station (video) | AgentOS | Status / gap |
|---|---|---|
| Triage agent — automated bug collection | **P8 SRE/incident agent** (App Insights, Sentry MCP, Board issues) + **new F.1 Triage**: cluster errors/user reports into deduplicated tickets (SimHash/LSH dedupe, §2.7) | gap → add F.1 |
| Refinement agent — bug report → scoped ticket | **P6 spec-driven** (clarifying questions, acceptance criteria, `TicketDecomposerAgent`) | planned |
| Prioritizer — what gets built | **new F.2 Prioritizer**: score tickets (impact × confidence ÷ effort, blast radius) into a priority queue (heap); human can reorder | gap → add F.2 |
| Implementer — CLAUDE.md, skills, MCP | **P5 built-in executor** + **P4 skills/MCP** + project memory (P3) | planned |
| Built-in quality pipeline | QA on real signals (P5), **P7 code-review + security agents** | planned |
| E2E tests over TDD + visual diffs | P5 runs E2E; **new F.3 visual-diff check** (Playwright screenshots vs baseline, pixel/perceptual diff) | gap → add F.3 |
| 100-turn limit when stuck | iteration cap + **task budgets** (P2) + stuck detection (no-progress in N turns → escalate to a human) | partial → in P5 |
| PRs, blast radius, auto-merge | **new F.4 merge policy**: blast-radius score (files/modules touched, ownership, test coverage of changed lines); low radius + green checks + AI review pass → auto-merge, else human | gap → add F.4 |
| AI reviewer | **P7 code-review agent** | planned |
| Staging, smoke tests, QA agent | **P8 deploy agent** + smoke tests + QA agent driving the running app (Playwright MCP) | planned |
| Formal verification | out of scope for v2 (note for later: property-based tests as the practical step) | deferred |
| Observability, agent-friendly codebase | **P1 metrics ledger**, OTel GenAI, repo map (P3) | planned |
| Model router, docs agent, feature flags | **P2.5 model router**, **P8 docs agent**, **new F.5 feature-flag gating** of agent-shipped changes | gap → add F.5 |

**Phase F — Factory stations (folds into P6–P8 order):** F.1 Triage · F.2 Prioritizer · F.3 visual diffs ·
F.4 blast-radius + auto-merge policy (configurable in Settings, default = human merge) · F.5 feature flags.
A **Factory** desktop app shows the line as stations with live throughput, queue depth, pass/fail and
$ per station — straight off the P1 metrics ledger.

## 4. Sequencing & dependencies

```
P0 ──► PC (UI config) ──► P1 (metrics + baseline) ──► P2 (tokens) ──► P5 (built-in executor) ──► P3 (memory) ──► P4 (ecosystem) ──► P6 ──► P7 ──► P8
                         └────────── eval harness gates every arrow after P1 ──────────┘
```

Phase IDs are stable; **execution order follows D-A (owner decision 2026-10-17): the built-in executor (P5)
runs right after P2**, before memory and the ecosystem. P5 ships a minimal repo map that P3.6 later
generalises; P4.7 (external executors) is then an add-on next to the built-in one, compared on the same eval.

## 5. Owner decisions

| # | Decision | Outcome |
|---|---|---|
| D-A | Executor strategy | ✅ **Decided 2026-10-17: build the built-in executor first** (P5 right after P2); external executors (P4.7) follow as optional add-ons |
| D-B | Memory backend | ✅ **Decided: built-in pgvector + FTS** behind `IMemoryStore`; Mem0/Zep/Letta as optional adapter plugins |
| D-C | Embeddings | MEAI `IEmbeddingGenerator`, Azure OpenAI default, local ONNX for on-prem |
| D-D | Metrics store | Postgres (`metrics` schema) now; revisit ClickHouse only if ledger > ~50M rows |
| D-E | Eval repos | Pick 3–5 pinned OSS .NET repos with good test suites for `repo-fix-dotnet` |
| D-F | Eval budget | ✅ **Decided: $100 / month** cap for nightly + on-demand evals (`EVAL_MAX_USD`) |
| D-G | Configuration | ✅ **Decided 2026-10-17: UI-first.** appsettings is MVP scaffolding → bootstrap-only; product settings live in AppConfig (platform + tenant scope), edited in the desktop, hot-reloaded (Phase C, runs right after P0) |

## 6. Risks

| Risk | Mitigation |
|---|---|
| Metrics without outcomes (vanity activity counts) | North-star KPIs are outcome-based (merged, first-pass, change-fail); activity metrics only as diagnostics |
| Memory poisoning / prompt injection via stored lessons | provenance + confidence, redaction, curation queue, memories rendered as data blocks, never instructions |
| External executors bypass governance | launch-time policy + hooks → evidence; deny when hooks unavailable |
| Token-saving degrades quality | every lever gated on eval pass@1 non-regression |
| Scope creep | one PR at a time, each with a visible desktop change and a metric |
