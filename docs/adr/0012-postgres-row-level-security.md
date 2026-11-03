# ADR-0012 — Postgres row-level security for tenant tables

- **Status:** Accepted — stage 1 · 2026-10-17; stage 2 · 2026-11-03 (GAP-14, [threat model](../security/threat-model.md))
- **Context source:** enterprise readiness review E0.1 ([enterprise-readiness.md](../plans/enterprise-readiness.md)), plan 0.9

## Context

Tenant isolation was enforced only by the application: EF global query filters plus explicit `TenantId`
predicates in repositories and Dapper SQL. A forgotten predicate, a new raw query, or an `IgnoreQueryFilters()`
in the wrong place would return another tenant's rows, and nothing below the application would stop it. An
enterprise security review expects the database itself to bind a session to a tenant.

Three facts shape the design:

- **Dev and lab connect as the Postgres superuser**, and superusers bypass RLS even when it is forced.
- **Azure Flexible Server connects as the table owner** (not a superuser). Owners bypass RLS unless
  `FORCE ROW LEVEL SECURITY` is set — and a role with `BYPASSRLS` can't be created there without a superuser.
- Not every database call knows its tenant from the caller: the runner handshake on the SignalR hub, background
  workers (scheduler, autopilot, evidence drain, runner revalidation), anonymous sign-up and boot run with no
  signed-in user, and `config.app_config` holds platform-scope rows every tenant reads.

## Decision

**Stage 1 (2026-10-17)** introduced the mechanism: a bootstrap step, a restricted role, a per-connection switch,
and one policy per tenant table that let a session with **no tenant see every row** (so the paths above kept
working). **Stage 2 (2026-11-03)** closes that escape. The mechanism, as it stands:

1. **Bootstrap** — once per boot, after every module migrated, under an advisory lock, idempotent
   (`RowLevelSecurity.BootstrapSql`):
   - creates a `NOLOGIN NOSUPERUSER NOBYPASSRLS` role `agentos_rls` and makes the connection user a member with
     `SET` (PG16+ syntax where available);
   - grants DML on every module schema, with default privileges for future tables;
   - **append-only tables** (`RowLevelSecurity.AppendOnlyTables` = `tenants.audit_events`, GAP-11b):
     `REVOKE UPDATE, DELETE, TRUNCATE` from the role — after the schema-wide grants, which run every boot;
   - enables **and forces** RLS on every table in `RowLevelSecurity.Tables`, each with one policy:
     ```sql
     USING / WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')
                         OR current_setting('app.platform', true) = 'on')
     ```
     An empty or unset tenant matches **no row**; only an explicit platform scope widens the session.
   - `config.app_config` also gets `agentos_shared_read`: `FOR SELECT USING ("TenantId" = '__platform')` — every
     tenant reads the platform settings its own resolve against; writing them needs the platform scope (an
     `UPDATE` / `DELETE` of a platform row by a tenant session touches nothing, an `INSERT` is refused).
   Each table's statements run in one `DO` block, so a policy is never missing while live traffic queries the
   table; a deadlock or lock timeout against live traffic is retried twice.
2. **Per connection** — the module contexts (EF `RowLevelSecurityInterceptor`) and the Dapper reads
   (`OpenWithTenantAsync`, `OpenForTenantAsync`) run `SET ROLE agentos_rls` and set `app.tenant_id` and
   `app.platform` from the flow's scope. Npgsql resets session state when a pooled connection returns. Every
   Dapper read now goes through this switch (stage 1 left several on the login user, which an owner login would
   see nothing through once "no tenant" means nothing).
3. **The scope** of a flow, innermost first:
   - `RowLevelSecurity.PlatformScope(reason)` — every tenant's rows, for work that is cross-tenant by nature.
     Counted on `agentos.rls.platform_scope{reason}`. The connection still runs as the restricted role, so the
     audit trail stays append-only even here. Uses: `autopilot-scan` (`WorkspaceRepository.ListAutopilotAsync`),
     `scheduler` (`EfScheduledRunStore.ClaimDueAsync`), `runner-handshake` (`EfRunnerDirectory.FindForPairingAsync`
     — the token is verified against the returned hash afterwards), `runner-revalidation`
     (`EfRunnerDirectory.FindStatusesAsync`), `platform-settings` (the desktop's `SettingsFields` around a
     platform-scope write, after re-checking the signed-in principal's `platform-admin` role — `SettingsService`
     never enters the scope itself and refuses a platform write outside one), `audit-platform` (the `_platform`
     audit chain);
   - `RowLevelSecurity.TenantScope(tenant)` — binds the flow to a tenant it learned from a trusted source other than
     the caller: a signed invitation token (sign-up is anonymous), a verified runner row (the rest of the hub
     handshake), an audit entry's own tenant (the audit writer), a tenant export / purge;
   - `RowLevelSecurity.TenantScopeIfUnbound(tenant)` — an explicit tenant **fills** a flow that has none (a
     background worker, the runner hub) and never overrides a flow's own: used by tenant-explicit stores
     (`EfAppConfigStore`, `EfApprovalStore`, the Dapper repositories, the evidence drain, the autopilot log, preview re-attach). A flow
     bound to tenant A that names tenant B still sees nothing of B;
   - otherwise the ambient identity (runs, Board sessions, the scheduler's run; every Blazor circuit event through
     `CircuitIdentityHandler`), else the authenticated HTTP request's `tenant` claim, else **none — nothing**.
4. **Tables** — `RowLevelSecurity.Tables` is the single list (20):
   - `pipeline.pipeline_runs`, `run_metrics`, `orchestrations`, `scheduled_runs`;
   - `sessions.sessions`, `session_repos`, `runners`, `ticket_files`, `design_reviews`, `approvals` (E1.5);
   - `tools.tool_invocations`;
   - `workspaces.workspaces`, `workspace_repos`, `autopilot_events`, `ticket_flows`;
   - `memory.nodes`, `memory.edges`;
   - `config.app_config` (stage 2), `tenants.audit_events` (stage 2), `tenants.invitations` (stage 2, GAP-11).

   `RowLevelSecurity.ExemptTables` lists the tables with a `TenantId` column deliberately outside RLS, with the
   reason: `tenants.api_tokens` (looked up by prefix while authenticating, before any tenant is known; EF filter +
   explicit predicates, only the SHA-256 of a token is stored). A real-Postgres test fails on a tenant table that is
   in neither list. `RowLevelSecurityDocsTests` reads this list back and fails when it and the code diverge.
5. **Before the bootstrap ran** (boot: migrations, module seeds) **and when RLS is off**, connections keep the login
   user and set `app.platform = 'on'`, so policies an earlier boot left behind never hide rows from a process that
   runs without the database wall on purpose (a forced policy binds an owner login).
6. **Failure is not silent.** `RowLevelSecurity.EnsureAtStartupAsync` (called by both hosts through
   `UseAgentOsRowLevelSecurityAsync`): outside Development a failed bootstrap **stops the host** with a
   plain-language `RowLevelSecurityBootstrapException`; in Development it logs an error and runs on application
   filters only. `Persistence:RowLevelSecurity=false` remains the deliberate escape hatch (logged as a warning on
   every boot). The `row-level-security` readiness check reports Healthy when bound, **Degraded** when turned off
   (readiness still passes; `agentos_health_status` shows 0.5) and Unhealthy when it failed.

## Consequences

- A query that forgets its tenant predicate, inside a request, run or desktop event, returns only the caller's
  rows; writing a row for another tenant fails with `42501`; and a connection that lost its tenant returns
  **nothing** instead of everything — a missed push fails closed. `agentos.rls.connections{scope}` counts bound
  connections by `tenant` / `platform` / `none`; a rising `none` points at a path that lost its tenant.
- Proven on real Postgres: `RowLevelSecurityStageTwoRealPostgresTests` (every table in the list, catalog-driven
  seeding: tenant, none, platform, cross-tenant writes, append-only audit, shared platform settings, a non-superuser
  owner bound by FORCE), `RowLevelSecurityEnabledPathsRealPostgresTests` (the platform scans, Dapper reads, evidence
  drain and app_config through the real module code with RLS switched on), `AuditAndInvitationsRealPostgresTests`,
  and the E2E `RowLevelSecurityRealDbTests` on the running stack.
- Every connection open costs one extra round-trip (`SET ROLE` + two `set_config`).
- RLS binds sessions against **forgotten predicates**; it is not a boundary against SQL injection (an injected
  statement could set the session settings). Platform scopes are counted, not audited per call.
- **Operations:** the database user needs `CREATEROLE` (or a DBA pre-creates `agentos_rls` and grants it). Azure
  Flexible Server's admin user has it. A user without it now stops the host outside Development instead of running
  without RLS — grant it, or set `Persistence:RowLevelSecurity=false` knowingly.
- The audit trail is append-only for the application; the tenant purge (a platform-admin erasure, recorded on the
  `_platform` chain) deletes as the login user, not the restricted role.
