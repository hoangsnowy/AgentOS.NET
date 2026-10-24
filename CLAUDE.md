# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**AgentOS.NET** — a .NET-native multi-agent AI platform for the software development lifecycle. A central orchestrator coordinates 5 specialist agents — Orchestrator, Requirement, Coding, Testing, QA — in a **Leader-Specialists-Quality Loop** (QA scores requirement↔code↔test consistency and loops until convergence or an iteration cap `NMax`). Hybrid, provider-agnostic LLM gateway (Anthropic Claude + Azure OpenAI + Microsoft Agent Framework + a paired dev-machine agent); providers/models are assigned per agent in the desktop **Settings → Agent profiles** (defaults in code; `appsettings.json` is bootstrap-only — see [docs/CONFIG.md](docs/CONFIG.md)). A Blazor Server **AgentOS** desktop-style UI drives the engine. Full context in [README.md](README.md). For local setup, secrets, and GitHub Actions, see [docs/SETUP.md](docs/SETUP.md).

The platform is a **modular monolith**: each feature is a self-contained `IModule` with its own DI surface, EF Core context, and Postgres schema, so any module can later ship as a standalone NuGet package. Framed as a credible OSS product, not a thesis demo.

## Commands

```bash
# Build / test (slnx-rooted, from D:\LuanVan\prototype)
dotnet restore AgentOs.slnx
dotnet build   AgentOs.slnx --configuration Release
# xunit.v3 4.0.0 is Microsoft.Testing.Platform-native — `dotnet test` runs in MTP mode
# (opted in via global.json "test.runner"): pass the solution with --solution, and use xunit's
# --filter-class / --filter-method instead of the VSTest --filter "FullyQualifiedName~..." syntax.
dotnet test    --solution AgentOs.slnx --configuration Release

# Single test class / method (MTP filter flags)
dotnet test --project tests/AgentOs.Tests --filter-class "*ToolGatewayTests"
dotnet test --project tests/AgentOs.Tests --filter-method "*ToolInvocationTests.Request_Validate_AllFieldsValid_DoesNotThrow"

# Run the API locally — Scalar UI at https://localhost:5080/scalar/v1
dotnet run --project src/AgentOs.Api

# Local secrets (DO NOT commit). UserSecretsId = "agentos-prototype"
cd src/AgentOs.Api
dotnet user-secrets set "Llm:Claude:ApiKey"      "sk-ant-..."
dotnet user-secrets set "Llm:AzureOpenAi:ApiKey" "..."
dotnet user-secrets set "Llm:AzureOpenAi:Endpoint" "https://<resource>.openai.azure.com"

# Run the Web (Blazor AgentOS.NET desktop) locally
dotnet run --project src/AgentOs.Web

# One-shot local dev — Aspire AppHost wires Postgres + Keycloak + MailHog + API + Web
dotnet run --project infra/AgentOs.AppHost

# Direct API run without Aspire — provide a Postgres connection string yourself
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=agentos;Username=postgres;Password=postgres"

# Generate a new EF migration — migrations are PER-MODULE (each module is its own startup project,
# owns its DbContext + schema + __EFMigrationsHistory). Example for the Pipeline module:
dotnet ef migrations add <Name> \
  --project src/AgentOs.Modules.Pipeline --startup-project src/AgentOs.Modules.Pipeline \
  --output-dir Persistence/Migrations --context PipelineDbContext

# UI E2E (Playwright) against the standalone Web — what CI runs
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AgentOs.Web -c Release --no-build --urls http://localhost:5199 --no-launch-profile
RUN_AGENTOS_E2E=true AGENTOS_URL=http://localhost:5199 dotnet test --project tests/AgentOs.E2E.Tests -c Release

# Lab server — long-running full stack for REAL GitHub / Azure DevOps / LLM tests, side by side with dev
# (own worktree ..\AgentOs-lab, own ports/volumes, own hostnames). See docs/LAB.md.
pwsh infra/lab/lab.ps1 up | update [-Ref origin/<branch>] | status | test | down | reset
```

CI: `.github/workflows/ci.yml` runs `restore → build Release → test` on Ubuntu, on push to `main`/`develop` or PR to `main`.

## Architecture

Modular monolith. Solution file is **`AgentOs.slnx`** (the .NET 10 XML format). Each module is an `AgentOs.Modules.*` class library that **references `AgentOs.Domain` + `AgentOs.SharedKernel` only**; cross-module references are explicit and minimal (e.g. `Llm → AppConfig`, `Integration → Tools`). Hosts (`Api`, `Web`) reference every module and wire them with one `services.AddModulesFromAssemblies(cfg, …)` call + `await app.Services.InitializeModulesAsync()` + `app.MapModuleEndpoints()` — no per-module wiring in `Program.cs`.

| Project | Role |
|---|---|
| `AgentOs.Domain` | Provider-neutral DTOs (`LlmRequest`, `LlmResponse`, `LlmOptions`), pipeline artifacts (`RequirementSpec`, `CodeArtifact`, `TestArtifact`, `QaReport`, `PipelineResult`), tool contracts (`ITool`, `IToolPolicy`, `IToolInvocationLog`, `IToolGateway`), interfaces (`ILlmClient`, `ILlmClientFactory`), exceptions. References the .NET BCL only. |
| `AgentOs.SharedKernel` | `IModule` / `IEndpointModule` / `IInitializableModule` contracts + `ModuleLoader` (reflection) + cross-cutting `ITenantContext` + `IAuthTokenProvider`. |
| `AgentOs.Modules.AppConfig` | Encrypted runtime KV store (DataProtection), `AppConfigDbContext` (schema `config`). Powers per-tenant LLM key overrides + the Settings UI. |
| `AgentOs.Modules.Llm` | Provider-agnostic gateway: `LlmClientFactory` + keyed `ILlmClient` per provider, `PooledChatLlmClient` (multi-key pool + 429 failover), `CostCalculator`, `AIToolFunction` (ITool→AIFunction adapter). |
| `AgentOs.Modules.Pipeline` | 5 agents + prompts + `PipelineOrchestrator` (+ optional MAF workflow engine) + `PipelineDbContext` (schema `pipeline`). |
| `AgentOs.Modules.Identity` | Keycloak JWT auth + `HttpTenantContext` (reads the `tenant` claim) + `Admin`/`Member` authorization policies. No DbContext, no module `/auth` route — login is OIDC/cookie via the host. |
| `AgentOs.Modules.Tenants` | Tenant registry + Keycloak admin client (member lifecycle) + signup/invitations + audit, `TenantsDbContext` (schema `tenants`). |
| `AgentOs.Modules.Tools` | `IToolRegistry`, `IToolPolicy` (per-tenant gate), `IToolInvocationLog` (evidence), `IToolGateway` (the policy→invoke→log seam). |
| `AgentOs.Modules.Integration` | `IGitHubPrService` (Octokit) + `IBuildVerifier` (`dotnet build` in a temp dir), exposed as ITools. |
| `AgentOs.Modules.Workspaces` | Connected source workspaces (repo/token registry) + `IWorkspaceConnector`, `WorkspacesDbContext` (schema `workspaces`). |
| `AgentOs.Modules.Sessions` | Remote-runner registry + pairing handshake + session-run feed (`IRunnerPairingService`, `ISessionRunFeed`), `SessionsDbContext` (schema `sessions`). |
| `AgentOs.Modules.Mcp` | MCP client (consume external tool servers) + server adapter; Api also serves MCP at `/mcp`. |
| `AgentOs.Modules.RemoteAgent` | SignalR hub + transport + `RemoteAgentLlmClient` — dispatches work to a paired dev-machine agent (zero server API tokens). |
| `AgentOs.Api` | ASP.NET Core minimal API (REST + Scalar UI + `/mcp`). A composition root. |
| `AgentOs.Web.Shell` | Shared Blazor shell + design-system UI kit: the GNOME window manager + per-circuit **scoped** state (`WindowManagerService`, `ToastService`), the app-shell host (`WindowHost`/`AppFrame`/`Taskbar`/`ToastHost`), the `IAppRegistry`/`DesktopApp` contracts, and the `Btn`/`Field`/`Panel`/`Icon` primitives. Referenced by `AgentOs.Web`. |
| `AgentOs.Web` | Blazor Server **AgentOS.NET** desktop — a composition root hosting the authentic-GNOME shell (empty wallpaper, Dash, Activities overview, virtual workspaces) on `AgentOs.Web.Shell`, plus the product apps (Overview, Agents pipeline, Workflow, Board, Settings hub, Terminal). |
| `AgentOs.RemoteAgent` | Standalone dev-machine agent executable that pairs to the RemoteAgent hub. |
| `infra/AgentOs.AppHost` | .NET Aspire orchestration (Postgres + Keycloak + MailHog + Api + Web). |

### LLM Gateway (critical pattern)

The 5 agents **do not call vendor SDKs directly** — they depend on `ILlmClient` (Domain). `LlmClientFactory.Create(name)` / `.CreateDefault()` resolve a keyed `ILlmClient` registered under its canonical provider name. Providers:

- **`Claude`** / **`AzureOpenAI`** — `PooledChatLlmClient`: a pool of `Microsoft.Extensions.AI` `IChatClient` instances (Anthropic.SDK / `Azure.AI.OpenAI`) keyed by API key, with round-robin + HTTP 429 cooldown failover across the (runtime override + appsettings) key pool.
- **`MAF`** — Microsoft Agent Framework `ChatClient` (`Microsoft.Agents.AI`).
- **`RemoteAgent`** — dispatches to a paired dev-machine agent over SignalR (owned by `Modules.RemoteAgent`).

Provider selection: `Llm:ForceProvider` / per-agent `Agents:<Name>:Provider` + runtime overrides from the Settings UI (hydrated per request from tenant-scoped `AppConfig`). Cost: `CostCalculator.Calculate(model, in, out[, cacheRead, cacheWrite])` looks up a built-in price table (Claude Fable/Opus/Sonnet/Haiku incl. 5.5, GPT-6/5.x/4.1/4o + o-series; official list prices 2026-10-17, incl. prompt-cache rates and **long-context tiers** — Haiku 5.5 >100k prompt, GPT-5.4+/6 >272k) by **exact id** in a `FrozenDictionary`; dated snapshots resolve to their base id unless the snapshot is itself priced. No loose prefix match (`gpt-4.1` must not price `gpt-4.1-mini`). Unknown model → **fail-closed** at a fixed $15/$75 rate (never `$0`, so it can't bypass the budget gate) + a warning; `IsKnown` tells callers which. `ModelCapabilities.TemperatureFor` omits `temperature` for models that reject sampling params with a 400 (Claude Opus 4.7+/Sonnet 5+/Haiku 5.5/Fable, GPT-5+/o-series) — only an allow-list of legacy models receives it.

### Tools, policy & evidence (governance)

Agents call **tools** through the gateway: `LlmRequest.Tools = ["build_verifier"]` makes `PooledChatLlmClient` resolve each name via `IToolRegistry`, adapt the `ITool` into an `AIFunction` (`AIToolFunction`), and run the tool-call loop via `FunctionInvokingChatClient`. **Every** invocation passes through `IToolGateway` (`DefaultToolGateway` in Domain) → `IToolPolicy` gate (default-permissive; production reads a per-tenant allowlist) → `ITool.InvokeAsync` → `IToolInvocationLog` (evidence; refusals + errors recorded too). `IToolGateway` is the single server-side seam so remote/off-box execution enforces identical governance. MCP: `Modules.Mcp` registers upstream MCP server tools under `{server}.{tool}`; the Api serves AgentOS.NET's own pipeline as MCP tools at `/mcp`.

### Persistence & multi-tenancy

`ConnectionStrings:DefaultConnection` (Postgres) is the only required wiring; **each module attaches its own DbContext + schema + migration history** (`pipeline.*`, `tenants.*`, `config.*`) and applies migrations at startup via its module init hook. Without a connection string (CI / local), modules fall back to no-op repositories so the app boots stateless. Row-level isolation: tenant-scoped entities carry a `TenantId` + an EF global query filter reading `ITenantContext.TenantId`. Auth is Keycloak-only: the Api validates RS256 bearer tokens against the realm JWKS, the Web uses cookie + OIDC (or a Development-only `Auth:DevAutoLogin` fixed-user handler for standalone runs); a single `HttpTenantContext` reads the `tenant` claim and falls back to the default tenant for anonymous requests (no `Auth:Mode` switch, no HS256). Local Postgres via Aspire AppHost; Azure deploy via `azd up` (Container Apps).

## Conventions

**.NET 10 / C# 14**, pinned via `global.json`. `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest-recommended`, `InvariantGlobalization=true` (Api overrides to `false`).

**Analyzer suppressions** (see `Directory.Build.props`): `CA1848` + `CA1873` solution-wide (LoggerMessage delegates) — remove before v1.0. The test project additionally suppresses `CA1707`, `CA1816`, `CA1859`, `xUnit1051`. Modules expose internals via `InternalsVisibleTo("AgentOs.Tests")` where tests need them; `.editorconfig` marks `**/Migrations/*.cs` as `generated_code`.

**CodeQL parity — catch it at build, not in the security tab.** Code scanning only reports *after* a push, so any CodeQL query with a compiler-side twin is enforced locally instead: `.editorconfig` sets `IDE0004` (redundant cast → `cs/useless-upcast`, `cs/useless-cast-to-self`), `IDE0035` (unreachable code), `IDE0059` (unused assignment → `cs/useless-assignment-to-local`) and `IDE0060` (unused parameter) to `warning`, which `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` promote to build errors. **When a new code-scanning alert lands, don't just fix the site — add its Roslyn twin to that block so the class can't recur.** When the query flags a specific *API* with no IDE rule, ban it instead: `BannedSymbols.txt` (repo root, `Microsoft.CodeAnalysis.BannedApiAnalyzers` as a global package → `RS0030` error) — e.g. `cs/path-combine` bans every `Path.Combine` overload in favour of `Path.Join`. Queries with no compiler equivalent (taint/dataflow: log forging, path traversal, SSRF) still rely on the CodeQL workflow; fix those in code, never dismiss (see the `codeql-conventions` memory for the burn-down history + the ~22 irreducible FPs).

**Data access — EF Core / LINQ by default, no ad-hoc raw SQL.** Business reads and writes go through the
module's DbContext (its global tenant filter applies); the existing Dapper read fast paths are the measured
exception and use `OpenWithTenantAsync`. Raw SQL is allowed only where EF cannot express it — DDL (the RLS
bootstrap), system-catalog reads (`pg_*`, migration history), generic catalog-driven cross-module operations
(tenant export/purge) — and then it is parameterized (never concatenated input; identifiers come from the
catalog and are quoted), kept in one class per module with a comment saying why EF can't, and covered by a
real-Postgres test.

**Test stack**: xUnit **v3** (`xunit.v3`), Shouldly (NOT FluentAssertions — v8 went commercial), NSubstitute. Naming: `MethodName_StateUnderTest_ExpectedBehavior`.

**Verification (E2E in the running app — unit-green is necessary, NOT sufficient)**: a change with any user-facing value is not "done" until it (1) is wired into the **AgentOS.NET Web desktop** (registered in `AppCatalog` with its `ComponentType`, which `WindowHost` renders via `DynamicComponent`), not Api-only, and (2) has been exercised in the *running app* — open the app, run the flow, capture a screenshot as proof. **Every host must run standalone with one command and no external services in Development** — the Web *is* the app; if it can't run without the full Keycloak+Postgres stack, it's broken. `dotnet run --project src/AgentOs.Web` boots the desktop via `Auth:DevAutoLogin` (a Development-only fixed-user auth handler, hard-thrown if ever set outside Development, and forced off under AppHost so the full stack uses real Keycloak); modules fall back to no-op repos with no DB. The UI E2E (`tests/AgentOs.E2E.Tests`, gated by `RUN_AGENTOS_E2E=true`, pointed at `AGENTOS_URL`) runs against that standalone Web. **Standalone proves only the _degraded_ path — match the verification surface to what the change touches.** With `DevAutoLogin` + no-op repos, anything that needs persistence, identity, or multi-tenant silently no-ops (a runner "registers" but is never saved; counters stay 0) or throws (Keycloak admin calls); a green standalone screenshot is NOT proof for those features. Rule: pure UI/layout → standalone screenshot is sufficient; **persistence / auth / tenant → verify on the full Aspire stack** (`dotnet run --project infra/AgentOs.AppHost`; dashboard `:15050`, Web `https://localhost:5180`, seeded login `operator`/`operator` on realm `agentic`) **and hand the user a drivable URL + creds so they can confirm too — never sign off a real-service feature on a surface only you can drive.** Per-circuit UI state (WindowManagerService, ToastService) is **scoped**, never singleton, or it bleeds across users. Run the full stack with `dotnet run --project infra/AgentOs.AppHost` (Aspire wires Postgres + Keycloak + Api + Web). Note the circuit constraint: a Blazor interactive-Server component has no `HttpContext`, so it can't use `ITenantContext` — read the tenant from `AuthenticationState` and pass it explicitly, or call a tenant-explicit service. Do NOT defer a feature's UI to a far-off milestone without explicit sign-off — every milestone must show something in the desktop.

**Full-stack (Aspire) run — make it work the FIRST try; these are KNOWN landmines, not "small bugs" to rediscover.** `dotnet run --project infra/AgentOs.AppHost` (dashboard `:17050`, Web `https://localhost:5180`, login `operator`/`operator`, realm `agentic`). Before debugging deeper, rule these out — each has a one-line fix:
- **Postgres "Running (Unhealthy)" + Web shows "Finished" (crashed), Api stays up** → a **stale `postgres-data` volume**: Postgres bakes the superuser password at initdb and never updates it, so a volume from an earlier run rejects the current pinned `PostgresPassword` (`FATAL: password authentication failed`) → the Web dies in `InitializeModulesAsync`. Fix: `docker volume rm agentos.apphost-*-postgres-data` (password is pinned to `postgres` in the AppHost, so it won't recur).
- **Keycloak login shows a stale realm name / old theme** → a **stale `keycloak-data` volume**: `WithRealmImport` skips re-import when the realm already exists. Fix: `docker volume rm agentos.apphost-*-keycloak-data`. (The realm displayName + login theme are baked from `infra/keycloak/`; the repo is the source of truth.) The same applies to **new realm roles** (e.g. `platform-admin`): an existing realm never gets them from the import — the azd postdeploy hook ensures `platform-admin` exists (idempotent); locally either drop the volume or create + assign it via the KC admin API (keep role descriptions ASCII — a `—` made the admin API return 400).
- **HTTP 431 (Request Header Fields Too Large)** → `localhost` shares ONE cookie jar across every dev app on the box → the Cookie header overflows the server header limit. Fixed in code: Keycloak `QUARKUS_HTTP_LIMITS_MAX_HEADER_SIZE=128K` (AppHost) **and** Kestrel `MaxRequestHeadersTotalSize = 128 KB` (`ServiceDefaults.AddServiceDefaults`, covers Web + Api). But cookies are **host-scoped, not port-scoped** — every dev app on `localhost` (any port) piles cookies into the one jar sent to KC `:8080`, so a heavy box can still exceed the limit; the real fix is browser-side: an **incognito** window (empty jar) or clear `localhost` cookies (DevTools → Application → Clear site data, or `chrome://settings/content/all` → search `localhost`). Also enable `chrome://flags/#allow-insecure-localhost` for the self-signed dev cert. The lab stack avoids the whole problem with its own `*.dev.localhost` hostnames (see Lessons learned).
- **Verify the Web/OIDC chain is actually up** (not just the container): `curl -k https://localhost:5180/account/login` must return **302** to `…/realms/agentic/protocol/openid-connect/auth?…request_uri=urn:ietf:…` — the `request_uri=urn:` proves PAR succeeded (redirect_uri accepted). `http://localhost:5180` returns `000` (single-scheme, Kestrel owns 5180 directly). Full landmine catalogue + token-forwarding details live in the `keycloak-aspire-gotchas` memory.

**UI / design**: before adding or editing any screen/component, read [docs/design/design-system.md](docs/design/design-system.md) **or run `/design-review`** — it is the written contract. Reuse the **defined** vocabulary (`Btn`/`Field`/`Panel`/`Icon` components; the `prefs-*`, `.page-head`, `.settings-table` classes SystemApp uses; `var(--…)` tokens). **Never reference a CSS class that is not defined in `app.css` or a scoped `.razor.css`** — phantom classes render unstyled and were the root cause of past app layout breakage (the old Spine — now **Board** — and Users windows); `/design-review` greps `.razor` class tokens against defined CSS to catch them. Surface chrome outside the shell (the Keycloak login at `infra/keycloak/themes/agentos`) mirrors the same tokens + branding (it is **AgentOS.NET**, never "AgentOs").

**Design workflow — Claude Design is the SOURCE OF TRUTH; every UI change round-trips through it (NO repo-only UI).** Anything that touches the UI — a token, a component, a screen, shell chrome, a widget, a CSS class — must be reflected in the **Claude Design** project [AgentOS.NET Design System](https://claude.ai/design/p/bec4d21d-0fad-4d66-9abe-ed8c711b75c8) (Published + org Default), not left in the repo alone. Direction: **GNOME / Adwaita**. The canonical loop is:

1. **Design/iterate in Claude Design** (the project chat) — never start a re-skin by hand-editing repo tokens.
2. **Pull the generated design-system code DOWN** into `src/AgentOs.Web`: tokens → `app.css :root`, component specs/screens → the `Btn`/`Field`/`Panel`/`Icon` vocabulary. Use the **DesignSync** tool + **`/design-sync`** skill (incremental, component-by-component — never a wholesale replace). Auth: DesignSync needs design-system authorization — run **`/design-login`** (works on API-key sessions) or `/login` with a Claude subscription.
3. **`/design-review`** the pulled result (phantom-CSS + vocabulary check) — it must still satisfy `design-system.md`.
4. **Verify E2E** in the standalone Web (light + dark), **commit**, and **push any new/changed component, screen, token, or chrome UP to the Claude Design project** (DesignSync `finalize_plan` → `write_files`) — a UI change that only lands in the repo is **incomplete**. Pushed `.html`/`.jsx` carry a first-line `<!-- @dsCard group="…" -->` marker so the Design System pane indexes them.

Hand-editing the repo directly (Claude Design auth unavailable in a non-interactive session — `/design-login` needs an interactive terminal) is a **fallback**, not an exemption: you STILL push the same change up to Claude Design the moment auth is available (DesignSync `write_files`, or have the user run `/design-login` / use Claude Design's "Send to Claude Code Web"). The project is the source of truth, the repo is the pulled artifact; the two must never drift. Reference of what's already pushed: `tokens/*` + `components/{core,forms,feedback,surfaces,navigation,shell}/*` + `ui_kits/agentos/{home,activities,activities-search,settings,settings-routing,agent-profiles,settings-api-tokens,settings-audit-log,settings-data,settings-platform,settings-prompts,settings-autopilot,system-about,board,board-tasks,board-try-the-app,board-autopilot,board-knowledge,board-knowledge-practices,insights-delivery,workflow*}.html` (last sync 2026-10-24) + `guidelines/cards/shell-widgets.html`. Keep branding **AgentOS.NET**, one accent, responsive (≤1024 = full-screen sheets).

**Commits**: Conventional Commits in English (e.g. `feat(tools):`, `fix(llm):`, `chore(deps):`). Co-author Claude when pair-coded. CI fails if the Release build/test fails.

**Language**: code, comments, docs, and LLM prompts/output are English (the repo was standardized from Vietnamese). The agent system prompts (`Modules.Pipeline/Prompts/*.cs`) open with `"You are the <X> Agent …"` — this opening line is used as a routing key; keep the prompt and any matcher in sync if reworded.

**Branding**: the product + desktop UI are both **AgentOS.NET**. Code namespaces stay `AgentOs.*`. Do not reintroduce "Agent Studio" or the retired `AgenticSdlc.*` prefix. Renamed from "AgentOS" on 2026-10-18 (Agno ships an unrelated, much larger "AgentOS" in the same category — see docs/plans/competitive-parity.md); write the full **AgentOS.NET** in UI, docs, mail and pages, never bare "AgentOS". Wire/crypto identifiers keep the old spelling on purpose: the `X-AgentOS-Runner-*` headers (paired runners) and the DataProtection application name `"AgentOS"` (changing it orphans every encrypted secret).

**PR**: fill in `.github/PULL_REQUEST_TEMPLATE.md` (summary + test plan) on every pull request.

## Owner directives (non-negotiable — they define "done")

The owner's standing instructions. They apply to every change; the execution plan is
[docs/plans/agentic-sdlc-v2.md](docs/plans/agentic-sdlc-v2.md).

1. **Enterprise level.** Own the quality calls without asking: tokens not raw colours, accessibility
   (role/name, keyboard, `prefers-reduced-motion`), an opt-out for anything playful, tests at every layer,
   docs updated, verified in the running app (light + dark). A feature that only works on the happy path
   isn't done.
2. **Performance first — measure, then fix.** Use the right data structure / algorithm on every hot path
   and exploit .NET 9/10 (`FrozenDictionary`/`FrozenSet`, `Span`/`SearchValues`, `HybridCache`,
   source-generated regex/JSON/logging, `MapStaticAssets` + `@Assets`, pooled buffers, single-pass folds,
   dictionary indexes instead of rescans). Never guess at a slowdown: reproduce it with a probe (frame rate,
   long tasks, circuit frames/s, interaction latency), turn suspects off one at a time, fix the measured
   cause, add a regression guard. The DS&A playbook is §2.7 of the plan.
3. **Metrics first.** Every agent feature is measurable: tokens per agent split into **fresh input · cache
   read · cache write · output (· reasoning)**, cost per class, latency, outcome. Offline/estimated numbers
   are always labelled (`≈`, "not billed"), never shown as real usage.
4. **UI-first configuration** ([ADR-0011](docs/adr/0011-ui-first-configuration.md)). A new setting is a
   `SettingDescriptor` in the module's `ISettingsContributor`, read through `ISettings` per call — never a
   new `appsettings.json` key (a test fails the build if product sections reappear there).
5. **Multi-tenant by default.** Every row carries `TenantId`; knowledge/memory is strictly per tenant; no
   cross-tenant reads, caches keyed by tenant. Anything touching persistence/identity/tenancy is verified on
   the full stack with two tenants (plan 0.9 adds Postgres RLS).
6. **Latest technology, verified.** Check current versions/APIs before choosing (today's date, release
   notes) — don't rely on memory. Prefer provider-neutral abstractions (Microsoft.Extensions.AI/VectorData).
7. **Visualise everything.** Every capability has a desktop surface; data-heavy features (graphs, retrieval,
   tokens, runs) get a real visualiser with a table alternative. Designed in Claude Design first.
8. **Lively, but never at the cost of speed.** Agent characters + living icons are part of the product —
   and nothing loops at rest (only a running agent animates continuously), `transform`/`opacity` only.
9. **Real integrations are tested for real** on the lab server (docs/LAB.md), not only with fakes.
10. **Docs stay true.** Update README/CONFIG/SETUP/LAB/ADRs with the change; delete docs that are superseded
    instead of letting them rot.

## Lessons learned (found in practice — don't rediscover them)

- **Cookies are scoped to the host, not the port.** Two stacks on `localhost` (dev 5180 + lab 6180, their
  Keycloaks, any other local app) share one cookie jar → sessions overwrite each other, the jar overflows
  (HTTP 431, *"Correlation failed"* at `/signin-oidc`). Fix: own hostnames (`lab.dev.localhost`,
  `labkc.dev.localhost` — `*.dev.localhost` resolves to loopback and is covered by the ASP.NET dev cert) +
  per-instance cookie names (`Auth:CookiePrefix`). A failed OIDC callback restarts sign-in once
  (`AuthCookies`), never a developer exception page.
- **Frontend lag was rendering, not Blazor.** Circuit traffic was ~1 frame/s and there were no long tasks;
  idle `infinite` animations inside windows with a `backdrop-filter` took the desktop from 60 to 20 fps.
  `backdrop-filter: blur(0px)` still builds a backdrop layer — use `none` when glass is off. Guard tests
  enforce both. Link local assets with `@Assets[...]` (fingerprinted, brotli, immutable caching).
- **Token usage conventions differ per provider.** MEAI counts cached ⊂ input and reasoning ⊂ output;
  Anthropic's `input_tokens` EXCLUDES `cache_read`/`cache_creation`. Normalise **per provider turn,
  beneath `FunctionInvokingChatClient`** (`UsageNormalizingChatClient`) — the tool loop sums turns but keeps
  only the last turn's raw response.
- **Tests on shared stacks must leave zero residue.** A test that changes real tenant data (budget caps,
  seeded spend, registered runners with live tokens) snapshots and restores it in a `finally`; verify with a
  residue query after the suite.
- **A running app locks its build output.** Stop the standalone Web / AppHost before `dotnet build`, or the
  build fails with MSB3021 — and an E2E run against stale binaries proves nothing. The lab runs from its own
  git worktree so dev rebuilds never touch it.
- **Aspire:** `AddProject(..., launchProfileName: null)` drops the launch profile's
  `ASPNETCORE_ENVIRONMENT` — set it explicitly (the lab Api otherwise boots as Production and exits). An
  existing Keycloak realm is never re-imported — sync changes through the admin API (`lab.ps1` does).
- **Recurring CodeQL notes:**
  - `cs/linq/missed-where`: a `foreach` whose body is one `if (x is T)` → use `OfType<T>()`/`Where`.
  - `cs/linq/missed-select`: a loop body that only maps → use `Select`.
  - `cs/path-combine`: banned at build time via `BannedSymbols.txt` — use `Path.Join`.
  - `cs/useless-cast-to-self`: `foreach (x in (ReadOnlySpan<T>)[…])` → a `static readonly` array.
  - `cs/local-not-disposed`: CodeQL doesn't see `await using (x.ConfigureAwait(false))` → write `await using var x = …`.
  - `cs/catch-of-all-exceptions`: a "never throw" catch needs an explicit `when (ex is not OperationCanceledException)` filter.
  - `cs/dispose-not-called-on-throw`: disposables in tests go under `using`.
- **Row-level security (ADR-0012):** the dev/lab stack connects as the Postgres SUPERUSER, which bypasses RLS even
  when forced — every connection therefore `SET ROLE agentos_rls`. A new tenant table gets the policy by being
  added to `RowLevelSecurity.Tables`; its module context needs `.AddInterceptors(RowLevelSecurityInterceptor.Instance)`
  and Dapper reads use `OpenWithTenantAsync`. Desktop calls carry the tenant because `CircuitIdentityHandler` pushes
  it on every circuit event.
- **Blazor circuit handlers:** an inbound-activity handler must keep the circuit's dispatcher — `ConfigureAwait(true)`,
  never `false` (renderer thread affinity).
- **Live agent runs on the lab without an API key:** `pwsh infra/lab/lab.ps1 pair` pairs this machine's `claude -p`
  (prompt mode, no YOLO) and forces RemoteAgent for the lab tenant; `unpair` reverts it. Live runs need
  `AGENTOS_E2E_RUN_TIMEOUT_SECONDS` (e.g. 1200) on the Workflow real-auth test.
- **Editing gotchas:** shell heredocs and `sed` mangle escapes (`\n`, `\\`, `$`) in C#/JS/regex — use the
  Edit/Write tools or a small Node script with asserted replacements; preserve the file's line endings.
  Never put backticks inside a double-quoted shell string (`node -e "…`x`…"`): bash executes them as commands.
  Keep multi-line edit scripts in a file (Write) and run `node file.js` instead of inline `node -e`. In those
  scripts pass replacements as a function (`s.replace(a, () => b)`) or use `split/join`: a `$` followed by a
  backtick, `'`, `&` or a digit in a replacement string is a JS replace pattern and splices in other text.
  Never write `\n` (or any backslash escape) inside a JS template literal that carries C#/TS source: the
  template turns it into a real newline/character and splits the target's string literals — build such text
  with `String.fromCharCode(92)` or edit with the Edit tool.
  PowerShell `Invoke-WebRequest` treats an unfollowed 302 as an error — probe with `curl.exe`.
- **Secrets:** never type real tokens into a browser or the chat. Tokens live in user-secrets / env and are
  read by test code; the owner sets them in their own terminal.
