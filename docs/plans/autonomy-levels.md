# Autonomy levels — to Level 5

The owner's goal (2026-10-21): AgentOS.NET runs the whole software lifecycle with agents. A person sets the goal
and is asked only when a decision is genuinely theirs. The acceptance test is DeltaByteLab/VNChessAcademy, built
end to end on the lab; anything that still needs a hand edit there is a missing AgentOS feature
(see the `dogfood-through-agentos` rule).

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
   3 rounds, then escalate (a failure card with "This needs a person").
3. **Acceptance agent.** What a human reviewer did by hand on the lab: read the ticket's criteria and the project's
   `docs/ACCEPTANCE.md`, run the unit tests, bring the stack up and run the Playwright specs, run data checks
   (e.g. the CBL corpus), and post the evidence on the PR. Pass → approve; fail → *Ask for changes* with the
   findings.
4. ✅ (#242, *Build and merge*: on green CI; acceptance agent next) **Merge policy.** Per project: merge on green + acceptance pass (squash, delete branch), or wait for a person.
5. **Environment care.** The known stack landmines (stale Keycloak/Postgres volumes, orphaned containers, ports)
   handled by the acceptance agent's stack step, not by a person.

## What L5 adds

- **Autopilot** per board: a loop that runs 1–4 whenever there is a ready ticket, inside a monthly budget and a
  parallelism cap, with Stop.
- **Escalation** in words: a "Needs you" badge + notification with the one decision required.
- **Report**: a daily summary per project — merged, in progress, blocked and why, spend.
