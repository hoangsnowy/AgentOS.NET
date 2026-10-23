# AgentOS.NET — the MVP

**One sentence:** an AI software team that turns a backlog into reviewed, tested, merged pull requests — on the
team's own computers, with evidence on every change and governance a company can sign off.

Everything below serves one loop. A feature that doesn't make this loop faster, safer or more visible waits.

```
Idea ──► spec + tickets with dependencies            (Discovery: Requirement + Decomposition agents)
     ──► Autopilot — the Autopilot Orchestrator agent decides what runs next, within fixed rules
           ──► each ticket: a MAF workflow  Coding → checks → Reviewer → Testing → merge
                (red checks · conflicts · requested changes · failed acceptance → back to Coding, 3 rounds)
     ──► Try the app — the latest version running on your computer
     ──► Board · Autopilot log · Knowledge · Insights — what happened, why, and what it cost
```

## Who it is for (first)

- **A tech lead / small team** in a .NET or web shop on GitHub: wants the backlog built without babysitting agents.
- **A non-technical product owner** on the same team: describes ideas in plain words, answers the agents'
  questions, tries the app — never opens a terminal.

## Why it wins

1. **Runs on the team's own machines** — each member's Claude / Codex subscription through a paired computer, in a
   scoped sandbox (own folder, allow-listed commands, no force-push, no credential reads). $0 server API cost.
2. **Nothing untested is merged** — a Reviewer agent and a Testing agent check every change, and post their evidence
   on the pull request; failures already on main get their own tickets.
3. **Measured** — every agent call: tokens (fresh · cache read · cache write · output), latency, outcome; per agent,
   per model, per run (Insights).
4. **It asks, it doesn't guess** — stuck work is retried with guidance or turned into one precise question on the
   ticket; your reply is read back and acted on.

**Proof:** VNChessAcademy (a multi-tenant teaching platform with a CCBridge xiangqi library) was built by this loop
on the lab — 25+ pull requests, most merged without a person touching the code.

## In the MVP (the dock)

| App | Role in the loop |
|---|---|
| **Home** | setup checklist (connect GitHub, connect a computer), recent activity |
| **Board** | Projects (Try the app) · New idea · Tasks · Computers · Autopilot · Knowledge · Activity · Files |
| **Insights** | runs, tokens, latency, cost (≈ API for subscription runs), budget |
| **Settings** | AI accounts, agent profiles, Autopilot (who decides, how a ticket runs), people, audit |

**Outside the MVP** (kept, not in the dock): the Workflow studio and the 5-agent pipeline it runs, Terminal, the
Thorough engine, Azure DevOps boards (partial), the Components catalog. Reachable with Ctrl+K / Activities.

## Before the first real users — enterprise gaps, in order

1. **Onboarding without a terminal** — "Connect GitHub" through a GitHub App (repo picker, no PAT), and a
   downloadable helper for "Connect this computer" (today it is `dotnet run`).
2. **Data isolation, stage 2** — an empty tenant on a connection sees *nothing* (today: everything), with an explicit
   platform scope for system workers; then an external security review.
3. **Reliability** — cloud workers so work doesn't depend on a laptop being awake (plan: `cloud-workers-plan`);
   resumable ticket workflows (checkpoints) across restarts.
4. **Deploy** — `azd up` to the customer's Azure, an operations guide, backups.
5. **Product shell** — landing page, docs site, licensing.

## Definition of done (every change)

Enterprise level, as CLAUDE.md's owner directives: tests at every layer, metrics, plain-language UI with an a11y pass,
docs true, verified in the running app — and for anything that touches persistence, identity or real services, on
the full stack / the lab.
