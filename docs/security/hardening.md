# AgentOS.NET — Production hardening checklist

> Verified against `main` @ `28b05ed`. Use with [DEPLOYMENT.md](../DEPLOYMENT.md),
> [keycloak-prod-runbook.md](../keycloak-prod-runbook.md), [dataprotection-dr-runbook.md](../dataprotection-dr-runbook.md)
> and [CONFIG.md](../CONFIG.md). Items marked **(manual)** are not enforced by code or the shipped `infra/` today —
> you must do them; the related gap in the [threat model](threat-model.md#15-top-gaps-prioritised) is named.

Tick every box before exposing a deployment to people outside the team that operates it.

## 1. Identity — Keycloak

- [ ] **Rotate or delete the seeded users.** The realm import ships `operator`/`operator` (admin + platform-admin)
      and `member`/`member` (`infra/keycloak/agentic-realm.json:147-171`). Follow the
      [Keycloak runbook](../keycloak-prod-runbook.md). **(manual — GAP-03)**
- [ ] **Replace the dev client secret.** Hosts refuse the committed `agentic-web-dev-secret` and admin password
      `admin` outside Development (`src/AgentOs.SharedKernel/Security/DevSecretGuard.cs`) — set real ones. The
      on-prem templates' `CHANGE_ME` placeholder is refused the same way.
- [ ] **Use a least-privilege service account for member admin.** Set `Auth:Keycloak:Admin:ClientSecret` so
      the hosts use `client_credentials` with a confidential client holding only the user-management roles
      instead of a master-realm admin password ([ONPREM.md](../ONPREM.md#keycloak-admin-access); the on-prem
      install creates `agentos-admin` for you).
- [ ] **Turn on brute-force detection, a password policy and OTP for admins** in the realm (Realm settings →
      Security defenses / Authentication). None is set in the realm file. **(manual — GAP-03)**
- [ ] **Decide on self-registration.** AgentOS.NET's open self-signup (`/signup`, `POST /tenants/register`) is the
      platform setting `Tenants:OpenSignup`, **off by default outside Development** (GAP-01): only invitations create
      accounts. If you turn it on, put those routes behind your edge (WAF / rate limit / CAPTCHA), because the caller
      becomes admin of the new tenant. Separately, the realm has `registrationAllowed: true`: disable it for a
      closed deployment. **(manual — GAP-03)**
- [ ] **Set a stable public hostname** (`KC_HOSTNAME`) and keep `Auth:Keycloak:RequireHttpsMetadata=true`
      (default; pinned in `src/AgentOs.Web/appsettings.Production.json:6-10`). The ACA template sets it `false` only
      for the internal back-channel metadata address.
- [ ] **Keycloak on Postgres** (cloud default) and real SMTP — [DEPLOYMENT.md § Production hardening](../DEPLOYMENT.md#production-hardening-kc-postgres--real-smtp).
- [ ] **Corporate SSO / MFA at the IdP** — Keycloak brokering is planned (E1.1); until then MFA is Keycloak OTP.

## 2. TLS and the edge

- [ ] **TLS everywhere.** Container Apps ingress ships `allowInsecure: false`
      (`infra/AgentOs.AppHost/infra/web.tmpl.yaml:15-19`). On-prem: terminate TLS at a reverse proxy; Kestrel does
      not redirect or send HSTS itself (no `UseHsts` / `UseHttpsRedirection` in either host). **Add HSTS, CSP
      (`frame-ancestors 'self'`), `X-Content-Type-Options: nosniff` and `Referrer-Policy` at the proxy.**
      **(manual — GAP-12)**
- [ ] **Restrict forwarded headers to your proxy.** The hosts trust `X-Forwarded-For/Proto` from any peer
      (`src/AgentOs.ServiceDefaults/Extensions.cs:37-42`). Make sure only the ingress can reach the containers
      (no public container port) and that the proxy **overwrites** — not appends — `X-Forwarded-*` and `Host`.
      Set `AllowedHosts` to your public hostnames (Api ships `"*"`). **(manual — GAP-05)**
- [ ] **Runners use `https://`.** Give members the `https://` Web URL; the runner accepts `http://` hub URLs
      (`src/AgentOs.RemoteAgent/RunnerCredentials.cs:64-81`).
- [ ] **Postgres TLS.** Add `SslMode=VerifyFull` (or at least `Require`) to `ConnectionStrings:DefaultConnection`;
      the shipped string has none (`infra/postgres/postgres.module.bicep:78`). Replace `AllowAllAzureIps`
      (`:44-51`) with a private endpoint / VNet integration. **(manual — GAP-15)**

## 3. DataProtection key ring

- [ ] **Wrap the ring** with Azure Key Vault (`DataProtection__KeyVaultKeyId`), a PFX
      (`DataProtection__CertificatePath` + `__CertificatePassword`) or a cert thumbprint
      (`DataProtection__CertificateThumbprint`) — `src/AgentOs.Modules.AppConfig/DataProtectionExtensions.cs:80-105`.
      The shipped `infra/` sets none. **(manual — GAP-03b)**
- [ ] **Set `DataProtection__RequireKeyWrapping=true`** so a host that lost its wrapping config refuses to start
      (default `false`, `DataProtectionExtensions.cs:67-74`).
- [ ] **Check System → About → Key ring** shows the protection you configured (not *Degraded*).
- [ ] **Back up the wrapping key separately** from the database and run the restore drill in
      [dataprotection-dr-runbook.md](../dataprotection-dr-runbook.md). Never truncate `config.data_protection_keys`.
- [ ] Keep the DataProtection application name `"AgentOS"` (changing it orphans every secret).

## 4. Database and row-level security

- [ ] **RLS is on.** Leave `Persistence:RowLevelSecurity=true` (default). The DB user needs `CREATEROLE`, or a DBA
      pre-creates `agentos_rls` ([ADR-0012](../adr/0012-postgres-row-level-security.md)). After first boot, confirm
      the log has **no** RLS bootstrap error — on failure the host keeps running with RLS off
      (`src/AgentOs.SharedKernel/Persistence/RowLevelSecurity.cs:132-136`). **(manual check — GAP-14)**
- [ ] Verify in SQL: `select relname, relrowsecurity, relforcerowsecurity from pg_class where relrowsecurity;`
      lists the 16 tables in `RowLevelSecurity.Tables` (`RowLevelSecurity.cs:39-47`).
- [ ] **Least-privilege app login.** The shipped deployment connects as the server administrator
      (`postgres.module.bicep:78`). Prefer a dedicated login that owns the schemas, has `CREATEROLE`, and is not a
      superuser. **(manual — GAP-15)**
- [ ] `Persistence:RequireDatabase=true` — default on the Api, pinned in `src/AgentOs.Web/appsettings.Production.json:3-5`.
- [ ] **Backups:** retention ≥ 7 days **and** geo-redundant backup (shipped `Disabled`, `postgres.module.bicep:22-25`);
      HA for production. Backups contain the key ring and every tenant's data — protect them like a vault.
      **(manual — GAP-16)**

## 5. Secrets handling

- [ ] No secret in `appsettings*.json` or the repo. Bootstrap secrets come from env / Key Vault / user-secrets
      ([SETUP.md](../SETUP.md)); product secrets (LLM keys, PATs) are set by tenant admins in **Settings** and stored
      encrypted.
- [ ] **Restrict who is a tenant admin.** A tenant admin can replace or remove the tenant's LLM keys and GitHub PAT.
      Reading them back is no longer possible: `GET /settings/{prefix}` masks every value whose key is not known to be
      non-secret (fail closed — GAP-08, fixed). The platform Azure OpenAI key is only ever sent to the platform's own
      endpoint: a tenant that sets its own endpoint must also set its own key (GAP-01, fixed — G-1 / L-3 in the
      [threat model](threat-model.md)).
- [ ] **Treat tenant exports as sensitive.** They hold the tenant's full business data. Secrets are excluded:
      `app_config` ciphertext and runner / API-token hashes are dropped, and the export is refused (HTTP 409, nothing
      written, not audited as an export) if a redacted column is renamed (GAP-08, fixed).
- [ ] PATs: GitHub classic `repo` (+ `project` for boards), or fine-grained scoped to the workspace repos; ADO
      Code R/W, Work Items R/W, Project & Team R ([LAB.md](../LAB.md)). Prefer a bot account per tenant.
- [ ] Rotate keys by setting a new value in Settings; every set / clear is audited (value never recorded).

## 6. Paired runners

- [ ] **Prompt mode only — never skip permissions.** Do not set `REMOTE_AGENT_YOLO` or `REMOTE_AGENT_ARGS` and do
      not enable the VS Code extension's `agentos.runOnMyMachine` on machines that matter
      (`src/AgentOs.RemoteAgent/Program.cs:237-246`; `tools/agentos-vscode/src/extension.ts:120`). Scoped autonomy
      (`claude -p --permission-mode acceptEdits` + allowlists, no `WebFetch`/`WebSearch`) is the default for Board runs
      (`src/AgentOs.RemoteAgent/ScopedAutonomy.cs`). `REMOTE_AGENT_AUTONOMY=off` refuses scoped runs entirely.
- [ ] **Leave `RemoteAgent:AutoApprove` locked `false`** (`appsettings.Production.json` → `Settings:Locks`). Note it
      is a boolean gate, not a per-run human approval. **(GAP-20)**
- [ ] Keep `REMOTE_AGENT_ALLOW` at its default or narrower; the default allows interpreters (`node`, `python`,
      `pip`) without approval (`src/AgentOs.RemoteAgent/RunnerCommandPolicy.cs:20-26`). Never set `*`.
- [ ] Run the runner as the member's normal (non-admin) account; keep the console attached so server-driven
      commands outside the allowlist can be approved (denied when nobody is there).
- [ ] **Revocation:** when someone leaves, revoke their runners in Board → Runners **and** restart the Web / Api
      revision — revoke does not drop a live connection and member removal does not revoke runners
      (`src/AgentOs.Modules.RemoteAgent/RemoteAgentHub.cs:67-74`). **(manual — GAP-04)**
- [ ] Protect `runner.json` (`%APPDATA%\AgentOS.NET\runner.json` on Windows has no extra ACL).

## 7. Tool policy, build sandbox, MCP, plugins

- [ ] **Tool allowlist per tenant.** `Tools:EnforceByDefault` is locked `true` in Production: a tool runs only if
      the tenant's `tools/allowlist` lists it (`src/AgentOs.Modules.Tools/Policy/AppConfigToolPolicy.cs:58-83`).
      Allow only what each tenant needs (`build_verifier`, `runner_shell`, specific `{server}.{tool}` MCP tools).
- [ ] **Build sandbox = Container** (locked). Give the host a Docker / Podman engine; keep the default image
      pinned or mirror it internally. Enable the verifier only where needed (off by default in Production).
- [ ] **MCP servers** are operator config (`Mcp:Servers`); `stdio` servers run a process on the host — vet each.
- [ ] **Plugins** load with full trust and no signature check (`src/AgentOs.SharedKernel/Plugins/PluginLoader.cs`);
      keep `Plugins:Path` read-only and install only plugins you built or reviewed. **(GAP-19)**

## 8. Rate limits, run limits and budget caps

- [ ] **Api** has a per-tenant token bucket of 120 requests/min (`src/AgentOs.Api/Program.cs:51-76`) — not
      configurable; partition falls back to client IP for anonymous calls.
- [ ] **Web** has no global limiter (only pairing exchange, 10/min/IP — `src/AgentOs.Web/Program.cs:294-306`):
      rate-limit at the proxy / WAF, especially `/signup`. **(manual — GAP-02)**
- [ ] **Run limits** (Settings → Platform → Run limits): `Pipeline:Limits:MaxConcurrentRuns` (default 4) and
      `Pipeline:Limits:RunsPerMinute` (default 30) per tenant (`src/AgentOs.Modules.Pipeline/Limits/RunAdmission.cs`).
- [ ] **Budget caps:** each tenant admin sets a monthly cap **and switches enforcement on** — without
      `budget/enforce=true` the cap only warns (`src/AgentOs.Modules.Pipeline/Cost/BudgetGuard.cs:38-63`). The gate
      fails open on database errors and does not apply to RemoteAgent (member's own subscription). **(GAP-09)**
- [ ] Set vendor-side spend limits on every API key as the hard stop.

## 9. Logging, telemetry and secret masking

- [ ] Prefer the **OTLP** path (`OTEL_EXPORTER_OTLP_ENDPOINT`): log records pass `SecretMaskingLogProcessor`
      (Anthropic / OpenAI `sk-` keys, GitHub tokens, `Bearer`, `Password=`) — `src/AgentOs.ServiceDefaults/SecretMaskingLogProcessor.cs`.
      Console JSON logs (shipped to Log Analytics by Container Apps) and Application Insights are **not** masked.
      **(GAP-15)**
- [ ] Set a retention on the Log Analytics workspace (none set in `infra/resources.bicep:37-46`).
- [ ] Configure real SMTP — the null sender (no `Email:SmtpHost`) logs invitation links including the token
      (`src/AgentOs.Modules.Tenants/Email/NullEmailSender.cs:21-26`). Use STARTTLS (`Email__UseStartTls=true`) or 465.
- [ ] Tool evidence stores full tool input / output (`tools.tool_invocations`); restrict database read access and
      watch `agentos.evidence.lost` / `agentos.evidence.inline_writes` (meter `AgentOs.Tools`).

## 10. Availability, backups and recovery

- [ ] Geo-redundant Postgres backups + HA; a scheduled restore drill (key ring decrypts a saved key).
- [ ] Single replica today: the runner hub broker is in-process (no backplane) — plan for downtime on revision
      swaps until E2.3.
- [ ] No automatic retention for runs / evidence / audit exists; size storage accordingly and use tenant purge
      (Settings → Data, platform admin) for off-boarding. **(GAP-16)**
- [ ] Container Apps keeps revisions — know the rollback command ([DEPLOYMENT.md § Operating](../DEPLOYMENT.md#operating-the-deployment)).

## 11. Production locks — verify they are in force

`appsettings.Production.json` (Api + Web) pins, under `Settings:Locks` ([CONFIG.md](../CONFIG.md#production-locks-appsettingsproductionjson-api--web)):

| Key | Locked value |
|---|---|
| `RemoteAgent:AutoApprove` | `false` |
| `Tools:EnforceByDefault` | `true` |
| `Llm:OfflineFallback` | `false` |
| `Integration:BuildVerifier:Sandbox` | `Container` |

- [ ] `ASPNETCORE_ENVIRONMENT=Production` on both hosts (Aspire `launchProfileName: null` drops it — set it
      explicitly; see CLAUDE.md *Lessons learned*).
- [ ] No `Settings__Locks__*` env override unless it is a recorded decision.
- [ ] Scalar / OpenAPI is not exposed (mapped only outside Production, `src/AgentOs.Api/Program.cs:174-183`) — also
      keep Staging environments off the internet.
