# Changelog

All notable changes to AgentOS.NET are recorded here. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) — see [docs/RELEASE.md](docs/RELEASE.md) for the
version policy, how a release is cut, and how to verify the published images.

Sections map to Conventional Commit types: `feat` → **Added**, `perf` / `refactor` / behaviour changes →
**Changed**, `fix` → **Fixed**, `fix(security)` / `security` → **Security**; anything that needs an operator
action on upgrade also gets a **Migration notes** entry.

## [Unreleased]

Everything on `main` before the first tagged release. AgentOS.NET has not shipped a tagged release yet;
`<Version>` in `Directory.Build.props` is 0.6.0.

### Added

- **Board + Autopilot.** A Projects-style board that spans many repositories: tickets with dependencies,
  file attachments, live plain-language progress, Stop / Try again / Ask for changes / Fix the failing
  checks. Autopilot runs each ticket as a Microsoft Agent Framework workflow (drawn as a graph in the Workflow
  app), driven by an Orchestrator agent that decides while rules only guard; it resumes interrupted work
  after a restart, updates behind branches, sends conflicts back to the Coding agent and closes superseded
  pull requests and delivered epics.
- **More agents.** Reviewer (reviews every change before testing), Testing (accepts a change against its
  criteria; failures already on the base branch get their own ticket), Discovery (New idea) and
  Retrospective (turns lessons into project practices every agent follows), alongside the original
  Requirement → Coding → Testing → QA pipeline. The Coding agent writes and updates the PR description.
- **Design gate** ([ADR-0016](docs/adr/0016-design-gate.md)). An optional Autopilot step between Test and
  Merge for UI tickets (`area:ui` by default once on): the Testing agent takes screenshots of the changed
  screens at phone and desktop width, light and dark, and a Designer agent checks them before merge; requested
  changes go back as a fix round, and the owner can approve or ask for changes on the task's Design panel.
- **Run on your own computer.** Pair a developer machine with a one-time code or the VS Code extension; Board
  sessions run on the local Claude / Codex CLI with scoped autonomy (no API tokens on the server), keep the
  machine awake while working, can use local test data, and *Try the app* runs a project's latest version
  locally.
- **Knowledge graph** shared by the three phases of a ticket (slice 1).
- **Insights.** Tokens per agent split into fresh input · cache read · cache write · output, cost per class,
  delivery metrics (first-try rate, fix rounds, time to merge), real usage reported from members' computers.
- **LLM providers.** Claude, Azure OpenAI, Microsoft Agent Framework, bring-your-own-key OpenAI, Google
  Gemini and xAI Grok, plus an OpenAI-compatible provider for self-hosted / air-gapped models (vLLM, Ollama,
  LM Studio). Price table with exact-id lookup, prompt-cache rates and long-context tiers.
- **UI-first configuration** ([ADR-0011](docs/adr/0011-ui-first-configuration.md)): one flat Settings rail
  with search, Agent profiles (pick a model, the provider follows), Model keys per provider with Test,
  Platform settings for platform admins, Autopilot settings and agent run time limits.
- **Enterprise basics.** API tokens / service accounts, tenant export + purge (Settings → Data), audit log of
  every settings and config write, workspace rules + versioned agent prompts, per-tenant run admission
  (concurrency + burst), scheduled runs (once or cron).
- **Desktop.** GNOME / Adwaita desktop with Dash, Activities, virtual workspaces, Ctrl/⌘+K search,
  System → About with every service probed live, animated agent characters and living icons (with an
  opt-out).
- **Release pipeline**: signed Api / Web images on GHCR with CycloneDX SBOMs and SLSA provenance
  ([docs/RELEASE.md](docs/RELEASE.md)), and this changelog.
- **Operations**: Grafana + Azure Monitor dashboards, Prometheus alert rules with promtool tests and runbooks,
  SLOs, and run / budget / health metrics (`deploy/observability`,
  [docs/OPERATIONS.md](docs/OPERATIONS.md)).

### Changed

- The dock is the MVP: Home · Board · Insights · Settings; Terminal and Files are Board tabs, and one
  Activity log holds changes, sign-ins and tool calls.
- Agents follow the target repository's own instruction files (`AGENTS.md`, `CLAUDE.md`,
  `.github/copilot-instructions.md`, `.cursor/rules`).
- 60 fps desktop: no idle animation loops, no always-on backdrop filter, fingerprinted and cached static
  assets.

### Fixed

- Autopilot: a failed fix round keeps its pull request; a task out of fix rounds is blocked once; the retry
  budget survives restarts; a fresh attempt never reuses a branch an interrupted one left behind.
- Board agent runs never fall back to the offline demo in place of the real model; offline canned output is
  labelled and never shipped as a pull request.
- Paired runners: a run's whole process tree ends with it; answers never queue behind coding runs; UTF-8
  pipes to and from the CLI; re-connecting a computer replaces its old offline entries.
- Sign-in cookies are isolated per instance and a failed OIDC callback restarts sign-in instead of erroring.
- Code-scanning (CodeQL) alerts are cleared as they land, with compiler-enforced twins for their classes.

### Security

- Postgres row-level security on every tenant table ([ADR-0012](docs/adr/0012-postgres-row-level-security.md)).
- DataProtection key ring wrapped with Azure Key Vault or a certificate.
- Governance evidence is never dropped silently; a Human checkpoint with no operator fails closed.
- Paired runners: command allowlist, per-session approval, kill on timeout, pairing token in a header
  instead of the URL.
- Tenant-listing / provisioning endpoints are platform-admin only; a QA gate runs before every pull request;
  third-party ticket text is fenced as untrusted data; `Path.Combine` is banned at build time.
- VS Code runner extension: `@vscode/vsce` 4.1.0 brings `@azure/msal-node` to 7.1.2, past the advisory on
  authorization-code-flow parameter disclosure (< 7.1.0; dev-only dependency).

### Migration notes

- None — there is no earlier tagged release to upgrade from.

[Unreleased]: https://github.com/hoangsnowy/AgentOS.NET/commits/main
