# Setup & First Run

Everything you need to build, run and test AgentOS.NET on your machine. For *what* to configure (deploy
time vs the desktop) see [CONFIG.md](CONFIG.md); for Azure see [DEPLOYMENT.md](DEPLOYMENT.md).

## 1. Prerequisites

- **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0>. `global.json` pins the
  version; check with `dotnet --list-sdks`.
- **Docker** (Docker Desktop / Podman) — only for the full stack (§4).
- Optional: an Anthropic and/or Azure OpenAI API key. Not needed to try AgentOS.NET — see §3.

## 2. Build and test

From the repo root:

```bash
dotnet restore AgentOs.slnx
dotnet build   AgentOs.slnx -c Release          # warnings are errors (TreatWarningsAsErrors)
dotnet test    --solution AgentOs.slnx -c Release
```

Tests run on xUnit v3 + Microsoft.Testing.Platform, so filters use xUnit's flags:

```bash
dotnet test --project tests/AgentOs.Tests --filter-class "*ToolGatewayTests"
dotnet test --project tests/AgentOs.Tests --filter-method "*ToolInvocationTests.Request_Validate_AllFieldsValid_DoesNotThrow"
```

No test calls a real model; no secrets are needed.

## 3. Run the standalone desktop (no Docker, no keys)

```bash
dotnet run --project src/AgentOs.Web
```

Open <http://localhost:5180>. In Development the Web:

- signs you in automatically (`Auth:DevAutoLogin`) as a user with `admin`, `member` and
  `platform-admin` roles,
- runs without a database (settings and runs are in memory and reset on restart),
- answers model calls with canned **Offline** output when no key is configured
  (`Llm:OfflineFallback` defaults on in Development), clearly labelled in the UI.

This is the right surface for UI work. It **cannot** prove persistence, real sign-in or multi-tenant
behaviour — those silently no-op here; verify them on the full stack (§4).

## 4. Run the full stack (Aspire)

```bash
dotnet run --project infra/AgentOs.AppHost
```

The AppHost starts Postgres, Keycloak and MailHog in containers and runs the Api and Web against
them, wiring connection strings and the OIDC authority automatically.

| What | Where |
|---|---|
| Aspire dashboard (logs, traces, resources) | <https://localhost:17050> |
| AgentOS.NET desktop | <https://localhost:5180> |
| Api — Scalar UI / OpenAPI | <https://localhost:5080/scalar/v1> · `/openapi/v1.json` |
| Keycloak admin console | URL shown for the `keycloak` resource in the dashboard (`admin` / `admin`) |
| MailHog (invitation emails) | URL shown for the `mailhog` resource |

Keycloak imports the `agentic` realm from `infra/keycloak/agentic-realm.json` on first start:

| User | Password | Roles |
|---|---|---|
| `operator` | `operator` | `admin`, `platform-admin` (tenant `default`) |
| `member` | `member` | `member` |

Data volumes persist across restarts.

### Troubleshooting the full stack

| Symptom | Cause | Fix |
|---|---|---|
| Postgres "Running (Unhealthy)", Web "Finished", `password authentication failed` | Stale `postgres-data` volume from an earlier run (the superuser password is baked in at first init) | `docker volume rm agentos.apphost-*-postgres-data` |
| Login page shows an old realm name/theme, or a new role (e.g. `platform-admin`) is missing | Stale `keycloak-data` volume — an existing realm is never re-imported | `docker volume rm agentos.apphost-*-keycloak-data` (or add the role via the Keycloak admin API; keep descriptions ASCII) |
| HTTP 431 Request Header Fields Too Large | Every dev app on `localhost` shares one cookie jar | Use an incognito window, or clear `localhost` site data |
| Browser refuses the dev certificate | Self-signed localhost cert | `dotnet dev-certs https --trust`; in Chrome you can enable `chrome://flags/#allow-insecure-localhost` |

Sanity check that sign-in is wired end to end:

```bash
curl -k -s -o /dev/null -w "%{http_code} %{redirect_url}\n" https://localhost:5180/account/login
# expect: 302 …/realms/agentic/protocol/openid-connect/auth?…request_uri=urn:ietf:…
```

## 5. Use a real model

The primary path is the desktop: **Settings → AI → Model keys** — one card per provider, Save then
**Test** (stored encrypted, per tenant) — then **Settings → Agent profiles** to pick each agent's model. Applies on the next call.

To seed a platform-wide fallback key for local runs instead, use user-secrets (the projects already
carry a `UserSecretsId`, so no `init` is needed):

```bash
dotnet user-secrets set "Llm:Claude:ApiKey"        "sk-ant-..."  --project src/AgentOs.Web
dotnet user-secrets set "Llm:AzureOpenAi:ApiKey"   "..."         --project src/AgentOs.Web
dotnet user-secrets set "Llm:AzureOpenAi:Endpoint" "https://<resource>.openai.azure.com" --project src/AgentOs.Web
```

Use `--project src/AgentOs.Api` for the Api host. Secrets live in your user profile, never in the repo.

## 6. Run the Api on its own

The Api requires a database by default (`Persistence:RequireDatabase=true`):

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Port=5432;Database=agentos;Username=postgres;Password=postgres" --project src/AgentOs.Api
dotnet run --project src/AgentOs.Api
```

Or boot it stateless with `Persistence__RequireDatabase=false`. Point `Auth:Keycloak:Authority` at a
reachable realm to call protected endpoints. Health: <https://localhost:5080/health>.

## 7. UI end-to-end tests (Playwright)

Against the standalone Web (what CI runs):

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AgentOs.Web --urls http://localhost:5199 --no-launch-profile
RUN_AGENTOS_E2E=true AGENTOS_URL=http://localhost:5199 dotnet test --project tests/AgentOs.E2E.Tests
```

Against the full stack (real Keycloak + Postgres; local/manual): start the AppHost, then run the suite
with `RUN_AGENTOS_E2E_REAL=true`. Playwright browsers install on first run.

## 8. GitHub Actions

- **CI** (`ci.yml`) — build, tests, coverage floor, dev-auth UI E2E. Needs **no secrets**.
- **CodeQL** and **dependency review** run on every PR.
- **Deploy** (`azd-deploy.yml`, manual `workflow_dispatch`) — needs Azure OIDC credentials plus the
  Keycloak secrets listed in [DEPLOYMENT.md](DEPLOYMENT.md). `LLM__CLAUDE__APIKEY` is optional (a
  platform fallback key for a demo deploy); real deployments use per-tenant keys set in the desktop.

Recommended branch protection for `main`: require a PR, require the `Build & Test` check, linear
history.
