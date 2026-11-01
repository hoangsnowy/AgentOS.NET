# Infra — Azure deploy via azd (Aspire)

Deployment is driven by **.NET Aspire + Azure Developer CLI (`azd`)**. The AppHost
(`infra/AgentOs.AppHost`) is the single source of truth for the topology; `azd` provisions from it:

| Resource | From the app model |
|---|---|
| Container Apps environment + Log Analytics | implicit (Aspire) |
| Azure Container Registry | implicit (image push) |
| Container App: `api` | `AddProject("api")` |
| Container App: `web` (AgentOS.NET desktop) | `AddProject("web")` |
| Container App: `keycloak` (custom image + `agentic` realm import) | `AddKeycloak(...)` |
| Azure Database for PostgreSQL flexible server (password auth) | `AddAzurePostgresFlexibleServer("postgres")` |
| Key Vault for the Postgres secrets | `infra/postgres-kv*` |
| Managed identity + role assignments | implicit (Aspire) |
| Application Insights + operations workbook + alert rules (opt-in, `enableObservability`) | `infra/observability/` (hand-written, E1.10 — see [OPERATIONS.md](../docs/OPERATIONS.md#azure-azd)) |

`infra/main.bicep`, `resources.bicep` and the `postgres*` modules are the azd-generated infrastructure
(`azd infra synth`), committed so the provisioned shape is reviewable — regenerate them rather than
hand-editing when the app model changes. Exception: `infra/observability/` is hand-written, and its hook in
`main.bicep` (the three observability parameters, the `observability` module, the
`APPLICATIONINSIGHTS_CONNECTION_STRING` output) plus the matching env var in `AgentOs.AppHost/infra/{api,web}.tmpl.yaml`
must be re-applied after an `azd infra synth`. `azd` builds images with the .NET SDK (no Dockerfile needed).

## First deploy (local)

```bash
azd auth login
azd up      # asks for env name, subscription, region and the parameters below, then provisions + deploys
```

| Parameter (`azd env set …`) | Secret | Purpose |
|---|---|---|
| `KeycloakAdminUsername` / `KeycloakAdminPassword` | password | Keycloak bootstrap admin |
| `KeycloakWebClientSecret` | yes | OIDC client secret for `agentic-web` (rotated into the realm by the hook) |
| `PostgresUsername` / `PostgresPassword` | password | App + Keycloak database |
| `SmtpHost` / `SmtpPort` | — | Invitation email (leave empty → emails are logged, not sent) |
| `OPERATORPASSWORD` / `MEMBERPASSWORD` | optional | Rotate the seeded realm users |

`azd up` prints the Api + Web URLs. Re-deploy after code changes with `azd deploy`.

### Post-deploy hook

`azure.yaml` runs `infra/hooks/postdeploy.sh` (`.ps1` on Windows) after every deploy. Once Keycloak is
up it patches the `agentic-web` redirect URIs/web origins to the real Web URL, rotates the client
secret, disables email verification when no SMTP is configured, rotates seed passwords, and ensures
the `platform-admin` role exists (an imported realm never gains new roles on its own). It is
idempotent; run it by hand with `bash infra/hooks/postdeploy.sh`.

## LLM keys

Real deployments use **per-tenant keys entered in the desktop** (Settings → LLM & providers). A
platform-wide fallback for a demo can be seeded from the environment:

```bash
azd env set Llm__Claude__ApiKey "sk-ant-..."
azd deploy
```

Production runs with `ASPNETCORE_ENVIRONMENT=Production`: there is no auto-login and offline fallback
is locked off, so without any key a model call fails loudly instead of serving canned output.

## CI/CD (GitHub Actions)

[`.github/workflows/azd-deploy.yml`](../.github/workflows/azd-deploy.yml) is **manual**
(`workflow_dispatch`). Continuous delivery on `main` is intentionally off until the Azure AD app has a
federated credential for `refs/heads/main` (instructions in the workflow header). It needs:

- secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (OIDC app), plus
  `KEYCLOAKADMINPASSWORD`, `KEYCLOAKWEBCLIENTSECRET`; optional `LLM__CLAUDE__APIKEY`
- variables `AZURE_ENV_NAME`, `AZURE_LOCATION`

⚠️ The OIDC service principal needs **Owner** or **User Access Administrator** — `azd` creates role
assignments for the managed identity (ACR pull, Key Vault). Contributor alone fails.

`azd pipeline config` can wire OIDC and the secrets for you.

## Local dev

No Azure needed: `dotnet run --project infra/AgentOs.AppHost` runs Postgres, Keycloak and MailHog as
local containers plus the Api, Web and the Aspire dashboard (see [docs/SETUP.md](../docs/SETUP.md)).

## Cleanup

```bash
azd down --purge
```
