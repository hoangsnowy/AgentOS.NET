# Competitive parity & positioning (2026-10-18)

Sources: a GitHub-API scan of every "AgentOS / Agent OS / AIOS" project, the vendor docs of the main AI coding
agents, Gartner's first Magic Quadrant for Enterprise AI Coding Agents (2026-05-20) and the OWASP Top 10 for
Agentic Applications 2026. Star counts were pulled 2026-10-17/18. Cells marked ❓ were not found in the sources
reviewed and are not guessed.

## 1 — The name collision

| Project | ★ | What it is | Overlap |
|---|---|---|---|
| Agno AgentOS (agno-agi/agno) | 42.8k | Python agent runtime + control plane: RBAC, service accounts, tracing, approvals, self-host; launched 2025-09 | **High**: same category |
| buildermethods/agent-os | 5.5k | Spec / standards injection for Claude Code + Cursor | Medium: same audience |
| rivet-dev/agentOS | 4.8k | WASM / V8 sandboxes for agents | Low; a possible sandbox backend |
| AgentVerse-OS | 1.0k | Browser windowed desktop running Claude Code / Codex | Medium: same desktop metaphor |
| SapienXai/AgentOS (agentos.sapienx.app) | 121 | Control plane for OpenClaw | Medium; feature list drove §3 |
| **hoangsnowy/AgentOs** | 3 | This repo, created 2026-05-17 | — |

Agno has used the name for the same category since 2025-09, eight months before this repo existed. A USPTO
"AGENTOS" mark could not be verified. **Owner decision needed before v1.0:** either qualify the name
everywhere (e.g. "AgentOS for .NET") or rename to a distinctive coined name. Renaming is cheapest while the
repo is small.

## 2 — Feature matrix vs. the AI coding agents

✅ yes · 🟡 partial · ❌ not offered · ❓ not found

| Capability | AgentOS | OpenHands | Devin | Copilot agent | Cursor agents | Claude Code Action | Codex | Agno |
|---|---|---|---|---|---|---|---|---|
| Self-host the full stack | ✅ | ✅ | 🟡 vendor-run VPC | ❌ | 🟡 workers only | 🟡 your runners | ❌ | ✅ |
| Air-gapped / local models | ✅ | 🟡 | ❌ | ❌ | ❌ | ❌ | ❓ | ❓ |
| Bring your own model / key | ✅ (Claude, Azure, OpenAI, Gemini, xAI, OpenAI-compatible) | ✅ | ❓ | 🟡 | 🟡 | 🟡 Claude only | ❓ | ❓ |
| Many tenants in one install (RLS, export/purge) | ✅ | 🟡 | ❌ | ❌ | ❌ | ❌ | ❌ | 🟡 |
| QA-gated multi-agent loop | ✅ | ❓ | ❓ | ❓ | ❓ | 🟡 | ❓ | ❌ |
| Ticket → PR | ✅ GitHub, ADO | ✅ GitHub/GitLab/Jira/Linear | ✅ Jira/Linear | ✅ Issues/Jira | ✅ GitHub/Linear/Slack | ✅ | ✅ Linear | ❌ |
| Azure DevOps Boards + Repos | ✅ | ❓ | 🟡 | ✅ | 🟡 | ❓ | ❓ | ❌ |
| Visual workflow editor | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 🟡 |
| Budgets + cache-token metrics | ✅ | ✅ | 🟡 | 🟡 | ✅ | ✅ | 🟡 | 🟡 |
| Audit log | ✅ | 🟡 | ✅ | ✅ | ❓ | ✅ | ✅ | 🟡 |
| SSO / SCIM / RBAC | 🟡 OIDC, 2 roles | ✅ | ✅ | ✅ | ✅ | ❓ | ✅ | ✅ |
| Container sandbox for code | 🟡 build verifier only | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |

**Differentiated:** self-hosted multi-tenant (RLS + export/purge) — no competitor found offers this
combination · an explicit Requirement→Code→Test→QA convergence gate (others stop at "open a PR for review") ·
.NET-native + Azure DevOps Boards/Repos · a visual workflow editor · a paired dev machine on a subscription
(zero server API tokens).

**Behind:** traction · no container/VM sandbox for agent code (OWASP code-execution risk) · 2 roles, no
SCIM · no published benchmark · no Jira / Linear / GitLab / Slack · no compliance attestations.

**What enterprises now require** (Gartner MQ criteria + vendor table stakes): iterative verification, human
oversight, traceability, native MCP; SAML SSO + SCIM, RBAC, audit API/streaming, no training on customer code,
spend limits, VPC/on-prem, admin-managed model + MCP allow-lists.

## 3 — Parity track (agentos.sapienx.app feature list + the matrix above)

Every item must fit the simplified information architecture (see the UX review) — **no new top-level app
unless the IA plan says so.** Items ship one PR each, verified in the running app.

| # | Item | Status |
|---|---|---|
| 1 | ~~Installable PWA~~ | Dropped by the owner (2026-10-18) |
| 2 | BYOK OpenAI · Gemini · xAI | ✅ [#214](https://github.com/hoangsnowy/AgentOs/pull/214) |
| 3 | Scheduled runs (cron / interval / one-off) | Next |
| 4 | Mission Control — one live screen of every running agent / run | Planned |
| 5 | Notifications to Slack / Discord / Telegram / Teams, then start runs from chat | Planned |
| 6 | Chat with an agent (ask / steer a run) | Planned |
| 7 | Custom agents + SDLC templates | Planned (E2 "tenant-defined custom agents") |
| 8 | One-click deploy (docker compose; Railway / Render templates) | Planned (with E1.9 Helm + compose) |
| 9 | Plans / quotas / history retention | Planned (with E1 retention + E2 metering) |

## 4 — Highest-leverage moves beyond parity

1. **Govern the coders enterprises already bought.** Wrap Claude Code, Codex and OpenHands as pluggable
   *Coding agent* providers behind `IToolGateway`, so budgets, the QA gate, audit and RLS apply to them —
   AgentOS becomes the governance layer instead of a Cursor competitor.
2. **Prove the QA loop with numbers.** A SWE-bench-Verified-style run plus a .NET task set, QA gate off vs.
   on: resolve rate, cost per resolved ticket, iterations, cache-token split.
3. **Close the sandbox gap.** Coding/Testing tools in ephemeral containers (Docker, ACA dynamic sessions)
   behind `IToolGateway`; a threat model mapped to OWASP ASI01–10.
4. **Enterprise pack.** SCIM + custom roles via Keycloak, audit streaming to a SIEM via OTel GenAI
   conventions, per-tenant model + MCP allow-lists, a SOC 2-style control map for self-hosters.
5. **Own the Microsoft shop.** One-command `azd up` / Helm, Azure Marketplace listing, Jira + GitLab next, ADO
   Boards → PR as the hero demo.

## Sources

Agno: github.com/agno-agi/agno, docs.agno.com/agent-os/introduction, agno.com/pricing,
agno.com/changelog/introducing-agentos · OpenHands: openhands.dev/enterprise, openhands.dev/pricing · Devin:
docs.devin.ai/enterprise/vpc/overview, docs.devin.ai/enterprise/integrations/azure-devops · GitHub Copilot:
docs.github.com/en/copilot/concepts/agents/coding-agent/about-coding-agent,
docs.github.com/en/copilot/concepts/models/bring-your-own-key · Cursor: cursor.com/enterprise,
cursor.com/docs/cloud-agent · Claude Code: code.claude.com/docs/en/github-actions,
code.claude.com/docs/en/monitoring-usage · Codex: developers.openai.com/codex/integrations/linear · Gartner MQ
coverage: virtualizationreview.com/articles/2026/06/05/… (secondary) · OWASP:
genai.owasp.org/resource/owasp-top-10-for-agentic-applications-for-2026 · Microsoft Agent Framework 1.0:
devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/
