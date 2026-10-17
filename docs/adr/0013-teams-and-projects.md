# ADR-0013 — Teams and projects inside a tenant

- **Status:** Proposed · 2026-10-18 (awaiting owner sign-off)
- **Context source:** owner question *"an organisation with many teams and many repos — how is it managed?"*;
  enterprise review E1.2 (RBAC v2)

## Context

A tenant is one organisation, and inside it everything is flat:

- members hold `admin` or `member` across the whole tenant;
- every member sees every board (a GitHub Projects / Azure DevOps board with its repos), every run, every
  session and every evidence row;
- budget, tool policy, workspace rules and agent profiles exist only at tenant level.

That works for one team. An organisation with five teams and fifty repos needs:

- Team Payments sees and runs only its own boards;
- a budget per team;
- each team's own conventions;
- reviewers with approval rights on their projects only;
- membership driven by the corporate directory, not added by hand.

## Decision (proposed)

Introduce two levels inside a tenant: **Team → Project**. A project is today's *board* plus its repos.

| Level | Owns | Configured by |
|---|---|---|
| **Org** (tenant, unchanged) | SSO, quotas, platform policy, audit, org workspace rules, LLM keys | org admin |
| **Team** | members + team roles, budget cap, team workspace rules, default agent profiles | team admin (or org admin) |
| **Project** (board) | repos, runners, sessions, runs, approval policy | team members per role |
| **Repo** | `AGENTS.md` / `CLAUDE.md` (already read, E0.7), CODEOWNERS, branch protection | the repo itself |

1. **Data:**
   - new `tenants.teams`, `tenants.team_members` (user, team, role);
   - a nullable `TeamId` on boards (`workspaces.workspaces`), runs, sessions and runners. Null means
     org-level, so existing data keeps working unchanged.
   - Every query that already filters by `TenantId` adds the caller's visible team set.
   - RLS (ADR-0012) grows a second predicate: a session setting `app.team_ids` (a CSV of allowed teams, or
     `*` for org admins).
2. **Roles:**
   - **Team roles:** `viewer · developer · approver · admin`, defined in one permission matrix in code.
   - **Org roles:** `member · admin · auditor`. `auditor` is read-only across teams: audit log, Insights,
     evidence.
   - An org admin is implicitly a team admin everywhere.
3. **Directory sync:** Keycloak groups (brokered from Entra ID / Okta, E1.1) map to teams by naming
   convention or an explicit mapping in Settings → Teams. SCIM (E2.1) keeps them current.
4. **Budget and metrics:**
   - The run-admission and budget gates check the **team** cap, then the org cap.
   - Insights gets a Team / Project filter. Cost, tokens and the cache split are already per run, so they roll
     up by `TeamId` with no new event shape.
5. **Rules stack:** agent system prompt → **org rules → team rules** → repo `AGENTS.md`. Each level is fenced
   and budgeted (E1.6 already does org + repo).
6. **UI:**
   - **Settings → Teams:** list, members, role, group mapping, budget.
   - **Board:** project picker filtered to my teams; a team badge on every board and run.
   - **Insights:** a team filter.
   - **Audit log:** a team column.

## Consequences

- Teams can't see or spend each other's resources, and approvers are scoped to their own projects.
- Directory groups decide membership, so leavers drop out with the directory.
- Migration is additive (a nullable `TeamId`). Tenants that never create a team see no change.
- **Cost:** every team-scoped query and the RLS policy gain a team predicate. The visible-team set is computed
  once per request or circuit and cached, so it is not a per-query lookup.

## Build order (once accepted)

1. Teams + memberships + Settings → Teams.
2. Assign boards to teams; visibility filter in Board / Insights / Evidence.
3. Team roles + permission matrix (E1.2), enforced on endpoints and desktop apps.
4. Team budget + Insights filter.
5. Keycloak group → team mapping.
6. RLS team predicate.
