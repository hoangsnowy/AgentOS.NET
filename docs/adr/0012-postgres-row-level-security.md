# ADR-0012 — Postgres row-level security for tenant tables

- **Status:** Accepted (stage 1) · 2026-10-17
- **Context source:** enterprise readiness review E0.1 ([enterprise-readiness.md](../plans/enterprise-readiness.md)), plan 0.9

## Context

Tenant isolation was enforced only by the application: EF global query filters plus explicit `TenantId`
predicates in repositories and Dapper SQL. A forgotten predicate, a new raw query, or an `IgnoreQueryFilters()`
in the wrong place would return another tenant's rows, and nothing below the application would stop it. An
enterprise security review expects the database itself to bind a session to a tenant.

Three facts shape the design:

- **Dev and lab connect as the Postgres superuser**, and superusers bypass RLS even when it is forced.
- **Azure Flexible Server connects as the table owner** (not a superuser). Owners bypass RLS unless
  `FORCE ROW LEVEL SECURITY` is set.
- Not every database call knows its tenant yet: runner pairing on the SignalR hub, background drains and boot
  run with no signed-in user, and two tables (`config.app_config`, `tenants.audit_events`) hold platform-scope
  rows every tenant reads.

## Decision

**Stage 1 (this ADR):**

1. A bootstrap step runs once per boot, after every module migrated, under an advisory lock. It is idempotent:
   - creates a `NOLOGIN NOSUPERUSER NOBYPASSRLS` role `agentos_rls`;
   - makes the connection user a member with `SET` (PG16+ syntax where available);
   - grants DML on every module schema, with default privileges for future tables;
   - enables **and forces** RLS on the nine stage-1 tenant tables, each with one policy:
     ```sql
     USING / WITH CHECK ("TenantId" = current_setting('app.tenant_id', true)
                         OR coalesce(current_setting('app.tenant_id', true), '') = '')
     ```
2. **Per connection**, the module contexts (EF `RowLevelSecurityInterceptor`) and the Dapper read paths
   (`OpenWithTenantAsync`) run `SET ROLE agentos_rls` and set `app.tenant_id`. Npgsql resets session state
   when a pooled connection returns. Migrations and boot run before RLS is enabled.
3. **The tenant** comes from the ambient identity, else the authenticated HTTP request's `tenant` claim:
   - a run pushes the ambient identity;
   - every Blazor circuit event pushes it too, through the new `CircuitIdentityHandler`, so desktop queries
     are bound as well as Api requests and runs.
4. Stage-1 tables:
   - `pipeline.pipeline_runs`, `run_metrics`, `orchestrations`;
   - `sessions.sessions`, `session_repos`, `runners`;
   - `tools.tool_invocations`;
   - `workspaces.workspaces`, `workspace_repos`.
5. If the bootstrap fails (missing privileges), the host still starts, RLS stays off and an error is logged;
   application filters still apply. `Persistence:RowLevelSecurity=false` is the escape hatch.

**Stage 2 (next):**

- bring `config.app_config` and `tenants.audit_events` under RLS, with explicit platform-row rules;
- tighten "no tenant ⇒ visible" to "no tenant ⇒ nothing" once every remaining path (hub, drains) pushes a
  tenant or runs under an explicit, audited bypass role.

## Consequences

- A query that forgets its tenant predicate, inside a request, run or desktop event, returns only the
  caller's rows. Writing a row for another tenant fails with `42501`. Both are proven on real Postgres by
  `RowLevelSecurityRealDbTests`, and the two-tenant UI test still passes.
- Every connection open costs one extra round-trip (`SET ROLE` + `set_config`).
- Stage 1 is permissive when no tenant is known. That is a deliberate step, recorded here and closed by stage 2.
- The circuit identity push also makes ambient-tenant lookups (settings, stores) correct in the desktop
  without hand-plumbing. Audit "who" columns use the pushed display name.
- **Operations:** the database user needs `CREATEROLE` (or a DBA pre-creates `agentos_rls` and grants it).
  Azure Flexible Server's admin user has it.
