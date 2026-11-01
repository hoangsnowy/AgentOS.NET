# Security Policy

## Reporting a vulnerability

Please do **not** open a public issue, discussion or pull request for a security vulnerability. Report it
privately through **GitHub private vulnerability reporting**: the repository's **Security** tab → **Report a
vulnerability** ([direct link](https://github.com/hoangsnowy/AgentOS.NET/security/advisories/new)). If that is not
possible, email the maintainer. We aim to acknowledge reports within a few days and to agree a disclosure date with
you once a fix is available.

When reporting, include: affected component, reproduction steps, and impact.

## Security documentation

- [Threat model](docs/security/threat-model.md) — STRIDE per trust boundary and module, mitigations with code
  references, and the prioritised list of open gaps.
- [Data flow](docs/security/data-flow.md) · [Sub-processors](docs/security/subprocessors.md) (what leaves the box) ·
  [Production hardening checklist](docs/security/hardening.md) · [DPA template](docs/security/dpa-template.md)
  (template, not legal advice).

## Scope notes

- **LLM keys & secrets** are never committed. Set them via `dotnet user-secrets`, environment
  variables, or the runtime Settings store (encrypted with ASP.NET Core Data Protection). See
  [docs/SETUP.md](docs/SETUP.md).
- **API auth** is Keycloak OIDC JWT bearer (RS256); tokens are signed by the Keycloak realm key
  and validated against its JWKS — rotate the signing key in Keycloak, not in AgentOS.NET.
- The **remote dev-agent runtime** executes work on a developer machine — treat the pairing
  credential as a secret. Remote execution is gated by the `RemoteAgent:AutoApprove` platform setting, which
  Production locks off (a gate, not yet a per-run human approval — see the
  [threat model](docs/security/threat-model.md#6-tb4--server--paired-runner)); server-driven shell commands outside
  the allowlist additionally need approval at the runner console.

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
- **Tenant isolation** — row-level EF query filters **and Postgres row-level security** on the tenant tables
  ([ADR-0012](docs/adr/0012-postgres-row-level-security.md)): every connection runs as a restricted role bound to
  the caller's tenant, so a forgotten filter returns only the caller's rows and a write for another tenant is
  refused. Per-tenant keys are never mixed with another tenant's or the platform's. Cross-tenant endpoints (list / provision tenants) require `platform-admin`,
  never the per-tenant `admin` role; a real-auth E2E proves a second tenant sees none of the first's data.
- **QA gate before every PR** — a pipeline result becomes a pull request only when QA passed on real model
  output (`PrGate`, enforced inside the PR service itself): failed runs, the iteration cap with QA still
  failing, and offline canned text are blocked with the reason on the session.
- **SSRF guard** — tenant-supplied hosts are validated and limited to an allowlist.
- **Paired runners** — a server-driven shell command runs on a developer's machine only if every segment of the
  chain starts with an allowlisted build/test/VCS program (`REMOTE_AGENT_ALLOW` to change); command
  substitution is refused; anything else needs approval at the runner console and is denied when nobody is
  there. Each command has a timeout that kills the whole process tree. The runner sends its pairing token in a
  header, never in the URL (the hub still accepts a legacy query-string form — tracked as a gap).
- **Supply chain / SAST** — Dependabot, dependency review, SHA-pinned actions, CodeQL with
  compiler-enforced twins for its query classes. Release images are cosign-signed (keyless), carry SLSA
  build provenance and a CycloneDX SBOM attestation, and every release attaches the SBOMs — verify an image
  before deploying it ([docs/RELEASE.md](docs/RELEASE.md#verify-an-image)).

## Supported versions

This project is pre-1.0. Security fixes land on `main` and ship as a patch release of the latest minor; the
previous minor gets security fixes for 90 days after the next one ships. The full policy (SemVer, prereleases,
image tags) is in [docs/RELEASE.md](docs/RELEASE.md#supported-versions).
