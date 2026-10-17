# AgentOS — Configuration Reference

Every key an operator can set, where it's read, its default, and whether it can change at runtime.
Sources merge in the usual ASP.NET Core order: `appsettings.json` → `appsettings.{Environment}.json`
→ user-secrets (Development) → environment variables (`Section__Key`) → Aspire AppHost injection.

**Validated at startup** = the host refuses to boot on an invalid value (`ValidateOnStart`).
**Runtime (Settings UI)** = admins change it per-tenant in the desktop Settings app; stored encrypted
in `config.app_config`; the allowlist lives in `SettingsKeyRegistry`.

## Connection / persistence

| Key | Default | Read by | Notes |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | — | every module | Postgres. Absent → modules fall back to no-op repositories (stateless boot) unless `RequireDatabase` is true. Aspire injects it on the full stack. |
| `Persistence:RequireDatabase` | Api `true`, Web `false` (`true` in Web Production) | module loaders | Fail-loud guard: refuse to boot stateless when a DB is expected. |

## LLM gateway (`Llm:*`)

The LLM section is **not startup-validated**: `LlmModule` binds `LlmOptions` with `.Bind(…).ValidateOnStart()`
but registers no `.Validate(…)` rule and `LlmOptions` carries no DataAnnotations, so `ValidateOnStart`
is a no-op. The provider-name + `https://` shape checks run only at **runtime** when a value goes through
the Settings UI (`POST /settings` → `SettingsKeyRegistry`); a bad value in `appsettings.json` is not rejected at boot.

| Key | Default | Runtime (Settings UI) | Notes |
|---|---|---|---|
| `Llm:Provider` | `Claude` | — | Default provider when an agent names none. |
| `Llm:ForceProvider` | empty | ✔ `Llm:ForceProvider` | Forces every agent to one provider: `Claude` (alias `Anthropic`) / `AzureOpenAI` / `MAF` / `RemoteAgent`. |
| `Llm:Claude:ApiKey` / `ApiKeys` | empty | ✔ `Llm:Claude:ApiKey` | Platform fallback key(s). Per-tenant keys via Settings UI win. Set via user-secrets/env — never commit. |
| `Llm:Claude:Endpoint` | `https://api.anthropic.com` | — | |
| `Llm:Claude:ApiVersion` | `2023-06-01` | — | Anthropic API version header. |
| `Llm:AzureOpenAi:ApiKey` / `ApiKeys` / `Endpoint` | empty | ✔ same keys | Endpoint must be absolute `https://` (enforced only on the Settings-UI path, not at startup). |
| `Llm:*:TimeoutSeconds` | 60 | — | |
| `Llm:OfflineFallback` | Web `true`, Production/Api `false` | — | When true, appends a keyless deterministic `Offline` provider to the END of every provider's failover chain (LlmClientFactory + OfflineLlmClient): a keyless run falls through to canned, schema-valid output so the pipeline + Workflow studio run on a fresh checkout with no secrets. Off in Production so a missing cloud key fails loudly. |
| `Llm:Fallbacks` | — | — | Ordered fallback provider list (FailoverLlmClient). |

## Agents (`Agents:*`) — validated at startup

Per agent (`Orchestrator`, `Requirement`, `Coding`, `Testing`, `Qa`, `IssueWork`, `Decomposer`):
`Provider` (non-empty), `Model` (non-empty), `Temperature` (0–2), `MaxTokens` (>0),
`IssueWork.MaxParallelRepos` (fan-out bound, clamped ≥1 at use).
Keep model ids in sync with `CostCalculator`'s price table: an unpriced model is charged at the table's ceiling rate (fail-closed) and logged as an estimate until it is added.

## Pipeline (`Pipeline:*`) — validated at startup

| Key | Default | Notes |
|---|---|---|
| `Pipeline:MaxIterations` | 3 | QA-loop cap when the story doesn't set `NMax`. Must be ≥ 1. |
| `Pipeline:Engine` | `Classic` | `Classic` (in-process loop) or `Workflow` (MAF graph). |

## Auth

| Key | Default | Notes |
|---|---|---|
| `Auth:Keycloak:Authority` | empty | **Required outside Development.** Dev fallback `http://localhost:8080/realms/agentic` applies in Development only; production boots refuse a missing value. |
| `Auth:Keycloak:Audience` | `agentic-api` | JWT audience (Api). |
| `Auth:Keycloak:ClientId` / `ClientSecret` | `agentic-web` / empty | Web OIDC client. Secret required outside Development; the dev default secret is rejected by `DevSecretGuard`. |
| `Auth:Keycloak:RequireHttpsMetadata` | `true` | Explicit value always wins. Unset: defaults true, except an `http://localhost` authority in Development (otherwise every request, including probes, 500s on a standalone run). |
| `Auth:Keycloak:Admin:BaseUrl` | empty | Keycloak admin API for member lifecycle. Empty → no-op client. Validated absolute URL at startup. |
| `Auth:Keycloak:Admin:{Realm,Username,Password,ClientId}` | `agentic`/`admin`/`admin`/`admin-cli` | Use a service account + secrets in production. |
| `Auth:DevAutoLogin` | `true` in Development only | Fixed-user auth for the standalone Web. Hard-throws outside Development; AppHost forces it off. |

## Web split mode (`Api:*`)

| Key | Default | Notes |
|---|---|---|
| `Api:BaseUrl` | empty | Empty = Web runs the engine in-process. Set = Web becomes a thin client of the Api host (settings, pipeline, health probes go over HTTP). Deployment-time only — not in the Settings UI. The split-mode client (`HttpPipelineClient`) forwards the user's token from `IAuthTokenProvider`; there is no static-bearer key. |

## Tools & integration

| Key | Default | Notes |
|---|---|---|
| `Tools:EnforceByDefault` | `true` in Production, else `false` | Fail-closed tool policy: deny unless on the tenant allowlist (`tools/allowlist` per-tenant config, Policy app). |
| `Integration:BuildVerifier:Enabled` | `false` in Production | Master gate. Keep off in Production unless `Sandbox=Container` (ADR-0005, audit #10). |
| `Integration:BuildVerifier:Sandbox` | `InProcess` | `InProcess` (host child process, Layer-1 hardening only — Dev) or `Container` (ephemeral no-egress `docker run` / ACA Job — Production-safe). |
| `Integration:BuildVerifier:ContainerImage` | `mcr.microsoft.com/dotnet/sdk:10.0` | Image for the container runner + ACA Job. A dotnet SDK image. |
| `Integration:BuildVerifier:CpuLimit` / `MemoryLimit` / `PidsLimit` | `2.0` / `1g` / `256` | Container resource quotas (`--cpus` / `--memory` / `--pids-limit`). |
| `Integration:BuildVerifier:TimeoutSeconds` | `90` | Hard cap on build duration; the process tree is killed on overrun. |
| `Github:*` (Pat/RepoOwner/RepoName/BaseBranch) | — | Runtime Settings UI only (per-tenant, encrypted). PAT must start `ghp_`/`github_pat_`/`gho_`. |

## Workspaces (`Workspaces:*`)

| Key | Default | Notes |
|---|---|---|
| `Workspaces:AllowedHosts[]` | empty | Defense-in-depth host allowlist for tenant-supplied workspace hosts (atop the connect-time `SsrfGuard`). The provider public hosts (`github.com`, `dev.azure.com`, `*.visualstudio.com`) are always allowed; add GitHub Enterprise Server / Azure DevOps Server hosts here to connect a self-hosted board. Bare host or URL; subdomains of an entry are allowed. A blank board host = the provider's public default. |

## MCP (`Mcp:*`) — validated at startup

| Key | Default | Notes |
|---|---|---|
| `Mcp:CallTimeoutSeconds` | 60 | Per `tools/call` cap. Must be > 0. |
| `Mcp:Servers[]` | empty | Each enabled server needs `Name`; `Transport` `stdio` needs `Command` (+`Args`/`Env`), `http` needs absolute `Url`. `Enabled:false` keeps the entry without connecting. |

## Email (`Email:*`) — validated at startup

| Key | Default | Notes |
|---|---|---|
| `Email:SmtpHost` | empty | Empty → NullEmailSender (logs instead of sending). Aspire injects MailHog in dev. |
| `Email:SmtpPort` | 1025 | 1–65535. 587 + `UseStartTls:true` for STARTTLS providers; 465 = implicit TLS. |
| `Email:From` / `FromName` | `noreply@agentic.local` / `AgentOS` | `From` required when `SmtpHost` is set. |
| `Email:User` / `Password` | empty | Secrets in production. |

## Misc

| Key | Default | Notes |
|---|---|---|
| `Plugins:Path` | `plugins` | Folder scanned for `IAgentOsPlugin` assemblies. |
| `Metrics:CsvPath` | — | Optional CSV export of per-run metrics. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Enables OTLP trace/metric export when set. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | — | Enables App Insights (Api + Web). |

## Runtime-settable keys (the `/settings` allowlist)

`POST /settings` (admin-only) accepts exactly: `Llm:ForceProvider`, `Llm:Claude:ApiKey`,
`Llm:AzureOpenAi:ApiKey`, `Llm:AzureOpenAi:Endpoint`, `Github:Pat`, `Github:RepoOwner`,
`Github:RepoName`, `Github:BaseBranch`. Anything else → 400. Values are validated (provider names,
https URL shape, PAT prefix). An empty value clears the override back to the platform fallback.
`GET /settings/{prefix}` reads the `Llm` and `Github` prefixes only.

## Notes

- Environment detection for the dev-only fallbacks reads `ASPNETCORE_ENVIRONMENT` first, then
  `DOTNET_ENVIRONMENT` (standard ASP.NET Core precedence).
- **Removed (0.6.0):** the dead `Auth:Bearer` section (`Issuer`/`Audience`/`Secret`/`OperatorPassword`)
  and `Pipeline:EnableHumanInTheLoop` — neither was read by any code path. Keycloak OIDC is the only
  shipped auth mode; the human-in-the-loop gate is roadmap work and will return as a real feature.
