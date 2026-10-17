# AgentOS — Roadmap

> Where AgentOS is today and where it's going. AgentOS is a .NET-native, multi-tenant
> operating shell for AI software-engineering agents. Horizon: **12 months (Jun 2026 – Jun 2027)**,
> plus a beyond-12-months outlook.
>
> This is the product roadmap (milestones, positioning, risks). The engineering execution plan —
> metrics, shared memory, ecosystem, built-in executor, UI-first configuration, software-factory
> stations — is [docs/plans/agentic-sdlc-v2.md](docs/plans/agentic-sdlc-v2.md) (updated 2026-10).
> **Enterprise readiness** — the scorecard (26 areas, grounded in code) and the re-baselined
> E0 → E1 → E2 track — is [docs/plans/enterprise-readiness.md](docs/plans/enterprise-readiness.md)
> (review 2026-10-17). It replaces the old "Q3 — Enterprise-grade operations" paragraph.

## Contents

1. [Vision & positioning](#1--vision--positioning)
2. [Where it is today](#2--where-it-is-today)
3. [The 12-month roadmap](#3--the-12-month-roadmap)
4. [Open architecture decisions](#4--open-architecture-decisions)
5. [Technical pillars](#5--technical-pillars)
6. [Business model](#6--business-model)
7. [Risks & mitigations](#7--risks--mitigations)
8. [Definition of success](#8--definition-of-success)

---

## 1 — Vision & positioning

**AgentOS is a desktop-native, multi-tenant operating shell for AI software-engineering agents.**
Most agent frameworks are Python libraries you wire together in code. AgentOS is a running
*product*: a Blazor desktop environment on a .NET 10 backend that coordinates a team of
role-specialized agents — Orchestrator, Requirement, Coding, Testing, QA — across the software
development lifecycle, with a provider-agnostic LLM gateway, enterprise identity, per-tenant
governance, and evidence/audit built in.

### Competitive positioning

| Product | Form factor | Language | Focus | Multi-tenant / on-prem | Where AgentOS differs |
|---------|-------------|----------|-------|------------------------|------------------------|
| [AutoGen](https://github.com/microsoft/autogen) (Microsoft) | Python library | Python | General multi-agent conversation | No (library) | AgentOS is a deployed product with UI + identity, not a lib you assemble |
| [MetaGPT](https://github.com/geekan/MetaGPT) | Python framework | Python | "Software company" role agents | No | MetaGPT ends at code-gen scripts; AgentOS adds tenancy, audit, governance, UI shell |
| [CrewAI](https://github.com/crewAIInc/crewAI) | Python framework | Python | Role/crew orchestration | Partial (Enterprise SaaS) | .NET-native, on-prem-first, SDLC-specialized vs generic crews |
| [Devin](https://www.cognition.ai/) (Cognition) | Hosted SaaS | Closed | Autonomous SWE agent | Cloud only, closed | AgentOS is open, self-hostable, inspectable; Devin is a black box |
| [Cursor](https://cursor.com/) | IDE (VS Code fork) | Closed | In-editor coding assist | No (per-dev) | Cursor = single-dev IDE; AgentOS = team/tenant SDLC platform |

**The gap AgentOS fills:** every popular framework is either (a) a Python library where you're the
integrator, or (b) a closed SaaS. Nobody offers a **.NET-native, desktop-shell, multi-tenant,
on-prem-ready SDLC agent platform with governance and evidence built in.** That's a defensible niche
for organisations that (1) live in the Microsoft/.NET stack, (2) can't ship code to a third-party
cloud (finance, gov, healthcare), and (3) want to *govern and measure* agent output, not trust it blindly.

### Target personas

1. **Enterprise dev team** in a .NET shop — wants agent assistance but code can't leave the building. On-prem + Keycloak/Entra SSO is the unlock.
2. **Software consultancy** — many client projects, needs hard tenant isolation so client A never sees client B's code or prompts. Multi-tenant is the unlock.
3. **Internal platform / DevEx team** — wants to offer "agents as a service" to its own engineers with audit, cost control, and RBAC. Open-core + observability is the unlock.

---

## 2 — Where it is today

AgentOS is a working platform, not a sketch. Already shipped:

- **One engine, three surfaces** — Pipeline (sandbox story), Workflow (visual graph of the same 5
  agents, [#52](https://github.com/hoangsnowy/AgentOs/pull/52)), and Board (real ticket →
  PR, formerly "Spine") all run the same engine. The Workflow surface is re-platformed onto **Microsoft Agent
  Framework Workflows** — `GraphExecutor` compiles the drawn `PlanGraph` into a MAF `Workflow` run on
  the MAF in-process runtime (QA pass/fail become conditional edges), node work delegating to the
  real typed agents + the governed tool gateway
  ([#87](https://github.com/hoangsnowy/AgentOs/pull/87),
  [#100](https://github.com/hoangsnowy/AgentOs/pull/100)). Board tickets run either the **Quality** brain (full 5-agent pipeline
  with a greenfield router) or the **Quick** brain, with a user-facing toggle, cost label, and
  bidirectional cross-links between all three apps
  ([#54](https://github.com/hoangsnowy/AgentOs/pull/54),
  [#73](https://github.com/hoangsnowy/AgentOs/pull/73)).
- **5-agent SDLC pipeline** (Orchestrator/Requirement/Coding/Testing/QA) in a **Leader–Specialists–Quality loop** that iterates to convergence or an iteration cap.
- **Provider-agnostic LLM gateway** — Claude + Azure OpenAI via `Microsoft.Extensions.AI` `IChatClient`, plus Microsoft Agent Framework and a paired dev-machine **RemoteAgent** (zero server API tokens). Multi-key pool with 429 failover; cost calculation; per-tenant BYO LLM keys ([#71](https://github.com/hoangsnowy/AgentOs/pull/71)).
- **Safety baseline** — budget gate on every server-token entrypoint, per-tenant rate + concurrency
  limits, `runner_shell` output cap ([#55](https://github.com/hoangsnowy/AgentOs/pull/55));
  fail-closed tool-policy gates, tenant-filter freeze, and SSRF guard
  ([#72](https://github.com/hoangsnowy/AgentOs/pull/72)).
- **Modular monolith** — each feature is a self-contained `IModule` with its own DI surface, EF Core context, and Postgres schema; any module can later ship as a standalone package.
- **Multi-tenant identity** — Keycloak OIDC (RS256 only), tenant claim, `platform-admin` role, **row-level isolation** via an EF global query filter.
- **Governance & evidence** — every tool call flows through `IToolGateway` → per-tenant `IToolPolicy` → `IToolInvocationLog`.
- **Cloud hardening (in code)** — ForwardedHeaders behind TLS-terminating ingress, durable
  Postgres-backed DataProtection key ring, publish-mode environment gating, `RequireDatabase`
  fail-loud, Keycloak custom image + postdeploy realm hook
  ([#69](https://github.com/hoangsnowy/AgentOs/pull/69),
  [#70](https://github.com/hoangsnowy/AgentOs/pull/70)).
- **Tools & MCP** — GitHub PR service, build verifier, an MCP client (consume external tool servers), and AgentOS's own pipeline served as MCP tools.
- **Remote repo execution** — Workspaces + Sessions drive boards → tickets → AI coding sessions against connected repos.
- **Plugin system** — runtime-discovered `IAgentOsPlugin` extensions dropped into a `plugins/` folder, no compile-time reference.
- **Supply-chain + SAST** — Dependabot, dependency-review, SHA-pinned actions, least-privilege workflow tokens, CodeQL at 0 findings ([#56](https://github.com/hoangsnowy/AgentOs/pull/56), [#62](https://github.com/hoangsnowy/AgentOs/pull/62)–[#65](https://github.com/hoangsnowy/AgentOs/pull/65)).
- **Health probes** — `/health` + `/alive` mapped unconditionally for container orchestrators ([#58](https://github.com/hoangsnowy/AgentOs/pull/58)).

### Honesty ledger

Status vocabulary used throughout this document: **Shipped+verified** (exercised in a running
deployment), **Shipped, cloud-unverified** (in code and tested, but no real cloud round-trip yet),
**Planned**.

| Claim | Status |
|---|---|
| Cloud hardening (ForwardedHeaders, DataProtection, env gating) | **Shipped+verified** — live on Azure Container Apps since 2026-06-21 (first `azd up`) |
| Multi-tenant isolation | **Shipped+verified** at application level — a real-auth E2E creates a second tenant through `/signup` and proves it sees none of the first's boards, runners, evidence or runs, on the dev stack and the lab ([#192](https://github.com/hoangsnowy/AgentOs/pull/192)); cross-tenant tenant endpoints closed ([#193](https://github.com/hoangsnowy/AgentOs/pull/193)). **Not yet DB-enforced** — Postgres RLS is E0.1 |
| QA gate before PR | **Shipped** — a result becomes a PR only when QA passed on real model output, enforced in the PR service ([#193](https://github.com/hoangsnowy/AgentOs/pull/193)); human approval policies are E1.5 |
| Audit trail | **Shipped** — settings + config writes (redacted), members, invitations, sign-in; Settings → Audit log + CSV ([#194](https://github.com/hoangsnowy/AgentOs/pull/194)). Tamper-evidence + SIEM are E2.2 |
| Enterprise readiness | **Not yet** — no corporate SSO, 3 roles, no API tokens, no on-prem install, single replica; see [the review](docs/plans/enterprise-readiness.md) |
| `build_verifier` execution | **Sandboxed** — ADR-0005 Accepted ([docs/adr/0005](docs/adr/0005-build-verifier-sandbox.md)); Layer-1 input hardening (`BuildInputSanitizer`: build-control files rejected, project synthesized, feed cut) + Layer-2 OS isolation (`docker run --network none --read-only --user --cap-drop ALL`) shipped ([#96](https://github.com/hoangsnowy/AgentOs/pull/96)). **Cloud-unverified** — only the ACA-Jobs cloud round-trip on a real `azd up` remains |
| Scale-out (≥2 replicas) | **Unverified** — single-instance only until a 2-replica test (see Q2) |

---

## 3 — The 12-month roadmap

Sequencing is dependency-driven with one hard external commitment in the **Sep–Dec 2026** window.
The spine: **identity decision → `azd up` round-trip → tenant residue + secrets → demo hardening →
enterprise ops → OSS launch**. The `build_verifier` sandbox shipped in code as a parallel Q1 track
([#96](https://github.com/hoangsnowy/AgentOs/pull/96)) — only its ACA-Jobs cloud round-trip rides on
the Azure deployment. PR-level slicing for the current engineering work lives in
[docs/plans/agentic-sdlc-v2.md](docs/plans/agentic-sdlc-v2.md) (the agentic-SDLC execution plan, Phases 0–8 + C + F).

### Q1 (Jun–Aug 2026) — Live on Azure

- **Goal:** a clean `azd up` produces a working, secured AgentOS on Azure Container Apps — login,
  run, persist — with no seeded credentials and no in-process RCE.
- **Deliverables:**
  - **Identity ([D1](#4--open-architecture-decisions), decide month 1):** persistent Keycloak on
    managed Postgres per [docs/keycloak-prod-runbook.md](docs/keycloak-prod-runbook.md) —
    `KC_DB=postgres`, stable `KC_HOSTNAME`, `KC_PROXY=edge`, theme baked into the image, seed users
    stripped, secrets via `azd env` / Key Vault.
  - **Real `azd up` round-trip:** provision → configure → redeploy → smoke test — OIDC login on the
    real FQDN, an authenticated pipeline run that persists under the real tenant, realm + saved LLM
    keys surviving a restart.
  - **Sandboxed `build_verifier`, done properly ([audit #10](https://github.com/hoangsnowy/AgentOs/blob/90240386830067f52c07b1d781e223ccdfe5f174/docs/deploy-readiness-audit.md), archived) — ✅ shipped:**
    ADR-0005 ([D2](#4--open-architecture-decisions)) Accepted; the two-layer sandbox is in code
    ([#96](https://github.com/hoangsnowy/AgentOs/pull/96)) — Layer-1 input hardening
    (`BuildInputSanitizer`: model-authored `.csproj`/`.targets`/`.props` rejected, project file
    always synthesized, feed cut) + Layer-2 ephemeral per-build container, no egress
    (`docker run --network none --read-only --user --cap-drop ALL`), CPU/mem/PID/timeout quotas.
    Only the ACA-Jobs cloud round-trip on a real `azd up` remains.
  - **Multi-tenant residue (audit B4) — ✅ closed in code:** run-history repo tenant stamping (run +
    metric rows) and `AuthSession` null-`HttpContext` fail-open are fixed; the
    [#72](https://github.com/hoangsnowy/AgentOs/pull/72) `OrchestrationStore` per-tenant keying ships;
    only the full-stack two-tenant verification on the real cloud deployment remains.
  - **Secrets, first slice (F2) — ✅ shipped:** the dead HS256 secret + operator password are gone from
    `appsettings.json` (`DevSecretGuard` fails fast on any committed dev default); production secrets
    flow through azd Key-Vault-backed secret parameters (`AddParameter(secret: true)`).
  - **Low-severity security — ✅ shipped:** explicit workspace-connect `Host` allowlist
    (`Workspaces:AllowedHosts` / `WorkspaceHostPolicy`, defense in depth on top of the
    [#72](https://github.com/hoangsnowy/AgentOs/pull/72) connect-time `SsrfGuard`, gating connect +
    add-repo + the find-boards/repos probes); ownership check so a member cannot revoke another
    member's runner/session.
- **Exit criteria:** a documented `azd up` from a clean environment yields a deployment where an
  external user can log in via OIDC and complete a pipeline run that persists under their tenant;
  a malicious-`.csproj` fixture test proves the sandbox cannot reach the host or the network; zero
  default credentials anywhere in the deployed environment.
- **Deferred:** real SMTP ([D4](#4--open-architecture-decisions): realm `verifyEmail:false` for
  now), scale-out, observability backend, Entra/Okta SSO, repo-grounded Quality editing.

### Q2 (Sep–Nov 2026) — Defensible demo ⭐ hard deadline anchor

> External commitment lands in the Sep–Dec 2026 window. Everything in Q1 plus this milestone is the
> minimum defensible set; Q3/Q4 content can slip without breaking the commitment.

- **Goal:** the live deployment survives a hostile end-to-end demo and hard questions about
  architecture, cost, and isolation.
- **Deliverables:**
  - **Latency/UX:** LLM token streaming (`GetStreamingResponseAsync`) + prompt-keyed HybridCache
    (E5); per-shell-command streaming in session runs; "Find boards" picker.
  - **Operability:** deploy-time migrations with an advisory lock (E1) — ✅ shipped
    ([#79](https://github.com/hoangsnowy/AgentOs/pull/79)): `PgAdvisoryLock` serialises concurrent
    replicas racing the boot migration, wired into every module's migrate hook (Pipeline, Sessions,
    Tenants, Tools, AppConfig); observability backend wired (App Insights / OTLP exporter) so a demo
    run is visible as traces + cost telemetry.
  - **Scale-out honesty resolved:** run the 2-replica test; then either ship the backplane choice
    ([D3](#4--open-architecture-decisions)) or pin sticky sessions + `minReplicas=1` and *document*
    single-instance as the supported posture.
  - **Secrets, second slice (F2):** finish the `IRuntimeOverrides` reshape into an async secret
    provider — async key getters shipped in [#72](https://github.com/hoangsnowy/AgentOs/pull/72);
    retire the remaining sync bridge (unblocks Q3 secret rotation).
  - **Gateway robustness:** pooled-client wrapper leak, failover gaps (401/400/5xx) (CostCalculator
    unknown models now fail closed at $15/$75 — done).
  - **Defense-grade docs:** `docs/architecture.md` refresh; ADRs (0005 sandbox and 0011 UI-first config shipped; modular monolith, LLM
    gateway, multi-tenant isolation, one-engine-three-surfaces still to write — see docs/adr/README.md);
    `docs/FEATURE-MATRIX.md` — every module/app tagged Working / Degraded-standalone / Stub / Gap.
- **Exit criteria:** a scripted 15-minute demo on the live Azure deployment — login → board ticket →
  Quality run with streaming progress → PR on a real repo → cost + trace shown in the observability
  backend — executed by someone other than the author; FEATURE-MATRIX published with no "Working"
  claim that isn't verified.
- **Deferred:** RBAC depth, audit export, DR drill, marketplace, local models.

### Q3 (Dec 2026 – Feb 2027) — Enterprise track, re-baselined 2026-10-17

The 2026-10-17 review ([docs/plans/enterprise-readiness.md](docs/plans/enterprise-readiness.md)) found the old
Q3 list too thin to pass an enterprise security review: it had no corporate SSO, no machine access, no
approval gates, no on-prem path and no data lifecycle. The track now runs in three stages and **starts now**,
interleaved with the agentic-SDLC phases:

- **E0 — Pilot-safe (≈2 weeks):** Postgres RLS · DataProtection keys wrapped by KMS / certificate · evidence
  never silently dropped · Workflow *Human* node fails closed · runner command allowlist · per-tenant limits on
  the Web host · **repo instruction files** (`AGENTS.md`, `CLAUDE.md`, Copilot instructions) · an
  **OpenAI-compatible provider** (vLLM / Ollama / Foundry) for air-gapped tenants.
- **E1 — Enterprise pilot (≈6 weeks):** **SSO** via Keycloak brokering (Entra ID, Okta, ADFS, SAML) · **RBAC v2**
  (viewer / developer / approver / admin / auditor) · **API tokens + service accounts + `/v1` API** · tenant
  **export / purge / retention** · **approval policies** (plan, diff, PR, merge; branch protection) · prompts v2
  (all agents, versions, rollback, tenant rules) · Agent Skills · Azure DevOps boards + Jira + Teams/Slack ·
  **Helm + docker compose on-prem** · dashboards / alerts / SLOs · SBOM + signed images · threat model + DPA.
- **E2 — Enterprise GA (≈6 more weeks):** SCIM · tamper-evident audit + SIEM export · HA (≥2 replicas, HA
  Postgres, geo backups, executed DR drill) · GitLab / Bitbucket / webhooks · tenant-defined custom agents ·
  SOC 2 / ISO mapping + pen test · platform admin console + metering · WCAG 2.2 AA + i18n.
- **Exit criteria:** E1 — a customer tenant signs in with their own Entra ID, roles come from their groups, a
  ticket from their tracker becomes an *approved* PR under their policy, shaped by their rules and skills, on
  Azure or their own k8s, and they can export or purge everything. E2 — SCIM, HA + DR drill, audit to their
  SIEM, admin console + metering, SOC 2 mapping.

### Q4 (Mar–May 2027) — Open the doors: OSS launch & extensibility

- **Goal:** a stranger can adopt, run, and extend AgentOS without talking to the author.
- **Deliverables:** authoring guides (module / agent / plugin / tool / LLM provider);
  `CONTRIBUTING.md` rewritten as a 5–10-minute first-PR path; community skills pack
  (module-scaffold, tool-scaffold, lint-conventions + CI check); plugin contract polish +
  first-party sample plugins; engineering write-ups (why .NET agents, multi-tenant isolation, the
  sandbox story); launch push (README, screenshots, runnable examples).
- **Exit criteria:** first non-author issue and first non-author PR merged; a recorded cold-start —
  clean machine → running AgentOS + one custom plugin using only the docs.
- **Deferred:** marketplace, certified packs, hosted AgentOS Cloud.

### Beyond 12 months

No dates — named horizon items: AIOps cost optimization (routing, cheaper-model fallback, measured
against a naive always-biggest-model baseline); prompt-drift regression harness; (local models moved
up to E0.8 — OpenAI-compatible provider); RAG over the tenant's own codebase (pgvector, stays in
Postgres); event-sourced runs → replay + time-travel debugging; agent/prompt-pack marketplace with
quality gating; tiered isolation (row → schema → infra) as a pricing axis.

### Milestone map (old → new)

| Old milestone | Where it went |
|---|---|
| M1 — OSS polish | Q4 |
| M2 — Extensibility depth | Q4 + beyond |
| M3 — Enterprise-ready | split: Q1 (deploy + secrets) / Q3 (RBAC, audit, DR) / beyond (Okta, SOC 2) |
| M4 — Observability + AIOps | Q2 (backend + traces) + beyond (AIOps) |
| M5 — Ecosystem | beyond |

---

## 4 — Open architecture decisions

Decision log — recommended defaults, not prescriptions. Each gets decided (and recorded as an ADR
where marked) by its deadline; until then the roadmap assumes the default.

| ID | Decision | Options | Recommended default | Decide by | Blocks |
|---|---|---|---|---|---|
| **D1** | Identity provider in cloud | Persistent Keycloak-on-Postgres vs Entra External ID | **Persistent Keycloak** — [the runbook exists](docs/keycloak-prod-runbook.md), preserves Tenants-module admin provisioning + custom theme + on-prem parity; Entra would force a Graph rewrite and weaken the on-prem story | Q1 month 1 | everything in Q1 |
| **D2** | `build_verifier` sandbox host | ACA Jobs vs Docker-in-ACA (likely dead end — privileged containers unsupported) vs ACI per-exec container groups vs process-level jail (insufficient alone) | **ACA Jobs** with egress disabled + CPU/mem/timeout quotas; local dev runs the same image via Docker under Aspire; project-synthesis + locked-feed defenses apply regardless of host | Q1 month 1 (ADR-0005) | Q1 sandbox implementation |
| **D3** | Blazor Server scale-out | Azure SignalR backplane vs ACA sticky sessions + `minReplicas=1` | **Sticky sessions + min 1 replica** through the deadline window; adopt Azure SignalR only when the 2-replica test proves a real need | Q2 | E2; honest scale claims |
| **D4** | Email | Defer (`verifyEmail:false`) vs ACS Email / SendGrid now | **Defer in Q1**, wire real SMTP in Q3 | Q1 (defer) / Q3 (wire) | signup verification, invitations |
| **D5** | Web↔API topology | Web in-process (today's reality) vs Web→API over HTTP (the API container is currently deployed but never called by Web) | **In-process for the deadline**; record the split as the scale path; stop deploying the unused API container or wire it — don't ship dead topology | Q2 | RemoteAgent hub reachability in cloud |

---

## 5 — Technical pillars

Long-term architecture commitments — where we are → where we go.

- **Orchestration** — *Now:* 5 agents in `AgentOs.Modules.Pipeline`, executed as a sandbox story (Pipeline), a visual graph (Workflow `GraphExecutor`), or a real ticket (Board, formerly "Spine") with two brains — Quality (5-agent) and Quick — behind one toggle. *Next:* user-defined agents via plugins; richer graph routing (conditional, parallel fan-out); no lock-in in the orchestration layer (our contracts, MAF/SK as one engine, not the only one).
- **LLM abstraction** — *Now:* Claude + Azure OpenAI + MAF + RemoteAgent behind `ILlmClient`/`ILlmClientFactory`, per-tenant BYO keys. *Next:* token streaming + response cache (Q2); **local models** (Ollama, vLLM) for fully air-gapped on-prem; capability negotiation (tool-calling/vision/context window) so the router picks per task. Rule: **always ≥2 providers wired.**
- **State** — *Now:* per-module Postgres contexts (modular monolith). *Next:* event-sourced agent runs → replay + time-travel debugging; snapshot a run, branch it, re-run with a different prompt/model (doubles as the prompt-drift regression harness).
- **Observability** — *Now:* OTel wired (spans + metrics) but no backend; structured logs drive `cost-report`. *Next:* exporter to App Insights / OTLP (Q2); OpenTelemetry GenAI semantic conventions; cost telemetry as a first-class signal, not a log scrape.
- **Security** — *Now:* Keycloak OIDC, JWT, tenant row-level isolation, per-tenant rate limit + budget gate + `runner_shell` cap, fail-closed tool-policy gates, SSRF guard, CodeQL SAST at 0; **sandboxed code execution** shipped — ADR-0005 + a two-layer `build_verifier` sandbox (input hardening + ephemeral no-egress quota'd container, [#96](https://github.com/hoangsnowy/AgentOs/pull/96)), with the ACA-Jobs cloud round-trip pending (see [D2](#4--open-architecture-decisions)). *Next:* zero-trust between modules, prompt-injection defense, and output filtering (PII/secrets).
- **Multi-tenant** — *Now:* row-level isolation via tenant context + isolation tests. *Next:* tiered isolation — row-level → schema-per-tenant → isolated infra per tenant; sell isolation level as a pricing axis.
- **Knowledge** — *Now:* agents are stateless beyond prompt context. *Next:* RAG over the tenant's own codebase + docs (pgvector, stays in Postgres); citation tracking so a claim links back to a source line.
- **Tools** — *Now:* MCP client + server, GitHub PR service, build verifier, `runner_shell`. *Next:* broader MCP tool catalog, browser automation for agents that verify a running app, richer native API calls.
- **UI** — *Now:* Blazor desktop shell, interactive circuits. *Next:* SSR + interactive islands for faster first paint; a mobile read-only view (check a run from your phone); a **CLI** for CI/headless use — same backend, no browser.

---

## 6 — Business model

| Model | Free | Paid | Pros | Cons |
|-------|------|------|------|------|
| **OSS-first** (sponsor) | Everything | Goodwill | Max adoption | Revenue weak/unpredictable for a small team |
| **Open core** | Core agents, single-tenant, basic auth | SSO, fine-grained RBAC, audit/compliance, schema/infra isolation, support | Adoption *and* a clear paid line | Must police the core/enterprise boundary |
| **SaaS** | Trial | Hosted seats | Recurring revenue | On-prem buyers (the whole point) can't use it |
| **Consulting** | The OSS | Time | Immediate cash + user insight | Doesn't scale |

**Recommendation — open core.** The target personas (on-prem .NET enterprises, consultancies)
*cannot* use SaaS — keeping code in-house is exactly why they'd pick AgentOS, which rules out pure
SaaS as the primary model. The features enterprises pay for (SSO/Entra, fine-grained RBAC,
audit/SOC 2, infra-level isolation, priority support) are precisely the ones that should be gated —
and they're the Q3-and-beyond roadmap. Start OSS for adoption, introduce the enterprise edition when
those features land. Consulting fills the gap and feeds requirements. A hosted "AgentOS Cloud" stays
a *later optional* offering, not the core bet.

---

## 7 — Risks & mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| **LLM cost explosion** | A runaway loop burns the budget in hours | ✅ per-tenant/per-run budget gate + rate limit (shipped); cheaper-model routing (beyond-12mo); prompt-keyed cache (Q2) |
| **Agent loop / hallucination** | Confidently wrong code, infinite loops | ✅ QA loop + max-iteration cap; ✅ no PR unless QA passed on real output ([#193](https://github.com/hoangsnowy/AgentOs/pull/193)); approval policies (E1.5); a quality benchmark in CI (plan eval harness) |
| **Multi-tenant data leak** | Tenant A sees tenant B → trust dead, legal exposure | ✅ row-level isolation + fail-closed gates + tenant-filter freeze; ✅ two-tenant real-auth E2E ([#192](https://github.com/hoangsnowy/AgentOs/pull/192)); ✅ platform-only tenant endpoints ([#193](https://github.com/hoangsnowy/AgentOs/pull/193)); Postgres RLS (E0.1); tiered isolation for high-assurance tenants |
| **Enterprise buyer says no** | Security review fails on SSO / RBAC / approvals / on-prem | The E0 → E1 → E2 track ([review](docs/plans/enterprise-readiness.md)); E0 items are each small and start now |
| **Sandbox escape** | Agent-run code touches the host | ✅ ADR-0005 + two-layer sandbox shipped ([#96](https://github.com/hoangsnowy/AgentOs/pull/96)): Layer-1 input hardening + Layer-2 ephemeral no-egress container (`--network none --read-only --cap-drop ALL`) with CPU/mem/PID quotas; fail-closed tool-policy gates; ACA-Jobs cloud round-trip pending |
| **Hard external deadline (Sep–Dec 2026)** | A slipped milestone breaks an external commitment | Q1+Q2 scope is the minimum defensible set; Q3/Q4 content is explicitly off the critical path and can slip without breaking the commitment |
| **Prompt drift** | Yesterday's good prompt silently regresses | Version prompts v1→vN; snapshot fixtures; regression test in CI (`prompt-tune` scores variants) |
| **Vendor lock-in** | A provider changes pricing/API | ✅ `IChatClient` abstraction + ≥2 providers wired; add a local model (Ollama/vLLM) as the always-available floor |
| **Prompt injection** | Malicious input hijacks an agent | Strict template boundaries (untrusted input never concatenated into the system prompt); input/output classification; secret/PII output filter |

---

## 8 — Definition of success

- **Near term (the Q2 anchor)** — AgentOS live on Azure, demoed end-to-end by someone other than the
  author: login → ticket → streaming Quality run → real PR → cost + trace in the observability
  backend.
- **Mid term** — the enterprise checklist green (RBAC, audit export, DR drill, rotation); first
  external contributors (non-author issues + a merged non-author PR).
- **Long term** — pick the lane the data points to: a thriving open-core/community project (companies
  in production, sustainable contributors) **or** a commercial lane (enterprise seats + hosted
  AgentOS Cloud). Instrument adoption months in; let the numbers choose rather than pre-committing.
