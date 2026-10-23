# AgentOS.NET — Configuration Reference

AgentOS.NET splits configuration in two (ADR-0011 — UI-first configuration):

1. **Set at deploy** — the few keys a host needs *before anyone can sign in*: where the database is,
   how sign-in works, logging, secrets, and operator locks. These live in `appsettings*.json`,
   environment variables (`Section__Key`), user-secrets (Development) or Aspire/azd injection.
2. **Set in the desktop** — every product setting (LLM providers, agent profiles, pipeline, safety
   switches, build sandbox). Each one is a `SettingDescriptor` declared by its module, has its default
   **in code**, and is changed in **Settings** without a restart.

`appsettings.json` contains bootstrap keys only — a unit test (`BootstrapConfigTests`) fails the build if
a product section creeps back in. There is no shared seed file: code is the single source of defaults,
so the Web and Api hosts cannot drift apart.

## How a setting value is resolved

For a key read through the settings registry, first match wins:

| # | Tier | Where it comes from | Who sets it |
|---|---|---|---|
| 1 | **Lock** | `Settings:Locks:<key>` in deployment config | Operator — the UI shows it read-only with the reason |
| 2 | **Tenant** | Settings UI, stored encrypted in `config.app_config` | Tenant admin (tenant-scope keys only) |
| 3 | **Platform** | Settings UI (`@platform/` rows) | Platform admin (`platform-admin` realm role) |
| 4 | **Seed** | Any configuration source at the same key (env var, user-secrets, appsettings) | Operator — optional; e.g. `Agents__Coding__Model` |
| 5 | **Default** | The descriptor's default, in code | — |

Every write is validated by the descriptor (kind, range, choices, custom validator) and audited
(who, when, scope, before/after; secrets masked). Readers resolve per call, so a saved change applies
to the next request.

## Set at deploy (bootstrap)

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | — | Postgres. Absent → modules fall back to no-op repositories (stateless boot) unless `RequireDatabase` is true. Aspire injects it on the full stack. |
| `Persistence:RequireDatabase` | `true` (code); Web base `false` for the standalone run, `true` in Web Production | Fail-loud guard: refuse to boot stateless when a DB is expected. |
| `Persistence:RowLevelSecurity` | `true` | Postgres RLS on the tenant tables ([ADR-0012](adr/0012-postgres-row-level-security.md)); the DB user needs `CREATEROLE`. `false` = escape hatch (application filters only). |
| `Auth:Keycloak:Authority` | empty | **Required outside Development.** Dev fallback `http://localhost:8080/realms/agentic` applies in Development only. |
| `Auth:Keycloak:Audience` | `agentic-api` | JWT audience (Api). |
| `Auth:Keycloak:ClientId` / `ClientSecret` | `agentic-web` / empty | Web OIDC client. Secret required outside Development; the dev default secret is rejected by `DevSecretGuard`. |
| `Auth:Keycloak:RequireHttpsMetadata` | `true` | Unset: true, except an `http://localhost` authority in Development. |
| `Auth:Keycloak:Admin:BaseUrl` | empty | Keycloak admin API for member lifecycle. Empty → no-op client. |
| `Auth:Keycloak:Admin:{Realm,Username,Password,ClientId}` | `agentic`/`admin`/`admin`/`admin-cli` | Use a service account + secrets in production. |
| `Auth:DevAutoLogin` | `true` in Development only | Fixed-user auth for the standalone Web. Hard-throws outside Development; AppHost forces it off. |
| `DataProtection:KeyVaultKeyId` | empty | Wrap the DataProtection key ring (which encrypts every tenant secret) with an Azure Key Vault key — `https://<vault>.vault.azure.net/keys/<key>`, via managed identity. |
| `DataProtection:CertificatePath` / `CertificatePassword` / `CertificateThumbprint` | empty | Wrap it with a certificate instead (on-prem / k8s secret mount, or the Windows cert store). |
| `DataProtection:RequireKeyWrapping` | `false` | Refuse to start with an unwrapped ring outside Development. System → About shows the key ring's protection either way. |
| `Api:BaseUrl` | empty | Empty = Web runs the engine in-process. Set = Web is a thin client of the Api host. System → About probes the Api at Aspire's `services:api:*` address first (the AppHost references the Api from the Web for that card only), then this. |
| `Settings:Locks:<key>` | Production pins 4 keys (below) | Operator locks. Change with an env var, e.g. `Settings__Locks__RemoteAgent__AutoApprove=true` — a deployment decision, not a click. |
| `Logging:*`, `AllowedHosts` | ASP.NET defaults | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `APPLICATIONINSIGHTS_CONNECTION_STRING` | — | Enable OTLP / App Insights export. |
| `Email:*` | `SmtpHost` empty → logs instead of sending | `SmtpHost`, `SmtpPort` (1025), `From`, `FromName`, `User`, `Password`, `UseStartTls`. Validated at startup. Aspire injects MailHog in dev. |
| `Mcp:Servers[]`, `Mcp:CallTimeoutSeconds` | empty / 60 | Upstream MCP servers. Validated at startup. Moves to a per-tenant registry in Phase 4. |
| `Plugins:Path` | `plugins` | Folder scanned for `IAgentOsPlugin` assemblies. |

**Secrets** (`Llm:Claude:ApiKey`, `Llm:AzureOpenAi:ApiKey`, …) can be seeded at deploy
(`azd env set Llm__Claude__ApiKey …`, user-secrets) *or* entered in Settings → LLM providers, where a
tenant key wins over the platform seed. Never commit them.

### Production locks (`appsettings.Production.json`, Api + Web)

| Locked key | Value | Why |
|---|---|---|
| `RemoteAgent:AutoApprove` | `false` | Remote execution on a paired machine always needs a human approval. |
| `Tools:EnforceByDefault` | `true` | Tool policy fails closed: a tool runs only if the tenant allowlists it. |
| `Llm:OfflineFallback` | `false` | A missing key fails loudly instead of serving canned output. |
| `Integration:BuildVerifier:Sandbox` | `Container` | Generated code never builds in the host process. |

## Set in the desktop (Settings)

Defaults below are the code defaults (tier 5). **Scope**: *Tenant* = each tenant admin sets their own;
*Platform* = one value for every tenant, platform admin only.

### Settings → LLM providers

| Key | Default | Scope | Notes |
|---|---|---|---|
| `Llm:Provider` | `Claude` | Tenant | Used by any agent whose profile names no provider. `Anthropic` is an alias of `Claude`. |
| `Llm:ForceProvider` | empty | Tenant | Route every agent to one provider. |
| `Llm:OfflineFallback` | **`true` in Development, else `false`** | Platform | Keyless provider → canned, schema-valid output, labelled Offline everywhere (runs, Workflow chip, PR refusal). Lets a fresh checkout run with no secrets. Locked off in Production. Never applies to Board agent runs (New idea, Coding, Reviewer, Testing, the Autopilot lead, Retrospective): those act on a real project, so they fail with the real reason (e.g. a plan limit) instead of getting canned text. |
| `Llm:Claude:ApiKey` / `ApiKeys` | empty | Tenant | Secret, write-only in the UI. Pool = round-robin with 429 failover. |
| `Llm:AzureOpenAi:Endpoint` / `ApiKey` / `ApiKeys` | empty | Tenant | Endpoint must be absolute `https://`. |
| `Llm:AzureOpenAi:Model` | `gpt-4.1` | Tenant | Default deployment. |
| `Llm:AzureOpenAi:PricingModel` | empty | Tenant | Canonical price-table id for an aliased deployment (`gpt41-prod` → `gpt-4.1`). |
| `Llm:OpenAi:ApiKey` / `Llm:OpenAi:Model` | empty / `gpt-5.4` | Tenant | Bring-your-own-key **OpenAI** (`https://api.openai.com/v1`, fixed). Provider name `OpenAI` — **no longer an alias of `AzureOpenAI`** (use `Azure`/`AzureOpenAI` for Azure). |
| `Llm:Gemini:ApiKey` / `Llm:Gemini:Model` | empty / `gemini-3.8-flash` | Tenant | **Google Gemini** via its OpenAI-compatible endpoint (fixed). Provider `Gemini` (alias `Google`). |
| `Llm:Xai:ApiKey` / `Llm:Xai:Model` | empty / `grok-4.7` | Tenant | **xAI Grok** (`https://api.x.ai/v1`, fixed). Provider `xAI` (alias `Grok`). Grok bills the whole request at the long-context rate above 200k prompt tokens; the price table models it. |
| `Llm:OpenAiCompatible:Endpoint` | empty | Platform | Any `/v1/chat/completions` server — vLLM, Ollama (`http://host:11434/v1`), LM Studio, Azure AI Foundry, a gateway. Platform-only because the server calls it (no tenant-set SSRF). Provider name `OpenAICompatible` (aliases `Ollama`, `vLLM`, `Local`). |
| `Llm:OpenAiCompatible:ApiKey` | empty | Platform | Secret; leave empty for keyless servers. |
| `Llm:OpenAiCompatible:Model` | `llama3.1` | Platform | Served when an agent profile names a vendor model (Claude/GPT) the server doesn't host; any other model id in a profile is sent as-is. |
| `Llm:OpenAiCompatible:InputPricePerMTok` / `OutputPricePerMTok` | `0` / `0` | Platform | Your own $/1M rates — metrics and the budget use them (0 on your own hardware), never the unknown-model ceiling. |

Code-only (no UI yet; seed via env if you must): `Llm:Claude:Endpoint` (`https://api.anthropic.com`),
`Llm:Claude:ApiVersion` (`2023-06-01`), `Llm:*:TimeoutSeconds` (60), `Llm:Fallbacks`
(inter-provider failover map). Resilience today is key-pool cooldown + failover; `Llm:*:MaxRetries`
exists on the options type but is not honoured yet.

### Settings → Agent profiles

Per role — `Orchestrator`, `Requirement`, `Coding`, `Testing`, `Qa`, `IssueWork`, `Decomposer` — keys
`Agents:<Role>:Provider` / `Model` / `Temperature` / `MaxTokens`, tenant scope. The model field lists
priced models for the chosen provider (plus *Custom…* for deployment aliases); `Temperature` is hidden
for models that reject sampling params; `MaxTokens` ≤ 128 000.

| Role | Provider | Model | Temp | Max output |
|---|---|---|---|---|
| Orchestrator | Anthropic | `claude-haiku-4-5` | 0.3 | 2000 |
| Requirement | Anthropic | `claude-sonnet-4-6` | 0.1 | 2000 |
| Coding | AzureOpenAI | `gpt-4.1` | 0.2 | 4000 |
| Testing | AzureOpenAI | `gpt-4o-mini` | 0.2 | 3000 |
| Qa | Anthropic | `claude-haiku-4-5` | 0.1 | 1500 |
| IssueWork | Anthropic | `claude-sonnet-4-6` | 0.2 | 32768 |
| Decomposer | Anthropic | `claude-sonnet-4-6` | 0.2 | 4000 |

`Pipeline:MaxIterations` (QA-loop cap when a story sets no `NMax`) — default 3, range 1–10.
An unpriced model is charged at the fail-closed $15/$75 per 1M rate until added to `CostCalculator`.

### Settings → Platform (platform admins)

**Run limits (per tenant)** — `Pipeline:Limits:MaxConcurrentRuns` (default `4`) and `Pipeline:Limits:RunsPerMinute`
(default `30`): admission for every Pipeline, Workflow and Quick run, so one tenant's burst or parallel load can't
starve the others. Separate from the monthly budget cap, which still applies.


| Key | Default | Notes |
|---|---|---|
| `RemoteAgent:AutoApprove` | `false` (Production locked) | Unattended remote execution. |
| `Tools:EnforceByDefault` | `true` in Production, else `false` | Fail-closed tool policy. |
| `Workspaces:AllowedHosts` | empty | Self-hosted GitHub Enterprise / Azure DevOps Server hosts, comma-separated (atop `SsrfGuard`; public provider hosts always allowed). |
| `Integration:BuildVerifier:Enabled` | `false` in Production | Lets agents compile generated code. |
| `Integration:BuildVerifier:Sandbox` | `InProcess` (Dev) | `Container` = ephemeral no-network `docker run`. Locked to `Container` in Production. |
| `Integration:BuildVerifier:ContainerImage` | `mcr.microsoft.com/dotnet/sdk:10.0` | Allow-listed image reference — never a docker flag. |
| `Integration:BuildVerifier:CpuLimit` / `MemoryLimit` / `PidsLimit` | `2.0` / `1g` / `256` | Container quotas. |
| `Integration:BuildVerifier:TimeoutSeconds` | `90` | Process tree killed on overrun. |

### Other per-tenant settings

- **Tool allowlist** — Policy app (`tools/allowlist`).
- **GitHub** (`Github:Pat`, `RepoOwner`, `RepoName`, `BaseBranch`) — Settings, encrypted per tenant.
  PAT must start `ghp_`/`github_pat_`/`gho_`.
- **Per-viewer preferences** (theme, wallpaper, glass, clock, *Animations*) — System → Appearance,
  stored in the browser only.

## Legacy `/settings` endpoint

`POST /settings` (admin) still accepts exactly `Llm:ForceProvider`, `Llm:Claude:ApiKey`,
`Llm:AzureOpenAi:ApiKey`, `Llm:AzureOpenAi:Endpoint`, `Github:Pat`, `Github:RepoOwner`,
`Github:RepoName`, `Github:BaseBranch` (`SettingsKeyRegistry`); anything else → 400. It writes the same
tenant rows the registry reads. New settings are added as descriptors, not to this allowlist.

## Notes

- Environment detection for Development-only defaults reads `ASPNETCORE_ENVIRONMENT`, then
  `DOTNET_ENVIRONMENT`.
- **Removed (C.6):** the `Llm`, `Agents` and `Pipeline` sections of both hosts' `appsettings.json`
  (they duplicated the code defaults and disagreed on provider casing). An existing deployment that set
  them via env vars keeps working — those values are now the *seed* tier.
- **Removed (0.6.0):** `Auth:Bearer` and `Pipeline:EnableHumanInTheLoop` (never read).
