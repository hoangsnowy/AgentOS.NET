# On-prem install — docker compose, Helm, air-gapped

Run AgentOS.NET on your own hardware: a single host with **docker compose**, or a Kubernetes cluster with the
**Helm chart** — online or fully **air-gapped**. (The Azure path is [DEPLOYMENT.md](DEPLOYMENT.md).)

| Path | Use it for | Lives in |
|---|---|---|
| docker compose | one server / a workstation / an evaluation | [`deploy/compose`](../deploy/compose) |
| Helm chart | Kubernetes (kind, k3s, OpenShift-style, managed) | [`deploy/helm/agentos-net`](../deploy/helm/agentos-net) |
| Offline bundle | air-gapped sites (no registry or internet access) | [`deploy/bundle.sh`](../deploy/bundle.sh) |

Both installs run the same topology as the Aspire app model (`infra/AgentOs.AppHost/Program.cs`, the source of
truth):

```
browser ──► Web (Blazor desktop, :8080) ──┐                      ┌──► Postgres 18
        └─► Keycloak (realm `agentic`) ───┼── back channel ──────┤     db `agentos`  (app, every module schema)
            Api (REST, /mcp, runner hub) ─┘   (in-network http)  └──  db `keycloak` (Keycloak's own store)
            Ollama (optional, OpenAI-compatible /v1)
```

- **Keycloak** is the AgentOS.NET image built from `infra/keycloak` — Keycloak 26.6 with the Postgres driver,
  health endpoints, the `agentic` realm and the AgentOS.NET login theme baked in. The realm is imported on the
  first start only; a one-shot step ([`configure-realm.sh`](../deploy/helm/agentos-net/files/configure-realm.sh))
  then points it at your install on every start/upgrade: redirect URIs, client secret, seed passwords.
- **Api + Web** run in `Production`, apply every module's EF migrations at boot (serialised by a Postgres
  advisory lock), and bootstrap row-level security (the `agentos_rls` role + policies, [ADR-0012](adr/0012-postgres-row-level-security.md)).
- **DataProtection** keys (they encrypt every tenant secret) are stored in Postgres and **wrapped with a
  certificate** the database never holds ([dataprotection-dr-runbook.md](dataprotection-dr-runbook.md)).
- Health: `/alive` (liveness) and `/health` (readiness — Postgres + Keycloak) on both hosts; Keycloak
  `/health/{started,live,ready}` on its management port 9000.

> Configuration here is **deploy-time only**. LLM providers and keys, agent profiles, budgets and every other
> product setting are set in the desktop (**Settings**) per [ADR-0011](adr/0011-ui-first-configuration.md).

---

## 1. docker compose — cold install

**Needs:** Docker Engine 24+ with the compose plugin, `openssl`, 4 vCPU / 8 GB RAM / 20 GB disk (more with
Ollama). From a clone of the repository (or an extracted [offline bundle](#3-air-gapped-install)):

```bash
cd deploy/compose
cp .env.example .env
./init-secrets.sh                 # random secrets into .env + the DataProtection certificate
docker compose build              # skip when the images were loaded from a bundle
docker compose up -d --wait       # Postgres → Keycloak → realm config → Api + Web, all healthy
```

`init-secrets.sh` replaces every `CHANGE_ME` in `.env` (mode 600) with a random value and writes
`dataprotection/dataprotection.pfx`. Both are git-ignored. **Back them up** — see [Backup](#5-backup--restore).

Open **http://localhost:8080** and [sign in as the first admin](#first-admin-login).

What `up` does, in order: Postgres creates the `agentos` and `keycloak` databases on first boot → Keycloak
imports the realm → `keycloak-config` (one-shot) rotates the client secret to `KEYCLOAK_WEB_CLIENT_SECRET`,
sets the redirect URIs to `WEB_PUBLIC_URL`, sets `operator`'s password, disables the demo `member` (unless
`MEMBER_PASSWORD` is set) and turns email verification off unless `SMTP_HOST` is set → Api + Web migrate the
database and turn healthy. `docker compose ps` shows the state; `docker compose logs -f web` the details.

### On a server: TLS in front

Outside `localhost` the browser needs **https** — the auth cookies are `Secure` in Production and Keycloak
requires TLS for non-local clients. Keep the default `BIND_ADDRESS=127.0.0.1` and put a TLS reverse proxy in
front that forwards `X-Forwarded-For`/`X-Forwarded-Proto` and WebSockets (the desktop is Blazor Server). For
example with Caddy (automatic certificates; use `tls internal` or your PKI on a closed network):

```caddyfile
agentos.corp.example { reverse_proxy 127.0.0.1:8080 }
sso.agentos.corp.example { reverse_proxy 127.0.0.1:8081 }
```

Then in `.env`: `WEB_PUBLIC_URL=https://agentos.corp.example`,
`KEYCLOAK_PUBLIC_URL=https://sso.agentos.corp.example`, and `docker compose up -d --wait` again (the realm
step re-runs and rewrites the redirect URIs).

### Local LLMs with Ollama (optional)

```bash
docker compose --profile ollama up -d --wait
docker compose exec ollama ollama pull llama3.1
```

As a **platform admin**, open **Settings → LLM providers** and set **OpenAI-compatible endpoint** to
`http://ollama:11434/v1` (and its model) — then pick provider `Ollama` in an agent profile. On a GPU host,
uncomment the `deploy.resources` block of the `ollama` service.

### Mail (optional)

Set `SMTP_HOST`/`SMTP_PORT`/`SMTP_FROM` (+ `SMTP_STARTTLS`, and `SMTP_AUTH`/`SMTP_USER`/`SMTP_PASSWORD` for
Keycloak) in `.env`. Keycloak reads them at realm import; for a realm that already exists, set them in the
Keycloak admin console (**Realm settings → Email**). The hosts send invitations through an unauthenticated
relay (`Email:*`).

---

## 2. Kubernetes with Helm

**Needs:** Kubernetes ≥ 1.29, Helm 3.14+ or 4, a default StorageClass (bundled Postgres), and for a real
deploy an ingress controller + TLS certificates. The chart deploys Api, Web, Keycloak (bundled or yours),
Postgres (bundled or yours) and optionally Ollama.

### Try it on kind

```bash
kind create cluster --name agentos

# Images: build them (deploy/bundle.sh build), then load them into the node
deploy/bundle.sh build --version 0.6.0
for i in api web keycloak; do kind load docker-image agentos-net/$i:0.6.0 --name agentos; done

# DataProtection wrapping certificate → a Secret (keep the .pfx + password somewhere safe)
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=AgentOS.NET DataProtection" \
  -keyout dp.key -out dp.crt
DP_PASSWORD=$(openssl rand -hex 24)
openssl pkcs12 -export -inkey dp.key -in dp.crt -out dataprotection.pfx -passout "pass:$DP_PASSWORD"
kubectl create namespace agentos
kubectl -n agentos create secret generic agentos-dataprotection --from-file=dataprotection.pfx

helm install agentos deploy/helm/agentos-net -n agentos \
  -f deploy/helm/agentos-net/ci/kind-values.yaml \
  --set secrets.dataProtectionCertificatePassword="$DP_PASSWORD" \
  --wait --timeout 15m

kubectl -n agentos port-forward svc/agentos-agentos-net-web 8080:80 &
kubectl -n agentos port-forward svc/agentos-agentos-net-keycloak 8081:8080 &
```

Open **http://localhost:8080**. `ci/kind-values.yaml` disables the ingress and sets both public URLs to
`localhost` (a secure context, so plain http works there). The chart generates the other credentials — `helm
install` prints how to read them (NOTES).

### Production install

1. **Images** — push them to your registry (`deploy/bundle.sh push registry.corp:5000`, see
   [air-gapped](#3-air-gapped-install)) and set `global.imageRegistry` (+ `global.imagePullSecrets`).
2. **Secrets** — create them yourself instead of letting the chart generate them:

   ```bash
   kubectl -n agentos create secret generic agentos-secrets \
     --from-literal=postgres-password="$(openssl rand -hex 24)" \
     --from-literal=keycloak-admin-password="$(openssl rand -hex 24)" \
     --from-literal=keycloak-web-client-secret="$(openssl rand -hex 32)" \
     --from-literal=operator-password='<first admin password>' \
     --from-literal=dataprotection-certificate-password="$DP_PASSWORD"
     # optional: member-password, smtp-password
   kubectl -n agentos create secret generic agentos-dataprotection --from-file=dataprotection.pfx
   ```

3. **TLS** — `kubernetes.io/tls` secrets named in `ingress.tls.secretNames` (or cert-manager annotations under
   `ingress.annotations`).
4. **Values** (`my-values.yaml`):

   ```yaml
   global: { imageRegistry: registry.corp:5000 }
   secrets: { existingSecret: agentos-secrets }
   ingress:
     className: nginx            # your controller
     hosts: { web: agentos.corp.example, keycloak: sso.agentos.corp.example }
   postgresql:
     persistence: { size: 50Gi, storageClass: fast-ssd }
   ```

5. `helm install agentos deploy/helm/agentos-net -n agentos -f my-values.yaml --wait --timeout 15m`

Every value is documented in [`values.yaml`](../deploy/helm/agentos-net/values.yaml). The chart fails at render
time — not at runtime — on configurations that cannot work (key wrapping required but unconfigured, an
external Keycloak without its URL, several Web replicas without sticky sessions).

### Your own Postgres or Keycloak

- **External Postgres** — `postgresql.enabled=false` and `externalDatabase.{host,port,database,username,sslMode}`
  (password: `postgres-password` in the secret). Create the `agentos` and `keycloak` databases first. The user
  must **own** them and hold **`CREATEROLE`**: the hosts create the `agentos_rls` role, its grants and the
  row-level-security policies at every boot. Without it RLS stays off and the hosts log
  `Row-level security bootstrap FAILED` (isolation falls back to application filters only).
- **External Keycloak** — `keycloak.enabled=false`, `keycloak.publicUrl=https://sso.corp.example`. Import
  [`infra/keycloak/agentic-realm.json`](../infra/keycloak/agentic-realm.json) and copy
  [`infra/keycloak/themes/agentos`](../infra/keycloak/themes/agentos) into its themes directory first; the realm
  Job then configures the client against `keycloak.internalUrl` (default: the public URL) with the master-realm
  admin from the secret. Set `keycloak.configureRealm=false` to manage the realm yourself (redirect URI
  `<web>/*`, client secret = `keycloak-web-client-secret`).
- **Azure Key Vault** wrapping instead of a certificate: `dataProtection.keyVaultKeyId` (pods need a workload
  identity with *Key Vault Crypto User*).

### Scaling

Keep `web.replicaCount: 1` unless your ingress has **cookie affinity** (Blazor Server keeps a live circuit
per tab; the DataProtection ring is shared through Postgres, so cookies survive across replicas). The bundled
Keycloak and Postgres are single instances — for HA use your own clustered Keycloak and a managed/HA Postgres.

---

## 3. Air-gapped install

On a connected machine with the repository, Docker and Helm:

```bash
deploy/bundle.sh build                      # agentos-net/{api,web,keycloak}:<version>
deploy/bundle.sh save [--with-ollama]       # → dist/agentos-net-<version>.tar.gz + .sha256
```

The bundle holds every image (`docker save`: Api, Web, Keycloak, Postgres[, Ollama]) with a `SHA256SUMS`, the
compose files, the packaged Helm chart, these docs and `bundle.sh` itself. Behind a registry mirror, build
Keycloak from it: `deploy/bundle.sh build --keycloak-image registry.corp/keycloak/keycloak:26.6`.

Carry it over, verify and load:

```bash
sha256sum -c agentos-net-<version>.tar.gz.sha256
tar -xzf agentos-net-<version>.tar.gz && cd agentos-net-<version>
./bundle.sh load                            # checks SHA256SUMS, then docker load
```

- **compose:** `cd compose && cp .env.example .env && ./init-secrets.sh && docker compose up -d --wait --no-build`
  (`.env.example` in the bundle already pins `AGENTOS_VERSION`).
- **Kubernetes:** `./bundle.sh push registry.corp:5000`, then
  `helm install agentos helm/agentos-net-<version>.tgz --set global.imageRegistry=registry.corp:5000 …`
  as in [Production install](#production-install).
- **Ollama models:** copy a model store from a connected host (`~/.ollama/models`, or the `ollama-data`
  volume) into the target's volume — nothing is pulled at runtime.

Nothing in the stack calls the internet at runtime except the LLM providers you configure.

---

## 4. Upgrade

1. Read the release notes (breaking changes, Postgres/Keycloak version moves). **Back up first** (§5).
2. Get the new images (`git pull && docker compose build`, or load the new bundle).
3. Roll:
   - compose: set `AGENTOS_VERSION` in `.env` (bundle installs) → `docker compose up -d --wait`
   - Helm: `helm upgrade agentos <chart> -n agentos -f my-values.yaml --wait --timeout 15m`
4. Database migrations run at boot, per module, serialised by an advisory lock — rolling several replicas is
   safe. The realm step re-runs (idempotent). Verify with `/health` and a sign-in.

Notes:

- **Keycloak never re-imports an existing realm.** Realm changes shipped in a release (a new role, client
  setting) are applied by `configure-realm.sh` or listed in the release notes for the admin console.
- **Postgres major upgrades** (e.g. 18 → 19) need `pg_upgrade` or dump/restore — a new image tag alone will
  refuse the old data directory. Minor upgrades (18.x) are a tag bump.
- **Rollback** = restore the pre-upgrade backup and redeploy the previous version (migrations are forward-only).
- Never rotate the DataProtection certificate by replacing it: add the new one and keep the old until every
  key it wrapped has expired (90 days by default) — see the [DR runbook](dataprotection-dr-runbook.md).

---

## 5. Backup & restore

Back up **three things, kept apart**: the `agentos` database (holds the DataProtection key ring *and* the
encrypted tenant secrets — one dump, same point in time), the `keycloak` database (users, credentials), and the
**DataProtection certificate + its password** (`deploy/compose/dataprotection/` + `.env`, or the
`agentos-dataprotection` and main Secrets). A database without its certificate cannot decrypt a single tenant
secret.

**compose:**

```bash
cd deploy/compose
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB"' > agentos-$(date +%F).dump
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -Fc keycloak'      > keycloak-$(date +%F).dump
```

**Kubernetes (bundled Postgres):**

```bash
kubectl -n agentos exec agentos-agentos-net-postgres-0 -- sh -c 'pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB"' > agentos.dump
kubectl -n agentos exec agentos-agentos-net-postgres-0 -- sh -c 'pg_dump -U "$POSTGRES_USER" -Fc keycloak'      > keycloak.dump
```

**Restore** (compose shown; on Kubernetes scale `web`, `api` and `keycloak` to 0 and use `kubectl exec -i`):

```bash
docker compose down                              # add -v to start from empty volumes
docker compose up -d --wait postgres
for db in agentos keycloak; do
  docker compose exec -T postgres sh -c "pg_restore -U \"\$POSTGRES_USER\" -d $db --clean --if-exists --no-owner --no-privileges" < $db-<date>.dump
done
docker compose up -d --wait
```

`--no-owner --no-privileges` matters: the dump carries grants to the `agentos_rls` role, which a fresh server
does not have yet — the hosts re-create the role, its grants and the policies when they boot. Then **verify
decryption** before declaring recovery: sign in as a tenant admin → Settings → saved API keys must show as
set (DR drill steps: [dataprotection-dr-runbook.md](dataprotection-dr-runbook.md#drill-do-this-once-before-go-live)).

---

## First admin login

The realm ships one administrator, **`operator`** — tenant admin of the `default` tenant **and**
`platform-admin` (platform-wide settings). Its password is the one you set:

- compose: `OPERATOR_PASSWORD` in `deploy/compose/.env`
- Helm: `kubectl -n agentos get secret <secret> -o jsonpath='{.data.operator-password}' | base64 -d`

After the first sign-in:

1. **Settings → LLM providers** — add your provider key (or the Ollama endpoint). Keys are per tenant and
   encrypted with the wrapped key ring.
2. **System → About** — every service Up, and **Key ring: Certificate** (Degraded = unwrapped).
3. Invite people (**Settings → Users**) or let them sign up; give admins the `admin` role and only platform
   operators `platform-admin` in the Keycloak admin console (`<keycloak>/admin`, master-realm user
   `KEYCLOAK_ADMIN_USERNAME`). Keycloak 26 marks that bootstrap admin as temporary — create a permanent
   admin there and delete the bootstrap one.

---

## Troubleshooting

| Symptom | Cause → fix |
|---|---|
| `docker compose config`: *required variable … is missing* | `.env` missing a secret — run `./init-secrets.sh`. |
| Keycloak: *HTTPS required* / browser drops the session | Plain http on a non-localhost name. Put TLS in front ([§1](#on-a-server-tls-in-front)) and use https public URLs. |
| *invalid redirect_uri* at sign-in | `WEB_PUBLIC_URL` / `web.publicUrl` differs from the URL in the browser. Fix it and re-run `docker compose up -d` / `helm upgrade` (the realm step rewrites the redirect URIs). |
| Sign-in loops, *IDX10205 issuer invalid* | `KEYCLOAK_PUBLIC_URL` differs from how browsers reach Keycloak — the issuer is fixed to it. |
| Api/Web exit: *RequireKeyWrapping is on but the key ring is unwrapped* | The certificate is not mounted or its path/password is wrong. Check `dataprotection/` and `DATAPROTECTION_CERTIFICATE_*`, or the Helm Secret. |
| HTTP 431 on Keycloak | Oversized cookie jar on a shared host name — compose/Helm raise Keycloak's limit to 128K; use dedicated host names per install (and `COOKIE_PREFIX` / `web.cookiePrefix`). |
| Log: `Row-level security bootstrap FAILED` | The database user lacks `CREATEROLE`/ownership (external Postgres). Grant it, restart. |
| Log: `libgssapi_krb5.so.2: cannot open shared object file` | Harmless — Npgsql probing Kerberos; the app uses password auth. |

Related: [DEPLOYMENT.md](DEPLOYMENT.md) (Azure), [CONFIG.md](CONFIG.md) (settings),
[keycloak-prod-runbook.md](keycloak-prod-runbook.md), [dataprotection-dr-runbook.md](dataprotection-dr-runbook.md).
