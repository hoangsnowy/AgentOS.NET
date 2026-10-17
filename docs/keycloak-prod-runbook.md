# Keycloak-on-Postgres production runbook (azd → Azure Container Apps)

How the deployed Keycloak works, and how to operate + verify it on Azure. The cloud topology is
**already wired in code** — a single `azd up` provisions a durable, Postgres-backed Keycloak with the
`agentic` realm + AgentOS.NET login theme baked into a custom image. This is **not** a paste-these-snippets
TODO; the infra ships in `main`. Your job on a real deploy is: set a few secrets, run `azd up`, set the
two public URLs, re-run the realm-patch hook, verify.

Everything below is cross-referenced to the source of truth. If a step here disagrees with the code,
the code wins — fix this doc.

| Concern | Where it lives |
|---|---|
| Provisioning + KC env wiring | `infra/AgentOs.AppHost/Program.cs` |
| Custom KC image (realm + theme baked) | `infra/keycloak/Dockerfile` |
| Realm definition (dev defaults) | `infra/keycloak/agentic-realm.json` |
| Post-deploy realm patch | `infra/hooks/postdeploy.sh` / `.ps1` (wired in `azure.yaml` → `hooks.postdeploy`) |
| azd parameters (dev defaults) | `infra/AgentOs.AppHost/appsettings.json` → `"Parameters"` |

Prereqs: `azd`, `az`, .NET 10 SDK, an Azure subscription, `AZURE_ENV_NAME` + `AZURE_LOCATION` chosen.

---

## What's already automated (do NOT do these by hand)

These were once manual steps; they are now code. Re-doing them by hand breaks the build/config.

- **Keycloak's own database.** The AppHost provisions a second database on the same Azure Postgres
  flexible server: `postgres.AddDatabase("keycloak-db", databaseName: "keycloak")`
  (`Program.cs:63`). There is **no** `az postgres flexible-server db create keycloak` step.
- **The JDBC URL.** Composed at deploy time from the server's bicep `hostName` output by
  `KeycloakJdbcUrl(postgres)` → `jdbc:postgresql://<host>:5432/keycloak?sslmode=require`
  (`Program.cs:199-206`), wired as `KC_DB_URL` with `KC_DB=postgres` and `KC_DB_USERNAME`/`KC_DB_PASSWORD`
  reusing the Postgres `PostgresUsername`/`PostgresPassword` params (`Program.cs:113-116`). Do **not**
  hand-build a JDBC URL or set a `KeycloakDbUrl`/`KeycloakDbUsername`/`KeycloakDbPassword` parameter —
  none exist.
- **The realm + AgentOS.NET theme.** Baked into a custom image by `infra/keycloak/Dockerfile`
  (`quay.io/keycloak/keycloak:26.6`, `kc.sh build --db=postgres`, realm JSON copied to
  `/opt/keycloak/data/import/`, theme to `/opt/keycloak/themes/agentos`, `CMD ["start", "--import-realm",
  "--optimized"]`). The publish branch points the resource at it via
  `.WithDockerfile("../../infra/keycloak")` (`Program.cs:104`). No ACR pre-build, no volume mount, no
  bind-mount in cloud.
- **The reverse-proxy env.** ACA terminates TLS and forwards plain HTTP with `X-Forwarded-*`. KC is
  configured for that with `KC_HTTP_ENABLED=true` + `KC_PROXY_HEADERS=xforwarded` +
  `KC_HOSTNAME_STRICT=false` (`Program.cs:105-107`). **There is no `KC_PROXY=edge`** — that option was
  removed in Keycloak 24 and the image is 26.6. Don't reintroduce it.
- **External ingress + plain-HTTP listen.** KC's `http` endpoint is marked external (`Program.cs:110`);
  the Web listens on plain HTTP `targetPort: 8080` behind the ACA ingress (`Program.cs:160-161`).

Run mode (`dotnet run --project infra/AgentOs.AppHost`) is unchanged — it uses the local H2 volume +
bind-mount theme + MailHog branch (`Program.cs:79-94`) and is gated off the publish branch.

---

## Step 1 — set secrets BEFORE the first `azd up`

The dev defaults in `appsettings.json` (`KeycloakAdminPassword=admin`,
`KeycloakWebClientSecret=agentic-web-dev-secret`, `PostgresPassword=postgres`) are public placeholders.
Override them as azd parameters (stored in Key Vault, injected as Aspire parameters) **before** the first
provision:

```bash
azd auth login
azd env set KeycloakAdminPassword    "$(openssl rand -base64 24)"
azd env set KeycloakWebClientSecret  "$(openssl rand -base64 32)"
azd env set PostgresPassword         "$(openssl rand -base64 24)"
# Optional overrides — defaults are fine to start:
# azd env set KeycloakAdminUsername "agentos-admin"   # default: admin
# azd env set PostgresUsername      "agentos"         # default: postgres
```

> The complete parameter set is exactly: `KeycloakAdminUsername`, `KeycloakAdminPassword`,
> `KeycloakWebClientSecret`, `PostgresUsername`, `PostgresPassword`, `SmtpHost`, `SmtpPort`
> (`appsettings.json:10-16`, `Program.cs:43-56`). Parameters like `KeycloakDbUrl`, `WebPublicOrigin`,
> or `KeycloakHostname` **do not exist** — KC derives its hostname from the forwarded headers and its
> DB URL from the provisioned server.

The same `KeycloakWebClientSecret` value is also what the Web app receives as
`Auth__Keycloak__ClientSecret` (`Program.cs:171`); the realm import bakes the *dev* secret, so the
post-provision hook (Step 3) rotates the realm's `agentic-web` client secret to this value — otherwise
every login fails on a secret mismatch.

---

## Step 2 — first provision (gets you the public FQDNs)

```bash
azd up        # provisions RG, ACR, ACA env, Postgres (app + keycloak DBs), KC, API, Web
```

The **post-deploy** hook runs automatically after the deploy (`azure.yaml` → `hooks.postdeploy`), once
the Keycloak container app is up. It derives the public Keycloak and Web URLs from the Container Apps
environment's default domain (`AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN`), so there is no
manual URL step. To see the FQDNs:

```bash
azd env get-values            # look for the web + keycloak container-app URLs
RG=$(azd env get-values | sed -n 's/^AZURE_RESOURCE_GROUP=//p' | tr -d '"')
az containerapp show -n keycloak -g "$RG" --query properties.configuration.ingress.fqdn -o tsv
az containerapp show -n web      -g "$RG" --query properties.configuration.ingress.fqdn -o tsv
```

---

## Step 3 — what the post-deploy realm patch does

The realm JSON ships **dev** values that only the hook can fix in cloud: the `agentic-web` client's
`redirectUris`/`webOrigins` point at `https://localhost:5180`, its `secret` is the public
`agentic-web-dev-secret`, and `verifyEmail` is `true`. The hook (`infra/hooks/postdeploy.sh` / `.ps1`)
fixes them through the Keycloak admin API with an `admin-cli` token:

1. **`agentic-web` client** → `redirectUris=["<web>/*"]`, `webOrigins=["<web>"]`, `secret` rotated to
   `KeycloakWebClientSecret`.
2. **Realm** → `verifyEmail=false` (no app SMTP wired by default — see Step 4).
3. **Realm role `platform-admin`** → created if missing (an imported realm never gains new roles on
   later deploys). Assign it to whoever administers platform-wide settings.
4. **Seed users** `operator` / `member` → passwords reset to `OPERATORPASSWORD` / `MEMBERPASSWORD`
   when set; otherwise left on the public imported password, with a warning.

It is idempotent and retries while Keycloak warms up. Inputs (azd env / environment):

| Variable | Required | Purpose |
|---|---|---|
| `AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN` | set by azd | Derives `https://keycloak.<domain>` and `https://web.<domain>` |
| `KEYCLOAK_BASE_URL` / `WEB_BASE_URL` | no | Override the derived URLs (custom domains) |
| `KeycloakAdminPassword` | yes | Keycloak admin password |
| `KeycloakAdminUsername` | no (default `admin`) | Keycloak admin username |
| `KeycloakWebClientSecret` | for secret rotation | New `agentic-web` client secret |
| `OPERATORPASSWORD` / `MEMBERPASSWORD` | no | New seed-user passwords |

Run it by hand any time:

```bash
bash infra/hooks/postdeploy.sh      # POSIX
pwsh infra/hooks/postdeploy.ps1     # Windows
```

> **Strip or rotate the seed users before a public env.** Either set `OPERATORPASSWORD`/`MEMBERPASSWORD`
> so the hook rotates them, or delete the `operator`/`member` entries from the realm JSON's `users` array
> and rebuild the image. Leaving the imported placeholder passwords on a public deployment is a hole.

---

## Step 4 — (optional) wire real SMTP

Email is **off by default in cloud**: the hook sets realm `verifyEmail=false`, and with no SMTP host the
Tenants module registers `NullEmailSender` (logs only) so the app boots. Wire a real provider only when
you need signup verification + invites to actually send.

Two independent SMTP surfaces:

**(a) App-sent mail** (Tenants invitations/notifications) — bound to `EmailOptions`
(`src/AgentOs.Modules.Tenants/Email/EmailOptions.cs`). The AppHost already injects
`Email__SmtpHost` / `Email__SmtpPort` from the `SmtpHost` / `SmtpPort` azd parameters
(`Program.cs:132-133, 179-180`). For an authenticated provider, also set the auth keys directly on the
container (env-var form of `Email:User` / `Email:Password` / `Email:UseStartTls`):

```bash
azd env set SmtpHost "smtp.sendgrid.net"
azd env set SmtpPort "587"
# auth (note the real key names — NOT Email__SmtpUser / Email__SmtpPass):
#   Email__User        <user-or-apikey-id>
#   Email__Password    <password-or-apikey>
#   Email__UseStartTls true        # STARTTLS on 587; port 465 implies implicit TLS regardless
```

> The option names are `SmtpHost`, `SmtpPort`, `From`, `FromName`, `User`, `Password`, `UseStartTls`
> (`EmailOptions.cs:15-33`). Auth is `Email:User` / `Email:Password` (env `Email__User` /
> `Email__Password`), **not** `Email__SmtpUser` / `Email__SmtpPass`. When `SmtpHost` is empty the module
> falls back to `NullEmailSender`.

**(b) Keycloak-sent mail** (its own verify/reset emails) — the realm's `smtpServer` block reads
`${KC_SMTP_*:default}` env vars (`agentic-realm.json:15-25`: `KC_SMTP_HOST`, `KC_SMTP_PORT`,
`KC_SMTP_FROM`, `KC_SMTP_FROM_NAME`, `KC_SMTP_SSL`, `KC_SMTP_STARTTLS`, `KC_SMTP_AUTH`, `KC_SMTP_USER`,
`KC_SMTP_PASSWORD`). To turn KC email on, add those as KC container env in the AppHost publish branch and
flip `verifyEmail` back on (drop the hook's `verifyEmail=false`, or re-enable via the admin API).

---

## Step 5 — verify

Confirm the things only a real deploy proves:

1. **Login** — open `https://<WEB_FQDN>`, sign in (`operator` + your `OPERATORPASSWORD`). No
   `Invalid redirect_uri` (proves the hook patched the client) and no `Correlation failed`
   (ForwardedHeaders + durable DataProtection from the Batch-3 code).
2. **Secret match** — login succeeding at all proves the realm's `agentic-web` secret was rotated to
   `KeycloakWebClientSecret` (the value the Web app holds).
3. **Persistence across restart** — `az containerapp revision restart -n keycloak -g "$RG"`; log in
   again → realm + users survive (proves the Postgres `keycloak` DB backing, not ephemeral H2).
4. **Saved app secrets survive** — in AgentOS.NET Settings save an LLM key, restart the `web` app, confirm
   it still decrypts (proves the durable DataProtection key ring).
5. **Bearer / issuer** — an authenticated API call (`Auth__Keycloak__Authority` =
   `<KC_URL>/realms/agentic`, `Program.cs:123,168,189-195`) returns 200, proving issuer/hostname
   alignment through the proxy.

First place to look on failure:

```bash
az containerapp logs show -n keycloak -g "$RG" --tail 200
```

---

## Notes / scope

- **Why password auth (not Entra) for Postgres + KC.** The app talks plain Npgsql and Keycloak talks
  JDBC; neither can use the Entra-token plugin Aspire's default provisioning assumes — hence
  `.WithPasswordAuthentication(...)` (`Program.cs:57-59`) and password-based `KC_DB_*`.
- **MailHog is dev-only** — it's never added in publish mode (`Program.cs:65-72`).
- If you later migrate to a managed IdP (Entra External ID), this whole runbook is replaced by an
  app-registration + rewriting the Tenants module's Keycloak admin REST calls to Microsoft Graph.
