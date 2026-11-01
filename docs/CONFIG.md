# AgentOS.NET — Configuration Reference

AgentOS.NET splits configuration in two (ADR-0011 — UI-first configuration):

1. **Set at deploy** — the few keys a host needs *before anyone can sign in*: where the database is,
   how sign-in works, logging, secrets, and operator locks. These live in `appsettings*.json`,
   environment variables (`Section__Key`), user-secrets (Development) or Aspire/azd injection.
2. **Set in the desktop** — every product setting (LLM providers, agent profiles, pipeline, safety
   switches, build sandbox). Each one is a `SettingDescriptor` declared by its module, has its default
   **in code**, and is changed in **Settings** without a restart.

`appsettings.json` contains bootstrap keys only — a unit test (`BootstrapConfigTests`) fails the build if
a product section creeps back in. There is no shared seed file: code is the single source of defaults,
so the Web and Api hosts cannot drift apart.

## How a setting value is resolved

For a key read through the settings registry, first match wins:

| # | Tier | Where it comes from | Who sets it |
|---|---|---|---|
| 1 | **Lock** | `Settings:Locks:<key>` in deployment config | Operator — the UI shows it read-only with the reason |
| 2 | **Tenant** | Settings UI, stored encrypted in `config.app_config` | Tenant admin (tenant-scope keys only) |
| 3 | **Platform** | Settings UI (`@platform/` rows) | Platform admin (`platform-admin` realm role) |
| 4 | **Seed** | Any configuration source at the same key (env var, user-secrets, appsettings) | Operator — optional; e.g. `Agents__Coding__Model` |
| 5 | **Default** | The descriptor's default, in code | — |

Every write is validated by the descriptor (kind, range, choices, custom validator) and audited
(who, when, scope, before/after; secrets masked). Readers resolve per call, so a saved change applies
to the next request.

## Set at deploy (bootstrap)

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | — | Postgres. Absent → modules fall back to no-op repositories (stateless boot) unless `RequireDatabase` is true. Aspire injects it on the full stack. |
| `Persistence:RequireDatabase` | `true` (code); Web base `false` for the standalone run, `true` in Web Production | Fail-loud guard: refuse to boot stateless when a DB is expected. |
| `Persistence:RowLevelSecurity` | `true` | Postgres RLS on the tenant tables ([ADR-0012](adr/0012-postgres-row-level-security.md)); the DB user needs `CREATEROLE`. `false` = escape hatch (application filters only). |
| `Auth:Keycloak:Authority` | empty | **Required outside Development.** Dev fallback `http://localhost:8080/realms/agentic` applies in Development only. |
| `Auth:Keycloak:Audience` | `agentic-api` | JWT audience (Api). |
| `Auth:Keycloak:ClientId` / `ClientSecret` | `agentic-web` / empty | Web OIDC client. Secret required outside Development; the dev default secret is rejected by `DevSecretGuard`. |
| `Auth:Keycloak:RequireHttpsMetadata` | `true` | Unset: true, except an `http://localhost` authority in Development. |
| `Auth:Keycloak:Admin:BaseUrl` | empty | Keycloak admin API for member lifecycle. Empty → no-op client. |
| `Auth:Keycloak:Admin:{Realm,Username,Password,ClientId}` | `agentic`/`admin`/`admin`/`admin-cli` | Use a service account + secrets in production. |
| `Auth:DevAutoLogin` | `true` in Development only | Fixed-user auth for the standalone Web. Hard-throws outside Development; AppHost forces it off. |
| `DataProtection:KeyVaultKeyId` | empty | Wrap the DataProtection key ring (which encrypts every tenant secret) with an Azure Key Vault key — `https://<vault>.vault.azure.net/keys/<key>`, via managed identity. |
| `DataProtection:CertificatePath` / `CertificatePassword` / `CertificateThumbprint` | empty | Wrap it with a certificate instead (on-prem / k8s secret mount, or the Windows cert store). |
| `DataProtection:RequireKeyWrapping` | `false` | Refuse to start with an unwrapped ring outside Development. System → About shows the key ring's protection either way. |
| `Api:BaseUrl` | empty | Empty = Web runs the engine in-process. Set = Web is a thin client of the Api host. System → About probes the Api at Aspire's `services:api:*` address first (the AppHost references the Api from the Web for that card only), then this. |
| `Settings:Locks:<key>` | Production pins 4 keys (below) | Operator locks. Change with an env var, e.g. `Settings__Locks__RemoteAgent__AutoApprove=true` — a deployment decision, not a click. |
| `Logging:*`, `AllowedHosts` | ASP.NET defaults | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `APPLICATIONINSIGHTS_CONNECTION_STRING` | — | Enable OTLP / App Insights export. |
| `Email:*` | `SmtpHost` empty → logs instead of sending | `SmtpHost`, `SmtpPort` (1025), `From`, `FromName`, `User`, `Password`, `UseStartTls`. Validated at startup. Aspire injects MailHog in dev. |
| `Mcp:Servers[]`, `Mcp:CallTimeoutSeconds` | empty / 60 | Upstream MCP servers. Validated at startup. Moves to a per-tenant registry in Phase 4. |
| `Plugins:Path` | `plugins` | Folder scanned for `IAgentOsPlugin` assemblies. |

**Secrets** (`Llm:Claude:ApiKey`, `Llm:AzureOpenAi:ApiKey`, …) can be seeded at deploy
(`azd env set Llm__Claude__ApiKey …`, user-secrets) *or* entered in Settings → LLM providers, where a
tenant key wins over the platform seed. Never commit them.

### Production locks (`appsettings.Production.json`, Api + Web)

| Locked key | Value | Why |
|---|---|---|
| `RemoteAgent:AutoApprove` | `false` | Remote execution on a paired machine always needs a human approval. |
| `Tools:EnforceByDefault` | `true` | Tool policy fails closed: a tool runs only if the tenant allowlists it. |
| `Llm:OfflineFallback` | `false` | A missing key fails loudly instead of serving canned output. |
| `Integration:BuildVerifier:Sandbox` | `Container` | Generated code never builds in the host process. |

## Set in the desktop (Settings)

Defaults below are the code defaults (tier 5). **Scope**: *Tenant* = each tenant admin sets their own;
*Platform* = one value for every tenant, platform admin only.

### Settings → LLM providers

| Key | Default | Scope | Notes |
|---|---|---|---|
| `Llm:Provider` | `Claude` | Tenant | Used by any agent whose profile names no provider. `Anthropic` is an alias of `Claude`. |
| `Llm:ForceProvider` | empty | Tenant | Route every agent to one provider. |
| `Tools:MaxOutputTokens` | `4000` (500–50000) | Tenant | Budget for one tool result sent back to the model (≈ chars / 4), on the **Routing** page. See *Tool-output shaping* below. |
| `Llm:OfflineFallback` | **`true` in Development, else `false`** | Platform | Keyless provider → canned, schema-valid output, labelled Offline everywhere (runs, Workflow chip, PR refusal). Lets a fresh checkout run with no secrets. Locked off in Production. Never applies to Board agent runs (New idea, Coding, Reviewer, Testing, the Autopilot lead, Retrospective): those act on a real project, so they fail with the real reason (e.g. a plan limit) instead of getting canned text. |
| `Llm:Claude:ApiKey` / `ApiKeys` | empty | Tenant | Secret, write-only in the UI. Pool = round-robin with 429 failover. |
| `Llm:AzureOpenAi:Endpoint` / `ApiKey` / `ApiKeys` | empty | Tenant | Endpoint must be absolute `https://`. |
| `Llm:AzureOpenAi:Model` | `gpt-4.1` | Tenant | Default deployment. |
| `Llm:AzureOpenAi:PricingModel` | empty | Tenant | Canonical price-table id for an aliased deployment (`gpt41-prod` → `gpt-4.1`). |
| `Llm:OpenAi:ApiKey` / `Llm:OpenAi:Model` | empty / `gpt-5.4` | Tenant | Bring-your-own-key **OpenAI** (`https://api.openai.com/v1`, fixed). Provider name `OpenAI` — **no longer an alias of `AzureOpenAI`** (use `Azure`/`AzureOpenAI` for Azure). |
| `Llm:Gemini:ApiKey` / `Llm:Gemini:Model` | empty / `gemini-3.8-flash` | Tenant | **Google Gemini** via its OpenAI-compatible endpoint (fixed). Provider `Gemini` (alias `Google`). |
| `Llm:Xai:ApiKey` / `Llm:Xai:Model` | empty / `grok-4.7` | Tenant | **xAI Grok** (`https://api.x.ai/v1`, fixed). Provider `xAI` (alias `Grok`). Grok bills the whole request at the long-context rate above 200k prompt tokens; the price table models it. |
| `Llm:OpenAiCompatible:Endpoint` | empty | Platform | Any `/v1/chat/completions` server — vLLM, Ollama (`http://host:11434/v1`), LM Studio, Azure AI Foundry, a gateway. Platform-only because the server calls it (no tenant-set SSRF). Provider name `OpenAICompatible` (aliases `Ollama`, `vLLM`, `Local`). |
| `Llm:OpenAiCompatible:ApiKey` | empty | Platform | Secret; leave empty for keyless servers. |
| `Llm:OpenAiCompatible:Model` | `llama3.1` | Platform | Served when an agent profile names a vendor model (Claude/GPT) the server doesn't host; any other model id in a profile is sent as-is. |
| `Llm:OpenAiCompatible:InputPricePerMTok` / `OutputPricePerMTok` | `0` / `0` | Platform | Your own $/1M rates — metrics and the budget use them (0 on your own hardware), never the unknown-model ceiling. |

Code-only (no UI yet; seed via env if you must): `Llm:Claude:Endpoint` (`https://api.anthropic.com`),
`Llm:Claude:ApiVersion` (`2023-06-01`), `Llm:*:TimeoutSeconds` (60), `Llm:Fallbacks`
(inter-provider failover map). Resilience today is key-pool cooldown + failover; `Llm:*:MaxRetries`
exists on the options type but is not honoured yet.

**Tool-output shaping** — a tool result headed back to the **model** (the LLM tool loop: `AIToolFunction` builds a
model-facing `DefaultToolGateway`) is shaped there; data consumers such as a workflow Tool node (whose result feeds
`${…}`, ExtractJson, Transform) always get the full output (`ToolGatewayResult.Output`; the shaped text is
`ModelOutput`). A result within `Tools:MaxOutputTokens` × 4 chars is passed through **unchanged**. A larger one:
build/test logs (MSBuild, `dotnet build`/`test`, VSTest, xUnit / Microsoft.Testing.Platform — recognised by lines
only a build/test tool prints, not by a keyword anywhere; LF, CRLF or mixed line endings) keep their
`error`/`warning` lines (`error CS…`, analyzer ids), failed-test blocks with their first 5 stack frames, summary lines
and a short head/tail window; restore/progress noise is dropped and repeated lines collapse to one with a `(×N)`
count. JSON results (objects or arrays, at any depth — `build_verifier`'s `output`, MCP `content[].text`) are shaped
in place and stay valid JSON: long string values get the log pass and a share of the budget, over-long arrays keep
their first elements plus a `"[… N more items omitted …]"` marker element. Whatever is still over the budget is cut
as text, head + tail around an explicit `[… N lines omitted …]` marker (never inside a surrogate pair) — so errors
are kept by the log pass, but this final cut may still drop some (it says so), and JSON that could not be fitted
structurally (e.g. an object with thousands of keys) is then no longer valid JSON. A shaping failure never fails the
call: it falls back to that plain cut. The evidence log (`tools.tool_invocations`) still stores the **full** output
plus `OutputChars`, `ShapedOutputChars` (= `OutputChars` when nothing was shaped) and the `MaxOutputTokens` budget
used (null for non-model callers), and the `AgentOs.Tools` meter counts the difference as
`agentos.tools.output.saved_chars` (tag `kind` = `builtin` | `mcp` | `remote`; ≈ chars / 4 tokens — an estimate,
not billed usage).

### Settings → Agent profiles

One agent registry (`AgentRoles`): every agent AgentOS.NET runs has one stable **role key**, used for its
profile (`Agents:<Key>:Provider` / `Model` / `Temperature` / `MaxTokens`, tenant scope), its prompt in
**Settings → Prompts** (`prompt/<Key>/system`), the `AgentRole` of its LLM calls (what **Insights → By agent**
groups and names by) and the label of its stored runs. The page shows two tables — the **Board agents** and the
legacy 5-agent **Workflow pipeline** — so a change to one never silently touches the other. Every profile is read
per call (no restart). The model field lists priced models for the chosen provider (plus *Custom…* for deployment
aliases); `Temperature` is hidden for models that reject sampling params; `MaxTokens` ≤ 128 000.

| Shown as | Key | Provider | Model | Temp | Max output |
|---|---|---|---|---|---|
| Discovery (New idea: requirement + ticket split) | `Discovery` | Anthropic | *Computer's default* | 0.2 | 4000 |
| Coding (tickets) | `IssueWork` | Anthropic | *Computer's default* | 0.2 | 32768 |
| Reviewer | `Reviewer` | RemoteAgent | *Computer's default* | 0 | 32768 |
| Testing (tickets) | `Acceptance` | RemoteAgent | *Computer's default* | 0 | 32768 |
| Designer (the Design gate) | `Designer` | RemoteAgent | *Computer's default* | 0 | 8192 |
| Autopilot lead | `AutopilotOrchestrator` | RemoteAgent | *Computer's default* | 0 | 4096 |
| Retrospective | `Retrospective` | RemoteAgent | *Computer's default* | 0 | 4096 |
| Orchestrator (Workflow pipeline) | `Orchestrator` | Anthropic | `claude-haiku-4-5` | 0.3 | 2000 |
| Requirement (Workflow pipeline) | `Requirement` | Anthropic | `claude-sonnet-4-6` | 0.1 | 2000 |
| Coding (Workflow pipeline) | `Coding` | AzureOpenAI | `gpt-4.1` | 0.2 | 4000 |
| Testing (Workflow pipeline) | `Testing` | AzureOpenAI | `gpt-4o-mini` | 0.2 | 3000 |
| QA (Workflow pipeline) | `Qa` | Anthropic | `claude-haiku-4-5` | 0.1 | 1500 |

- **Computer's default (Board agents).** A Board agent chooses **no model** unless someone picks one. On a member's
  paired computer (`RemoteAgent`) the request carries no model and the runner passes no flag, so the CLI runs its
  own default — the member's choice, usually newer than any id AgentOS.NET would pin. On our servers the LLM gateway
  fills in the provider's configured model (`Llm:Claude:Model`, the Azure default deployment, the vendor's / the
  OpenAI-compatible server's model from **Settings → Model keys**) before the request is validated. A model picked
  in the profile is always passed: `claude --model <id>`, `codex -m <id>` (only when that CLI can serve it). The
  Workflow-pipeline agents always name a model. Reviewer and Testing (tickets) need a checkout, so keep them on a
  paired computer unless the chosen provider can reach one.
- **Budget gate.** Every Board agent passes the tenant's enforced monthly cap before its call, decided from the
  *effective* provider (after `Llm:ForceProvider`); only a call that really runs on a member's computer (0 server
  tokens) is exempt.
- **Discovery** covers both calls of New idea: the requirement step uses the Discovery profile (and is filed under
  Discovery in Insights) with the Requirement agent's prompt and spec contract; the ticket split uses the Discovery
  prompt. It replaces the former `Decomposer` keys, which **keep working**: while a Discovery field has no stored
  value, the tenant's (then the platform's, then the appsettings) `Agents:Decomposer:<Field>` applies, and the
  Decomposer prompt override applies while no Discovery override is saved. Agent profiles marks the row *Saved as
  "Decomposer" — still applies*; saving a field stores it under Discovery, and **Reset** (profile or prompt) clears
  the tenant's Decomposer values too.
- **Prompts.** Board prompts are templates: `{owner}`, `{repo}`, `{branch}`, `{defaultBranch}` (Reviewer, Testing
  (tickets)) and `{issue}`, `{branchStep}`, `{commitType}`… (Coding (tickets), which has one prompt for a member's
  computer and one — `IssueWork.Server` — for the server runner) are filled on every run. The untrusted-text rule is
  appended by code and cannot be overridden; workspace rules follow it.
- **Practices.** Coding, Reviewer, Testing (tickets) and the Autopilot lead read the project's practices (Board →
  Knowledge) before they work.

`Pipeline:MaxIterations` (QA-loop cap when a story sets no `NMax`) — default 3, range 1–10.
An unpriced model is charged at the fail-closed $15/$75 per 1M rate until added to `CostCalculator`.

### Settings → Autopilot

How Autopilot runs a project once a person turns it on in **Board › Autopilot**. Tenant scope (admins); read on every
pass / call, so a change applies within a minute, no restart. The Board's Autopilot tab shows the live values.

| Key | Default | Range | Notes |
|---|---|---|---|
| `Autopilot:DecidedBy` | `Orchestrator agent` | `Orchestrator agent` / `Rules` | Who picks the next moves. The agent works within the rules; `Rules` = the fixed rules alone (saves the member's plan). |
| `Autopilot:TicketEngine` | `MAF workflow` | `MAF workflow` / `Step by step` | How each started ticket runs: one Microsoft Agent Framework graph per ticket, or Autopilot driving each step every minute. |
| `Autopilot:MaxParallel` | `2` | 1–6 | Tasks running at once on a board (coding, fixing, reviewing, testing). |
| `Autopilot:MaxParallelTests` | `1` | 1–3 | Acceptance runs at once **per member's computer** (each brings the app up on its fixed ports). |
| `Autopilot:MaxFixRounds` | `3` | 0–10 | Times a change goes back to the Coding agent (red checks, conflicts, review / acceptance findings) before a person is asked. Counted **per task in total** from the persisted `FixRounds` — the same count in both engines and across restarts. |
| `Autopilot:MaxAgentRetries` | `2` | 0–5 | Extra guided retries the Orchestrator agent may give a stuck task after the fix rounds are used up. |
| `Autopilot:ChecksTimeoutMinutes` | `60` | 5–240 | How long a ticket's MAF workflow waits for a pull request's checks before handing it back to the step-by-step passes. |
| `Autopilot:ChecksPollSeconds` | `60` | 15–600 | How often it looks at them. |
| `Autopilot:CodingRunMinutes` | `60` | 10–240 | The longest one Coding (tickets) run may take on a computer — clone, change, build, test, push (was a fixed 20 minutes). |
| `Autopilot:ReviewRunMinutes` | `30` | 5–120 | The longest one Reviewer run may take (was a fixed 20). The Designer (Design gate) runs under the same limit. |
| `Autopilot:TestingRunMinutes` | `45` | 10–180 | The longest one Testing (tickets) run may take (was a fixed 30). |
| `TicketFiles:MaxFileMegabytes` | `10` | 1–100 | *Files for the AI* — the largest single file a person can attach to a ticket ([ADR-0015](adr/0015-ticket-files.md)). |
| `TicketFiles:MaxTotalMegabytes` | `50` | 1–500 | All files of one ticket together. They are copied to the computer for every run of the ticket. |
| `TicketFiles:MaxFiles` | `500` | 1–2000 | Files per ticket (an uploaded folder counts each file in it). |

The three `TicketFiles:*` limits are declared by the Sessions module (`TicketFileSettings`) and shown in their own
*Files for the AI* panel on this page; they are read on every upload (desktop and `PUT /workspaces/{boardId}/tickets/{n}/files/{path}`),
so lowering one never deletes stored files — a run still carries at most 512 MB, whatever the limits say.

Fixed rules (not settings): *Build and merge* merges a change only when its checks are green and its ticket flow's
gates passed — by default the Reviewer agent approved it and the Testing agent passed it, which needs the member's
computer connected. When that computer's
Claude / Codex **plan hits its limit** (e.g. `You've hit your session limit · resets 11:50pm (Asia/Ho_Chi_Minh)`), the
RemoteAgent provider raises a typed `PlanLimitException` and records the member's wait (`IPlanLimits`); Autopilot
then starts nothing, retries nothing and spends no fix round until the reset (+1 minute; 30 minutes when the CLI gives
no time), logs one line in the Autopilot activity, shows it on Board › Autopilot and Board › Computers, and picks the
cut-short work up again afterwards. The wait is in memory: after a restart the next run meets the limit again (an
immediate answer) and the wait is back.

### Workflow › Ticket flow

The steps each ticket runs ([ADR-0014](adr/0014-ticket-flow-is-data.md)) are **data, not a setting key**: versioned
rows in `workspaces.ticket_flows` (per tenant; `BoardId` = a project's own flow, which wins over the tenant's), edited
by admins in Workflow › Ticket flow (members read-only) and read when a ticket starts. Nothing in `appsettings.json`.

| Step | Agent role | Can be off / filtered | On fail |
|---|---|---|---|
| Code | `IssueWork` — Coding (tickets) | no | handed back to Autopilot |
| Checks | — (CI + merge state) | no | back to Code (fix round) |
| Review | `Reviewer` | yes — *skip tickets labelled* / *only tickets labelled* | back to Code (fix round) |
| Test | `Acceptance` — Testing (tickets) | yes — same filters | back to Code (fix round) |
| Design | `Designer` | yes — **off by default**; once on, *only tickets labelled* `area:ui` | back to Code (fix round); needs-person waits for the owner |
| Merge | — (source control) | no | handed back to Autopilot |

**Design gate** ([ADR-0016](adr/0016-design-gate.md)) — no setting keys either. With Design on for a ticket, its Testing
run also takes Playwright screenshots (390 / 1280 px, light / dark) that the runner sends back to the tenant's file store
under `design:{board}:{issue}:{fix round}` (one folder per repo of the task) — fixed caps: 16 files, 5 MB each, 40 MB a
run, png / jpg / webp only, and at most 8 runs' worth (320 MB) in flight on the server at once. The Designer agent's
verdict and the owner's *Approve design* / *Ask for changes* are rows in `sessions.design_reviews` (RLS), written by
separate narrow updates so neither can erase the other. On a server provider the screenshots go as images within fixed
limits (≤ 16, ≤ 3.75 MB and ≤ 8000 px each, ≤ 32 MB together — from Anthropic's vision docs); an image over them is left
out and the prompt says why. The Designer reads `.claude/skills/*design*/SKILL.md` from the repo's default branch.
Autopilot merges the ticket only after a pass or an owner approval; changes count on the task's fix rounds; a check
that couldn't run is re-run automatically up to 2 times once a computer is connected (*Check again* runs it by hand).

Refused on save **and** at run time (the run then uses the built-in flow and the Autopilot activity says why): a flow
that doesn't start at Code / end at Merge, turns Checks off or limits it, lets any ticket reach Merge with neither
Review nor Test (every combination of the filter labels is checked), runs Design for a ticket that doesn't run Test,
loops other than *fail → Code*, leaves a step
unreachable, puts steps out of order, or names an agent that can't run the step. Fix rounds stay
`Autopilot:MaxFixRounds` on the task's persisted count. Every change is audited (`ticketflow.changed`) and can be
reverted; a task records the version it started with (`sessions.sessions.TicketFlowVersion`) and keeps it.

### Settings → Platform (platform admins)

**Run limits (per tenant)** — `Pipeline:Limits:MaxConcurrentRuns` (default `4`) and `Pipeline:Limits:RunsPerMinute`
(default `30`): admission for every Pipeline, Workflow and Quick run, so one tenant's burst or parallel load can't
starve the others. Separate from the monthly budget cap, which still applies.


| Key | Default | Notes |
|---|---|---|
| `RemoteAgent:AutoApprove` | `false` (Production locked) | Unattended remote execution. |
| `Tools:EnforceByDefault` | `true` in Production, else `false` | Fail-closed tool policy. |
| `Workspaces:AllowedHosts` | empty | Self-hosted GitHub Enterprise / Azure DevOps Server hosts, comma-separated (atop `SsrfGuard`; public provider hosts always allowed). |
| `Integration:BuildVerifier:Enabled` | `false` in Production | Lets agents compile generated code. |
| `Integration:BuildVerifier:Sandbox` | `InProcess` (Dev) | `Container` = ephemeral no-network `docker run`. Locked to `Container` in Production. |
| `Integration:BuildVerifier:ContainerImage` | `mcr.microsoft.com/dotnet/sdk:10.0` | Allow-listed image reference — never a docker flag. |
| `Integration:BuildVerifier:CpuLimit` / `MemoryLimit` / `PidsLimit` | `2.0` / `1g` / `256` | Container quotas. |
| `Integration:BuildVerifier:TimeoutSeconds` | `90` | Process tree killed on overrun. |

### Other per-tenant settings

- **Tool allowlist** — Policy app (`tools/allowlist`).
- **GitHub** (`Github:Pat`, `RepoOwner`, `RepoName`, `BaseBranch`) — Settings, encrypted per tenant.
  PAT must start `ghp_`/`github_pat_`/`gho_`.
- **Per-viewer preferences** (theme, wallpaper, glass, clock, *Animations*) — System → Appearance,
  stored in the browser only.

## Legacy `/settings` endpoint

`POST /settings` (admin) still accepts exactly `Llm:ForceProvider`, `Llm:Claude:ApiKey`,
`Llm:AzureOpenAi:ApiKey`, `Llm:AzureOpenAi:Endpoint`, `Github:Pat`, `Github:RepoOwner`,
`Github:RepoName`, `Github:BaseBranch` (`SettingsKeyRegistry`); anything else → 400. It writes the same
tenant rows the registry reads. New settings are added as descriptors, not to this allowlist.

## Notes

- Environment detection for Development-only defaults reads `ASPNETCORE_ENVIRONMENT`, then
  `DOTNET_ENVIRONMENT`.
- **Removed (C.6):** the `Llm`, `Agents` and `Pipeline` sections of both hosts' `appsettings.json`
  (they duplicated the code defaults and disagreed on provider casing). An existing deployment that set
  them via env vars keeps working — those values are now the *seed* tier.
- **Removed (0.6.0):** `Auth:Bearer` and `Pipeline:EnableHumanInTheLoop` (never read).
