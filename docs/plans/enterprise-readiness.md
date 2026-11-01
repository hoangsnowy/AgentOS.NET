# AgentOS.NET — Enterprise readiness review & plan (2026-10-17)

> Owner: *"Review the roadmap thoroughly — as it is, an enterprise can't use it."* This is that review: what an
> enterprise buyer checks, where AgentOS.NET stands **today (grounded in code, with evidence)**, and the
> re-baselined track that closes the gaps — **E0 Pilot-safe → E1 Enterprise pilot → E2 Enterprise GA**.
> It supersedes the old one-paragraph "Q3 — Enterprise-grade operations" in [ROADMAP.md](../../ROADMAP.md)
> and slots in next to the agentic-SDLC plan ([agentic-sdlc-v2.md](agentic-sdlc-v2.md)), which keeps the
> product phases (metrics, memory, ecosystem, executor).

## 1. Verdict

**Today AgentOS.NET is a credible single-team pilot on a trusted network — not something an enterprise security
review would pass.** The engine, the desktop, metrics and governance basics are real. What blocks an
enterprise is not one feature: it's the checklist a security architect, an IT/identity admin, a platform-ops
team and procurement each walk through. Each of these alone is a "no":

1. **Identity** — no corporate SSO (Entra ID / Okta / ADFS), no SCIM deprovisioning: leavers keep access.
2. **Authorization** — three roles (`member`, `admin`, `platform-admin`); no viewer/auditor/approver, no
   per-workspace permissions, no service accounts / API tokens for CI.
3. **Agent control** — agents can reach a PR with no human approval step, a Workflow *Human* node
   auto-approves when no operator is attached, the runner has no command allowlist (plan 0.7).
4. **Data** — isolation is application-level only (no Postgres RLS); no tenant export / purge / retention;
   the DataProtection key ring sits unwrapped in the same database it protects.
5. **Operations** — single replica (no SignalR backplane), geo-redundant backup off, no DR drill, no
   dashboards/alerts, no on-prem install path (Helm / compose), no air-gapped LLM.
6. **Customisation** — tenants can't give agents their engineering rules: only 4 of 7 agent prompts are
   editable (no versions), target-repo instruction files (`AGENTS.md`, `CLAUDE.md`,
   `.github/copilot-instructions.md`) are ignored, there are no skills, no custom agents, no knowledge base.
7. **Compliance paperwork** — no threat model, data-flow / sub-processor list, SBOM, signed images.

Fixed in the last 48 h (this review's first findings): cross-tenant `GET/POST /tenants` (#193), PR opened
when QA never passed or on offline canned output (#193), settings audit that SECURITY.md claimed but didn't
exist (#194), two-tenant isolation proven end-to-end on the full stack (#192), live service status (#191).

### Progress (2026-10-18)

**E0 Pilot-safe is complete**, and two E1 items have shipped. Each one was verified on the full stack or against
real services:

| Item | Shipped | Proof |
|---|---|---|
| E0.1 Postgres RLS (stage 1) | #205, [ADR-0012](../adr/0012-postgres-row-level-security.md) | Real-DB test: predicate-less query sees only its tenant, cross-tenant write → 42501; two-tenant UI test green with RLS on |
| E0.2 key-ring wrapping | #203 | Real certificate: key file holds `encryptedSecret`, round-trip OK; About shows protection |
| E0.3 evidence never dropped silently | #199 | Flood test: 10/10 persisted (6 inline), lost counter + metric |
| E0.4 Human node fails closed | #196 | Headless run stops at the checkpoint |
| E0.5 runner allowlist + approval + kill + token in header | #201 | Policy tests; real pairing over headers on the lab (#204) |
| E0.6 per-tenant run admission | #200 | Concurrency + burst tests; Settings → Platform → Run limits |
| E0.7 repo instruction files | #197 | Real GitHub: the sandbox's `AGENTS.md` read back on the lab |
| E0.8 OpenAI-compatible provider | #198, #202 | Wire test against a real OpenAI-protocol server |
| E1.4 tenant export + purge | #208 | Real-auth: export (no secrets) → UI purge → zero rows, users gone, audited |
| E1.6 workspace rules + versioned prompts | #206 | Agent prompt carries the rules; versions + restore |
| E1.10 dashboards, alerts, SLOs | #302, [OPERATIONS.md](../OPERATIONS.md) | Real code → OTLP → Prometheus 3.7: every dashboard series present, 3 Grafana dashboards render (light + dark), `AgentOsBudgetBlockingRuns` fired + `AgentOsBudgetOverCap` pending on an over-cap tenant; `promtool test rules` green. Lab over-cap run + Azure deploy still to do |
| (pre-E0) QA gate before PR, platform-only tenant endpoints, settings audit, isolation E2E, About | #191–#194 | Real-auth suite |

Live agent runs without an API key: `lab.ps1 pair` routes the lab to this machine's `claude -p` (#204). The full
5-agent pipeline completed live.

**Next:**
- E1.1 SSO brokering, E1.2 RBAC v2, E1.3 API tokens + `/v1`, E1.5 approval policies;
- E1.7 skills, E1.8 ADO boards + Jira, E1.9 Helm + compose;
- E1.10–E1.13 ops, supply chain, docs.

## 2. Scorecard

Status: ✅ shipped + verified · 🟡 partial · ❌ absent. "Where" cites the code that proves the status.

| # | Area | What an enterprise expects | Today | Where / evidence | Closes in |
|---|---|---|---|---|---|
| 1 | SSO | Entra ID / Okta / ADFS / Google via OIDC or SAML, MFA policy, JIT provisioning | ❌ Keycloak local users only | realm has no `identityProviders` | **E1.1** |
| 2 | Provisioning | SCIM 2.0 create/update/**deprovision** → sessions revoked | ❌ | — | **E2.1** |
| 3 | RBAC | viewer · developer · approver · admin · auditor; per-workspace scope; custom roles later | 🟡 3 roles | `IdentityModule.cs:23-25` | **E1.2** |
| 4 | Machine access | API tokens / service accounts, scoped + expiring, for CI and the CLI | ❌ JWT only | — | **E1.3** |
| 5 | API contract | versioned REST (`/v1`), published OpenAPI, deprecation policy | 🟡 unversioned | Scalar UI on Api | **E1.3** |
| 6 | Tenant isolation | DB-enforced, proven by test | 🟡 EF filters + E2E (#192), no RLS | plan 0.9 | **E0.1** |
| 7 | Key management | DataProtection ring wrapped by KMS (Key Vault / HSM / cert), rotation | 🟡 ring in Postgres, unwrapped | `DataProtectionExtensions.cs` | **E0.2** |
| 8 | Data lifecycle | retention per data class, tenant export, purge (GDPR), legal hold | ❌ | — | **E1.4** |
| 9 | Audit | who/what/when for config, members, sign-in, agent actions; export; tamper-evident; SIEM | 🟡 trail + UI + CSV (#194); evidence queue drops oldest past 4096; not hash-chained; no SIEM | `BufferedToolInvocationLog.cs:35` | **E0.3**, **E2.2** |
| 10 | Human approval | approve plan / diff / PR / merge per policy; fail closed when nobody can approve | 🟡 QA gate (#193); Human node auto-approves without an operator | `GraphExecutor.cs:721-733` | **E0.4**, **E1.5** |
| 11 | Repo safety | never push the default branch, honour branch protection + CODEOWNERS, draft PRs | 🟡 PR-only path | `GitHubPrService` | **E1.5** |
| 12 | Runner safety | command allowlist, per-session approval, kill/timeout, no token in URLs | 🟡 server timeout only | plan 0.7 | **E0.5** |
| 13 | Quotas | per-tenant rate limits on **both** hosts, budget caps, concurrency | 🟡 Api only; budget ✅ | `Api/Program.cs:51-75` | **E0.6** |
| 14 | Agent instructions | repo instruction files, tenant rules, all-agent prompts with versions + rollback | 🟡 4/7 prompts, no versions; repo files ignored | `PromptCatalog.cs`, `IPromptOverrideService.cs` | **E0.7**, **E1.6** |
| 15 | Skills / custom agents | SKILL.md packs, tenant-defined agents, per-agent tool allowlist | ❌ | plan 4.5 | **E1.7**, **E2.5** |
| 16 | Knowledge | per-tenant knowledge base / memory, citations | ❌ (ADR-0008 Proposed) | plan P3 | plan P3 |
| 17 | LLM choice | Claude, Azure OpenAI **and** an OpenAI-compatible endpoint (vLLM, Ollama, Foundry) for air-gapped; zero-retention + region options | 🟡 Claude/Azure/MAF/RemoteAgent | `LlmClientFactory` | **E0.8** |
| 18 | Trackers / SCM | GitHub ✅; Azure DevOps boards; GitLab; Jira; Teams/Slack notifications; webhooks | 🟡 GitHub + ADO repos | plan 0.10, 4.3, 7 | **E1.8**, **E2.4** |
| 19 | Deployment | Azure (azd) ✅; on-prem Helm chart + docker compose; air-gapped bundle; upgrade path | 🟡 azd/ACA only | `DEPLOYMENT.md` | **E1.9** |
| 20 | Availability | ≥2 replicas (backplane / sticky), HA Postgres, backups geo-redundant, executed DR drill | ❌ `minReplicas: 1`, geo backup off | `postgres.module.bicep:22-27` | **E2.3** |
| 21 | Observability | dashboards, alerts, SLOs, live status | ✅ Grafana + App Insights dashboards, 19 alerts with runbooks, SLOs (E1.10); runner-connected gauge open | `deploy/observability`, [OPERATIONS.md](../OPERATIONS.md) | **E1.10** ✅ |
| 22 | Supply chain | SBOM, signed images + provenance, pinned deps, SAST | 🟡 CodeQL 0, Dependabot, pinned actions | `.github/workflows` | **E1.11** |
| 23 | Compliance docs | threat model, data-flow + sub-processors (LLM vendors), DPA template, SOC 2 mapping, pen test | 🟡 SECURITY.md | — | **E1.12**, **E2.6** |
| 24 | Admin console | tenants list/suspend/delete, quotas, usage per tenant, seats | 🟡 Platform settings + About | `PlatformSettingsApp` | **E2.7** |
| 25 | Accessibility / i18n | WCAG 2.2 AA audit; localisable UI | 🟡 aria in 21 files; no i18n | — | **E2.8** |
| 26 | Release policy | semver, changelog, migration notes, supported versions / LTS | 🟡 pre-1.0, `main` only | `SECURITY.md` | **E1.13** |

## 3. The track

Same rules as every plan PR (agentic-sdlc-v2 §0): one PR per row, a visible desktop change where it touches
behaviour, unit + E2E, real integrations verified on the lab, docs updated in the same PR.

### E0 — Pilot-safe (≈2 weeks) — *nothing here waits for a decision*

| PR | Change | Acceptance |
|---|---|---|
| **E0.1** `feat(security)` | **Postgres RLS** on every tenant table (= plan 0.9): `ENABLE ROW LEVEL SECURITY` + `tenant_id = current_setting('app.tenant_id')` set per connection by a `DbConnectionInterceptor`; migrations run as a bypass role | Raw SQL with the wrong tenant returns 0 rows; the two-tenant E2E (#192) stays green |
| **E0.2** `feat(security)` | **Key wrapping**: `ProtectKeysWithAzureKeyVault` (cloud) / `ProtectKeysWithCertificate` (on-prem) behind a bootstrap setting; startup refuses an unwrapped ring outside Development; rotation runbook | Key ring rows are ciphertext; rotation drill documented |
| **E0.3** `fix(tools)` | **Evidence never silently drops**: bounded queue keeps back-pressure for governance events, a dropped-event counter metric + alert, the gap recorded in the trail | Flood test: zero lost refusals; counter visible on About |
| **E0.4** `fix(pipeline)` | **Human node fails closed**: no operator attached → the run pauses (or fails with "approval required"), never auto-approves; tenant setting for an explicit auto-approve, audited | Unit test; headless run with a Human node does not pass it |
| **E0.5** `fix(remote-agent)` | Runner command allowlist + per-session approval + kill/timeout + token out of the URL (= plan 0.7) | Unit tests; lab pairing |
| **E0.6** `feat(web)` | Per-tenant rate limit + concurrency on the **Web** host too (the desktop runs the engine in-process) | Load test hits 429 per tenant, other tenants unaffected |
| **E0.7** `feat(pipeline)` | **Repo instruction files**: read `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, `.cursor/rules` from the target repo (GitHub + ADO), fence them as repo guidance, show what was used on the run | A fixture repo's rule ("use xUnit") changes the generated tests; the run shows the files it read |
| **E0.8** `feat(llm)` | **OpenAI-compatible provider** (vLLM, Ollama, LM Studio, Azure AI Foundry, any `/v1/chat/completions`): endpoint + key + model in Settings, priced via the editable pricing table, counted in metrics | Lab run against a local Ollama model end to end |

### E1 — Enterprise pilot (≈6 weeks)

| PR | Change | Acceptance |
|---|---|---|
| **E1.1** `feat(identity)` | **Corporate SSO**: Keycloak identity brokering (OIDC + SAML) configured from Platform settings — Entra ID, Okta, ADFS, Google; claim → tenant + role mappers; JIT user creation; enforce MFA at the IdP | Sign in to the lab with an Entra ID test tenant; the user lands in the mapped tenant with the mapped role |
| **E1.2** `feat(identity)` | **RBAC v2**: roles viewer · developer · approver · admin · auditor; a permission matrix in code (one place), enforced on endpoints **and** desktop apps; per-workspace membership | Permission-matrix tests; E2E per role |
| **E1.3** `feat(api)` | **API tokens + service accounts** (hashed, scoped, expiring, audited) and **`/v1` versioned API** with published OpenAPI + deprecation headers; the CLI uses tokens | CI pipeline triggers a run with a service-account token |
| **E1.4** `feat(data)` | **Tenant export + purge + retention**: JSON/CSV export of every tenant table, purge job (zero rows left, verified by the catalog-driven check from #192), retention policies per data class (runs, evidence, audit) | Purge E2E leaves zero rows; export round-trips |
| **E1.5** `feat(governance)` | **Approval policies**: per tenant/workspace — require approval for plan, diff, PR, merge; approver role; draft PRs; never push the default branch; respect branch protection + CODEOWNERS | A policy "PR needs approval" holds the run until an approver clicks Approve; the audit shows who |
| **E1.6** `feat(pipeline)` | **Prompts v2**: every agent's prompt editable (incl. Orchestrator, IssueWork, Decomposer), version history, diff, rollback, per-tenant "engineering rules" block appended to every agent | Roll back a prompt; metrics split by prompt version |
| **E1.7** `feat(skills)` | **Agent Skills** (= plan 4.5) pulled forward: SKILL.md packs per tenant, name+description in prompt, body on demand, per-agent enablement, usage metrics | A tenant skill changes Coding output; usage counted |
| **E1.8** `feat(integration)` | **Azure DevOps boards** (= plan 0.10) + **Jira** (native or Atlassian MCP) + Teams/Slack notifications | Lab: ADO + Jira tickets run end to end |
| **E1.9** `feat(deploy)` | **On-prem**: Helm chart + docker compose (Postgres, Keycloak, Web, Api, optional Ollama), offline image bundle, upgrade guide | Cold install on a clean k8s (kind) from docs only |
| **E1.10** `feat(ops)` ✅ #302 | Dashboards (Grafana JSON + App Insights workbook), alert rules (budget over cap, runner disconnect, failed runs, evidence drops, probe Down), SLOs | Alert fires on a lab over-cap run |
| **E1.11** `chore(supply-chain)` | CycloneDX SBOM per release, cosign-signed images, SLSA provenance | `cosign verify` passes on a release image |
| **E1.12** `docs(security)` | Threat model (STRIDE per module), data-flow diagram, sub-processor list (LLM vendors + what leaves the box), DPA template, hardening guide | Reviewed; linked from SECURITY.md |
| **E1.13** `docs(release)` | Semver + CHANGELOG + migration notes + supported-versions policy | First tagged release follows it |

### E2 — Enterprise GA (≈6 more weeks)

| PR | Change |
|---|---|
| **E2.1** `feat(identity)` | SCIM 2.0 (users + groups → roles/tenants), deprovision revokes sessions + runner tokens |
| **E2.2** `feat(audit)` | Tamper-evident audit (hash chain + periodic anchor), SIEM export (OTLP logs / syslog / webhook), audit of agent actions (tool calls, PRs) in the same trail |
| **E2.3** `feat(ops)` | HA: ≥2 Web replicas (SignalR backplane or sticky + documented), HA Postgres, geo-redundant backups, an **executed** DR drill with timings |
| **E2.4** `feat(integration)` | GitLab (SCM + issues), Bitbucket, inbound/outbound webhooks (= plan Phase 7) |
| **E2.5** `feat(agents)` | Tenant-defined custom agents (role, prompt, tools, model) usable in Workflow + Board; per-agent tool allowlists |
| **E2.6** `docs(compliance)` | SOC 2 / ISO 27001 control mapping, external pen test, remediation |
| **E2.7** `feat(admin)` | Platform admin console: tenants (list / suspend / delete / quotas), usage + cost per tenant, seats; metering export for billing |
| **E2.8** `feat(web)` | WCAG 2.2 AA audit + fixes; localisation infrastructure (`IStringLocalizer`), Vietnamese + English |

### Sequencing

```
E0 (all parallelisable, ≈2 wk) ──► E1.1 SSO ─┬─► E1.2 RBAC ──► E1.5 approvals ──► E1.3 tokens/API
                                             └─► E1.4 data lifecycle
E1.6 prompts · E1.7 skills · E1.8 trackers · E1.9 on-prem · E1.10–E1.13 ops/docs run alongside
E1 exit ──► E2
```

The agentic-SDLC phases (metrics P1, tokens P2, executor P5, memory P3) keep running; E0 is interleaved first
because each item is small and each one is a security-review blocker.

## 4. Exit criteria

- **E0 (pilot-safe):** a security reviewer can't find a fail-open path — RLS on, keys wrapped, nothing
  auto-approves, nothing drops evidence silently; a pilot tenant runs on its own model endpoint.
- **E1 (enterprise pilot):** a real customer tenant signs in with their Entra ID, roles come from their groups,
  a ticket from their tracker becomes an approved PR under their policy, their own rules and skills shape the
  output, and they can export or purge everything — on Azure **or** their own k8s.
- **E2 (GA):** SCIM, HA + DR drill, tamper-evident audit to their SIEM, admin console + metering, SOC 2 mapping.

## 5. Decisions needed (recommended defaults)

| # | Decision | Default until decided |
|---|---|---|
| ED-1 | SSO: Keycloak brokering vs direct Entra ID in the Web | **Keycloak brokering** — keeps one token shape, works on-prem, supports SAML |
| ED-2 | RBAC model | **Fixed roles + permission matrix in code** first; custom roles only on demand |
| ED-3 | Scale-out | **Sticky sessions + documented** until a 2-replica test proves a backplane is needed (ROADMAP D3) |
| ED-4 | Air-gapped LLM | **OpenAI-compatible provider** (covers vLLM, Ollama, Foundry) rather than one SDK per runtime |
| ED-5 | Edition boundary (open core) | SSO, SCIM, RBAC v2, audit export/SIEM, HA, admin console = enterprise edition candidates (ROADMAP §6) |
