# ADR-0014 — Ticket flow is data: Autopilot runs what the Workflow app shows

- **Status:** Accepted · 2026-10-24
- **Context source:** owner — "what the UI shows must be what runs" (connected features); the Workflow app only knew
  the legacy pipeline roles while Autopilot ran a graph hard-coded in `TicketWorkflow`.

## Context

Two graphs described one product:

- the **Workflow app** (`OrchestrationStudio` → `GraphRunner` → `GraphExecutor`) draws and runs graphs of the legacy
  5-agent pipeline (Requirement / Coding / Testing / QA via `AgentRoleMap`);
- **Autopilot** ran every ticket through a Microsoft Agent Framework workflow whose edges were C# in
  `TicketWorkflow.Build` (Code → Checks → Review → Test → Merge, failures back to Code), and the step-by-step rules
  (`AutopilotPlanner`) encoded the same order a second time.

Nobody could see the per-ticket flow, let alone change it (skip review for chores, skip acceptance testing for docs),
and the next gate (a design / screenshot check) would have meant a third hard-coded copy.

## Decision

1. **The ticket flow is a definition, not code.** `TicketFlowDefinition` (Pipeline module, next to `AgentRoles` and
   `AutopilotSettings`): ordered step nodes + conditional edges. Node kinds: **Code** (Coding (tickets) —
   `IssueWork`), **Checks** (CI), **Review** (`Reviewer`), **Test** (Testing (tickets) — `Acceptance`), **Merge**.
   Each agent node names its `AgentRoles` key (so it runs with that agent's profile and prompt) and may carry label
   filters (*skip tickets labelled …*, *only tickets labelled …*). Edges: `Pass` → the next step, `Fail` → back to
   Code — a fix round counted on the task's persisted `FixRounds` against `Autopilot:MaxFixRounds` (no second
   counter). `TicketFlowDefinition.Default` is exactly the old hard-coded flow.
2. **Safety invariants, checked on save and on every load** (`TicketFlowValidator`, plain-language reasons): starts
   at Code · ends at Merge · Checks always runs before Merge (can't be turned off or filtered) · every ticket, whatever
   its labels, passes at least one merge gate (Review or Test) — every combination of the labels named in the filters
   is checked · no cycle except a fail edge back to Code · every step reachable · steps in their natural order · one
   step per kind · a role the step can actually run. A stored definition that breaks one is refused and the run uses
   the built-in flow (Autopilot logs why, once per board). `TicketFlowPlan.SafetyProblem` re-checks the per-ticket
   plan before a MAF graph is built. `AutopilotPlanner.WithinCaps` stays the outer guard on slots.
3. **Both engines run it.** `TicketWorkflow.Build(steps, plan, …)` creates one executor per step the ticket runs and
   wires `pass` / `fail → Code` / `done` edges from the definition. `AutopilotPlanner` reads the task's plan
   (`PlanTask.Flow`) and makes no move for a step it skips (no review when Review is off for the ticket).
4. **Storage: versioned per tenant, optional per board** — `workspaces.ticket_flows` (Workspaces module, which owns
   the board and Autopilot state), append-only: a save, a revert or "use the default flow" adds a version with who
   and when. `BoardId = Guid.Empty` is the tenant's default; a board's own flow wins. Version numbers run per tenant,
   so a task can **snapshot** the version it started with (`sessions.sessions.TicketFlowVersion`): a ticket under way
   keeps its flow — in the MAF workflow and, after a restart, in the step-by-step passes — while new tickets use the
   current one. Tenant-scoped: `TenantId` on every row, EF query filter, RLS (`RowLevelSecurity.Tables`), every
   store method tenant-explicit; parsed versions are cached per (tenant, version) since versions are immutable.
5. **The Workflow app shows and edits it** — Workflow › *Ticket flow*: the steps as studio node cards (with each
   agent's character) and as a table, an inspector to turn a step on/off, pick its agent and set label filters,
   validation inline, Save / Discard / Revert; members read-only; the step a running ticket is on is highlighted live
   (the only animation). Board › Autopilot summarises the flow in one line and links there.

It is *not* a `SettingDescriptor` (ADR-0011): it is structured, versioned data with its own invariants and editor —
like a workflow graph — but it follows ADR-0011's intent: configured in the desktop, per tenant, never in
`appsettings.json`, read per call.

## Consequences

- What the owner sees is what runs; changing the flow needs no deploy and applies to the next ticket.
- **Adding a step kind is local**: one `TicketStepKind` value + one `TicketStepKinds` entry (rank, roles, merge gate,
  can send back) + one executor in `TicketWorkflow.Create` (+ a planner move if the step-by-step rules should run it).
  The Design / screenshot gate is the first planned one.
- Today each agent step kind has exactly one agent that can run it, so the agent picker offers one choice per step;
  the role is stored and validated so a second implementation (e.g. a security reviewer) only needs registering.
- Label filters read the ticket's labels when it starts (MAF) or on each pass (step by step); a task whose ticket
  left the board falls back to the `type:` label it was created with.
- The legacy 5-agent Workflow graphs (`GraphExecutor`) are unchanged.
