# Contributing to AgentOS.NET

Thanks for your interest! AgentOS.NET is a .NET-native multi-agent platform for the software development
lifecycle. Issues and pull requests are welcome.

## Getting started

```bash
git clone https://github.com/hoangsnowy/AgentOS.NET.git
cd AgentOs
dotnet build AgentOs.slnx -c Release
dotnet test  --solution AgentOs.slnx -c Release     # xUnit v3 on Microsoft.Testing.Platform
dotnet run   --project src/AgentOs.Web              # the desktop at http://localhost:5180, no keys needed
```

You need the **.NET 10 SDK** (pinned in `global.json`); tests need no API keys or services. See
[docs/SETUP.md](docs/SETUP.md) for the full stack, E2E tests and troubleshooting.

## Workflow

1. Branch from `main` (`feat/…`, `fix/…`, `refactor/…`, `docs/…`, `chore/…`).
2. Make the change **with tests**. `dotnet build -c Release` must stay warning-free
   (`TreatWarningsAsErrors`, `Nullable`, latest analyzers, CodeQL-parity IDE rules).
3. `dotnet test --solution AgentOs.slnx -c Release` must pass.
4. **Verify in the running app** when the change is user-facing (see below).
5. Open a PR and fill in [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md)
   (summary + test plan). CI (PRs to `main`) runs build → tests → coverage floor → dev-auth UI E2E;
   CodeQL and dependency review run alongside.

## Definition of done

- **Unit-green is necessary, not sufficient.** A user-facing change is wired into the desktop
  (an app in `AppCatalog` or a screen inside one) and exercised in the running Web.
  - Pure UI / layout → the standalone Web (`dotnet run --project src/AgentOs.Web`) is enough.
  - Persistence, sign-in or multi-tenant behaviour → verify on the full Aspire stack; standalone
    silently no-ops those paths.
- **Measurable.** Agent-facing features record what they cost and how well they did (tokens, cost,
  latency, outcome) — if it can't be measured, it isn't done.
- **Configurable in the UI.** A new setting is a `SettingDescriptor` in the module's
  `ISettingsContributor`, read through `ISettings` — not an `appsettings.json` key
  ([ADR-0011](docs/adr/0011-ui-first-configuration.md), [CONFIG.md](docs/CONFIG.md)).
- **Documented** where behaviour or configuration changed (README, CONFIG.md, an ADR for decisions).
- **In the changelog.** A user- or operator-visible change adds a line under `## [Unreleased]` in
  [CHANGELOG.md](CHANGELOG.md), plus a **Migration notes** bullet when an upgrade needs an action
  ([docs/RELEASE.md](docs/RELEASE.md#migration-notes-convention)).

## Conventions

- **Commits**: [Conventional Commits](https://www.conventionalcommits.org/) in English
  (`feat(llm): …`, `fix(web): …`, `docs: …`, `chore(deps): …`).
- **Tests**: xUnit v3 + Shouldly + NSubstitute (not FluentAssertions). Name
  `MethodName_StateUnderTest_ExpectedBehavior`. UI flows: Playwright in `tests/AgentOs.E2E.Tests`.
- **Architecture**: modular monolith. Each feature is an `AgentOs.Modules.*` library referencing
  `AgentOs.Domain` + `AgentOs.SharedKernel`; cross-module references are explicit and minimal. The
  hosts (`Api`, `Web`) reference every module. EF migrations are per module:
  ```bash
  dotnet ef migrations add <Name> --project src/AgentOs.Modules.X --startup-project src/AgentOs.Modules.X \
    --output-dir Persistence/Migrations --context XDbContext
  ```
- **LLM access**: agents depend on `ILlmClient` via `ILlmClientFactory`, never a vendor SDK. New
  models need a price in `CostCalculator` (unpriced models are charged at a fail-closed ceiling).
- **Performance**: hot paths use the right data structure (e.g. `FrozenDictionary` lookups) and
  avoid per-call allocations; keep it measurable.
- **UI**: read [docs/design/design-system.md](docs/design/design-system.md) first (or run
  `/design-review`). Reuse `Btn`/`Field`/`Panel`/`Icon`, `var(--…)` tokens and defined classes only —
  never a CSS class that isn't defined. Support light + dark and `prefers-reduced-motion`. UI changes
  are mirrored in the Claude Design project.
- **Security**: third-party text that reaches a prompt is wrapped with `UntrustedText`; never log
  secrets; when a code-scanning alert lands, fix it and add its compiler-side twin rule if one exists.
- **Language**: code, comments, docs and prompts are English. Branding is **AgentOS.NET**.

## Reporting bugs / requesting features

Use the issue templates under [`.github/ISSUE_TEMPLATE`](.github/ISSUE_TEMPLATE). For anything
security-related, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
