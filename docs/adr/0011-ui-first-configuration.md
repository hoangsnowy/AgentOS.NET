# ADR-0011 — UI-first configuration: settings registry, scopes, precedence

- **Status:** Accepted (2026-10-17)
- **Deciders:** AgentOS maintainers
- **Plan:** [Agentic SDLC v2 — Phase C](../plans/agentic-sdlc-v2.md) (owner decision D-G)

## Context

Product configuration is split three ways:

1. **`appsettings.json`** — the whole `Llm` and `Agents` sections, duplicated in Api and Web with
   drift (`Llm:Provider` is `Claude` in one host and `Anthropic` in the other; `OfflineFallback` differs).
   Bound with `IOptions<T>`, so every change needs a redeploy and restart.
2. **`IRuntimeOverrides`** — eight tenant-scoped keys (provider keys, force provider, Azure endpoint,
   GitHub PAT) in the encrypted `config.app_config` store, editable in Settings → LLM.
3. **Ad-hoc AppConfig keys** — `budget/*`, `tools/*`, `prompt/*`, `workspace/*`, each owned by a module
   with its own read/validate code.

The owner's direction: **appsettings is MVP scaffolding**. Only the connection string and pre-login
bootstrap (identity provider, DataProtection, logging, plugin path) belong there; every product setting
is configured in the desktop UI and applies without a restart.

## Decision

### Tiers and precedence

| Tier | Storage | Edited by |
|---|---|---|
| **Bootstrap** | appsettings / env / Key Vault | operator at deploy time |
| **Platform** | `config.app_config`, reserved tenant `__platform`, key prefix `@platform/` | platform admin |
| **Tenant** | `config.app_config`, the tenant's own rows | tenant admin |

A read resolves the first hit of: **operator lock → tenant → platform → seed → default**.

- **Lock** — `Settings:Locks:<key>` in bootstrap config pins a value the UI cannot change (e.g. keep
  `RemoteAgent:AutoApprove=false` in Production). Reported as source `Locked`.
- **Seed** — the same key path in `IConfiguration` (appsettings/env). Existing deployments keep working
  while their appsettings sections are migrated out; C.6 deletes them and the descriptor default takes over.
- **Default** — declared on the descriptor next to the code that consumes it.

The `@platform/` key prefix keeps platform rows distinct even in the tenant-agnostic in-memory store
used for standalone runs, so no store or schema change is needed.

### Registry

- **`SettingDescriptor`** (Domain, BCL-only): key (`Area:Sub:Key`, identical to the appsettings path so
  seeding is free), kind (text/secret/int/decimal/bool/choice/url/model/json), scope ceiling
  (`Platform` = platform-only, `Tenant` = tenant may override), default, label/help/category for the UI,
  range/choices, optional custom validator.
- **`ISettingsContributor`** — each module declares its descriptors next to the code that reads them
  (Llm → `Llm:*`, Pipeline → `Agents:*`, …). No central list to drift.
- **`ISettingsCatalog`** — built once at startup into a `FrozenDictionary` (O(1) lock-free lookups);
  duplicate keys fail fast.
- **`ISettings`** — async typed reads with source attribution, validated writes, and a monotonic
  `Version` that bumps on every local write so consumers can cache derived objects and rebuild cheaply.
  Writes are validated against the descriptor; an empty value clears the override.
- **`ISettingsAuditSink`** — every write records who/when/scope/before/after (secrets redacted). Lives in
  Domain so Tenants can implement it without other modules referencing Tenants.

### Freshness

Reads go through `IAppConfigStore`, whose EF implementation caches 15 s per process (write-through
locally). A write is therefore visible immediately on the host that made it and within 15 s on the other
host — acceptable for configuration. A cross-host push (Postgres `LISTEN/NOTIFY` bumping `Version`) is
a follow-up if a setting ever needs sub-second consistency.

### Secrets

Values are encrypted at rest by the store (DataProtection). The UI never receives a secret's value —
only whether it is set and a masked suffix. Audit records redact secrets.

## Consequences

- Consumers move from `IOptions<T>` to `ISettings` reads per call (C.4): `LlmClientFactory`, the agents
  (`LlmAgentBase` must stop freezing the provider in its constructor), `GraphExecutor`, `IssueWorkAgent`,
  `TicketDecomposerAgent`, `MafChatClient`.
- `SettingsKeyRegistry` (the `/settings` endpoint allowlist) is replaced by the catalog.
- appsettings shrinks to bootstrap only (C.6); docs/CONFIG.md becomes "deploy-time vs UI".
- New settings never get an appsettings section — they get a descriptor and a UI field in the same PR.
