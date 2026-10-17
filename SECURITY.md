# Security Policy

## Reporting a vulnerability

Please do **not** open a public issue for security vulnerabilities. Instead, report privately via
[GitHub Security Advisories](https://github.com/hoangsnowy/AgentOs/security/advisories/new)
or email the maintainer. We aim to acknowledge reports within a few days.

When reporting, include: affected component, reproduction steps, and impact.

## Scope notes

- **LLM keys & secrets** are never committed. Set them via `dotnet user-secrets`, environment
  variables, or the runtime Settings store (encrypted with ASP.NET Core Data Protection). See
  [docs/SETUP.md](docs/SETUP.md).
- **API auth** is Keycloak OIDC JWT bearer (RS256); tokens are signed by the Keycloak realm key
  and validated against its JWKS — rotate the signing key in Keycloak, not in AgentOS.
- The **remote dev-agent runtime** executes work on a developer machine — treat the pairing
  credential as a secret. Remote execution needs a human approval; unattended auto-approve is a
  platform setting that Production locks off.

## Built-in safeguards

- **Tool governance** — every tool call goes through `IToolGateway` → per-tenant policy → evidence log
  (refusals and errors included). Production enforces a fail-closed allowlist.
- **Build sandbox** — generated code is compiled only in an ephemeral, no-network, read-only, non-root
  container with CPU/memory/PID quotas ([ADR-0005](docs/adr/0005-build-verifier-sandbox.md));
  Production locks the sandbox to `Container`.
- **Prompt-injection fencing** — third-party text (tickets, issues, repo content) is wrapped as
  untrusted data before it reaches a prompt.
- **Operator locks** — Production pins the riskiest settings so the Settings UI cannot change them
  ([CONFIG.md](docs/CONFIG.md#production-locks-appsettingsproductionjson-api--web)); platform-wide
  settings require the `platform-admin` role.
- **Audit trail** — every settings change (old → new, secrets masked), every direct config write (LLM keys,
  tokens, prompts, tool policy, budget — recorded as *set / cleared*, never the value), member lifecycle,
  invitations and sign-in land in an append-only audit log, viewable in **Settings → Audit log** and
  exportable as formula-safe CSV. Platform-scope changes are visible to platform admins only.
- **Tenant isolation** — row-level EF query filters; per-tenant keys are never mixed with another
  tenant's or the platform's.
- **SSRF guard** — tenant-supplied hosts are validated and limited to an allowlist.
- **Supply chain / SAST** — Dependabot, dependency review, SHA-pinned actions, CodeQL with
  compiler-enforced twins for its query classes.

## Supported versions

This project is pre-1.0; security fixes land on `main`.
