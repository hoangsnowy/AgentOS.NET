# DataProtection key ring — backup & disaster recovery

The DataProtection key ring lives in Postgres (`config.data_protection_keys`, wired by
`AddAgentOsDataProtection` in both hosts). Those keys encrypt **every tenant secret at rest** —
per-tenant LLM API keys, GitHub PATs, board credentials in `config.app_config` — and protect the
auth/OIDC cookies.

> **If the key ring is lost, every encrypted value is permanently unrecoverable.** There is no
> escrow and no recompute. Backups of `app_config` WITHOUT `data_protection_keys` are useless.

## What to back up

| Table | Why |
|---|---|
| `config.data_protection_keys` | The key ring. Without it nothing else decrypts. |
| `config.app_config` | The encrypted tenant secrets themselves. |
| everything else (`pipeline.*`, `tenants.*`, `workspaces.*`, `sessions.*`, `tools.*`) | Run history, tenant registry, evidence — restorable independently. |

Back up the **whole database** (`pg_dump`) — the key ring and ciphertext must come from the same
point in time. A ciphertext newer than the key that encrypted it cannot decrypt.

## Backup procedure

- **Azure (flexible server):** enable automated backups ≥ 7 days AND geo-redundant backup. The
  current bicep (`infra/postgres/postgres.module.bicep`) ships `geoRedundantBackup: Disabled`
  with 7-day retention — enable geo-redundant backup for production.
- **Self-hosted:** nightly `pg_dump -Fc agentos` to off-box storage. The dump contains the key
  ring — treat it with the same sensitivity as a secrets vault (encrypt at rest, restrict access).

```bash
pg_dump -Fc -h <host> -U <user> -d agentos -f agentos-$(date +%F).dump
```

## Restore procedure

1. Provision Postgres, restore: `pg_restore -d agentos agentos-<date>.dump`.
2. Point `ConnectionStrings:DefaultConnection` at the restored server; start the hosts. Migrations
   are idempotent (and serialized by a `pg_advisory_lock` per schema since 0.6.0).
3. Verify decryption BEFORE declaring recovery: sign in as a tenant admin → Settings → the saved
   API-key fields must show their values (a wrong/missing key ring surfaces as decryption errors in
   the `AgentOs.Modules.AppConfig` logs, or as blank values where data exists).
4. Users re-authenticate (cookies issued before the restore may be rejected — expected).

## Wrapping the key ring (do this before go-live)

The ring lives in the same Postgres it protects, so by default a database copy carries the keys that decrypt
its own secrets. Wrap it with a key the database never holds (bootstrap config, set at deploy):

| Option | Setting | Notes |
|---|---|---|
| Azure Key Vault | `DataProtection__KeyVaultKeyId=https://<vault>.vault.azure.net/keys/<key>` | Give the Web + Api managed identities *Key Vault Crypto User* on the key. Key Vault keys rotate in place; old versions must stay enabled while old ring entries exist. |
| Certificate file | `DataProtection__CertificatePath` (+ `__CertificatePassword`) | PFX mounted from a k8s secret / on-prem secret store. Keep every certificate that ever wrapped a live key until those keys expire. |
| Windows cert store | `DataProtection__CertificateThumbprint` | Looked up in CurrentUser, then LocalMachine \ My. |

Then set `DataProtection__RequireKeyWrapping=true` so a host that lost its wrapping config refuses to start
instead of silently writing unwrapped keys. **System → About → Web desktop → Key ring** shows the protection in
force (Degraded when unwrapped outside Development). Back up the wrapping key / certificate **separately**
from the database backup — restoring one without the other loses every tenant secret.

## Key rotation

DataProtection rotates keys automatically (default 90-day lifetime) and keeps old keys in the ring
for decryption — rotation requires no action **as long as the table persists**. Never truncate
`data_protection_keys` to "clean up": old keys still decrypt old ciphertext.

## Drill (do this once before go-live)

Restore last night's dump to a scratch server, boot the Api against it with
`Persistence__RequireDatabase=true`, and confirm a saved tenant LLM key decrypts. An untested
backup is a hope, not a plan.
