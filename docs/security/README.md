# AgentOS.NET security documentation

| Document | What it answers |
|---|---|
| [threat-model.md](threat-model.md) | STRIDE per trust boundary and per module: threats, the existing mitigation (with code references), residual risk, and the prioritised list of open gaps |
| [data-flow.md](data-flow.md) | Mermaid data-flow diagrams and a table of what data crosses each boundary |
| [subprocessors.md](subprocessors.md) | Every external service the code can send data to (LLM providers, GitHub, Azure DevOps, Azure, e-mail, telemetry) — what leaves the box and when |
| [hardening.md](hardening.md) | Production checklist: Keycloak, TLS, DataProtection key wrapping + DR, RLS role, secrets, runners, tool policy, rate limits, budgets, backups, logging |
| [dpa-template.md](dpa-template.md) | A generic Data Processing Agreement **template** (not legal advice) for operators |

Report vulnerabilities privately — see [SECURITY.md](../../SECURITY.md).
