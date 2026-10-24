# Autonomy levels — to Level 5

The owner's goal (2026-10-21): AgentOS.NET runs the whole software lifecycle with agents. A person sets the goal
and is asked only when a decision is genuinely theirs. The acceptance test is DeltaByteLab/VNChessAcademy, built
end to end on the lab; anything that still needs a hand edit there is a missing AgentOS feature
(see the `dogfood-through-agentos` rule).

## Three agentic phases

Every ticket flows through three phases (owner, 2026-10-21); the knowledge graph is the memory they share.

| Phase | The agents… | Today | Missing |
|---|---|---|---|
| **1. Agentic Discovery** | idea → research → spec → tickets with acceptance criteria and dependencies | New idea → spec → tickets, dependencies (#240) | a research agent (market / UX / technical), reading the customer's own documents, asking back when the idea is ambiguous |
| **2. Agentic Coding** | ticket → code → pull request; continues from review and CI | on a computer or the servers, scoped, Stop, live steps, Ask for changes, Fix the failing checks | autopilot over a board in dependency order |
| **3. Agentic Review + Testing** | the **Reviewer agent** reads the diff against the ticket and the repo's rules (correctness, security/isolation, tests) → approve, or must-fix findings back to Coding; then the **Testing agent** accepts it against its criteria: unit tests, Playwright on a real stack, data checks → evidence → merge, or back to Coding | Reviewer (#264) + Testing (#244) + Autopilot merge policy, both posting evidence on the PR | review of a cloud worker's change; a person's review folded in |

**Knowledge graph** (ADR-0008, `AgentOs.Modules.Memory`): Discovery records decisions and requirements, Coding records
changed files, PRs and review lessons, Testing records failures, fixes and evidence — and each phase reads what the
others learned. The board shows each ticket's phase (Discovery → Coding → Testing → Done).

## Levels

| Level | The agent… | A person… | State (2026-10-21) |
|---|---|---|---|
| **L1** | writes code when asked | runs everything | ✅ |
| **L2** | takes one ticket to a pull request: clone, edit, build, test, push, PR (`Closes #N`), on the server or on the member's computer, with live steps and Stop | starts each ticket, reviews, merges | ✅ (#228–#236) |
| **L3** | continues on the same pull request from review feedback or failing CI checks (**Ask for changes**, **Fix the failing checks**) | writes the feedback / clicks fix | ✅ this PR |
| **L4** | runs a board in dependency order, accepts its own work against the ticket's acceptance criteria (tests, Playwright on a stack, data checks) and merges on green; on red it fixes itself, bounded | approves the plan, handles escalations | ❌ |
| **L5** | autopilot: keeps picking the next ready ticket, keeps environments healthy, reports evidence; stops only on `needs-human`, budget, or repeated failure | sets goals and budgets | ❌ |

## What L4 needs

1. ✅ (#240) **Ticket dependencies.** "Start from idea" writes them (`Depends on #11`) and the board records them; *Run all
   approved* starts only tickets whose dependencies are merged, at most N at a time, and re-checks when one merges.
   Today it starts everything at once — #12 (import) would race #11 (the module it needs).
2. ✅ (#242, Autopilot) **Self-repair on CI.** When a task's pull request goes red, run *Fix the failing checks* automatically, at most
   *Automatic fix rounds* (Settings › Autopilot, default 3), then escalate (a failure card with "This needs a person").
   The rounds are the task's persisted `FixRounds` — one count for both engines and across restarts.
3. ✅ (#244) **Acceptance agent.** What a human reviewer did by hand on the lab: read the ticket's criteria and the project's
   `docs/ACCEPTANCE.md`, run the unit tests, bring the stack up and run the Playwright specs, run data checks
   (e.g. the CBL corpus), and post the evidence on the PR. Pass → approve; fail → *Ask for changes* with the
   findings.
3c. ✅ (#270) **One ticket = one MAF workflow.** The Autopilot Orchestrator agent decides which ticket starts; a
   Microsoft Agent Framework Workflow (`TicketWorkflow`) then runs it — Coding → checks → Reviewer → Testing → merge —
   with conditional edges back to Coding (red checks, conflicts, requested changes, failed acceptance; the same fix
   rounds, seeded from the task's persisted count). A flow that ends without a merge, or one a restart cut short, is
   picked up step by step. Settings › Autopilot › *Each ticket runs as* (MAF workflow / Step by step); its checks wait
   and poll are settings too.
3d. ✅ **Ticket flow is data** ([ADR-0014](../adr/0014-ticket-flow-is-data.md)). The per-ticket graph is a
   definition the owner sees and edits in Workflow › Ticket flow (per tenant, per project, versioned, revert): steps
   on/off, the agent per step, label filters (no Review for `type:chore`, no Test for `type:docs`). `TicketWorkflow`
   builds its MAF graph from it and the step-by-step rules skip what it skips; a ticket keeps the version it started
   with. Safety invariants (checks always, never neither Review nor Test, fail → Code only) are enforced on save and
   at run time. Next: a Design / screenshot gate as a new step kind.
3b. ✅ (#264) **Reviewer agent.** Between green CI and acceptance testing: a read-only agent reviews the diff in
   context and posts its review on the PR; must-fix findings go back to the Coding agent (same bounded rounds).
4. ✅ (#242, #264, #244) **Merge policy.** Per project: *Build and merge* merges only on green checks + review approved +
   acceptance passed (which needs the member's computer connected) — squash, delete branch — or *Build* waits for a person.
4b. ✅ **Plan limits.** When the member's computer answers with its Claude / Codex plan limit, Autopilot waits until the
   reset (+1 min) — no new work, no retries, no fix rounds spent — says so once in the Autopilot activity and on the
   Board (Autopilot + Computers tabs), and picks the cut-short work up again afterwards.
5. **Environment care.** The known stack landmines (stale Keycloak/Postgres volumes, orphaned containers, ports)
   handled by the acceptance agent's stack step, not by a person.

## What L5 adds

- **Autopilot** per board: a loop that runs 1–4 whenever there is a ready ticket, inside a monthly budget and a
  parallelism cap (Settings › Autopilot: tasks at once, acceptance tests at once, fix rounds, Orchestrator retries,
  checks wait), with Stop.
- **Escalation** in words: a "Needs you" badge + notification with the one decision required.
- **Report**: a daily summary per project — merged, in progress, blocked and why, spend.
