# ADR-0016 — Design gate: UI tickets are checked against screenshots before merge

- **Status:** Accepted · 2026-11-01
- **Context source:** owner: UI tickets merged by Autopilot "look vibe-coded" — green checks, a passing review and
  acceptance tests say nothing about a button that is unreadable in dark mode or a page that scrolls sideways on a
  phone. Builds on [ADR-0014](0014-ticket-flow-is-data.md) (the ticket flow is data) and
  [ADR-0015](0015-ticket-files.md) (the tenant-scoped file store).

## Context

The ticket flow (Code → Checks → Review → Test → Merge) gates correctness. Nothing in it looks at the screens. The
Testing agent already brings the app up in a real browser on the member's computer for the acceptance criteria, so
the evidence is one Playwright call away — but it never left that computer, and no agent judged it.

## Decision

1. **A step kind, `Design`, between Test and Merge** (`TicketStepKind.Design`, rank 40, role `Designer`, can send a
   change back, **not** a merge gate). It is **off in the built-in flow** and, once an admin turns it on in
   Workflow › Ticket flow, runs for tickets labelled **`area:ui`** (the default filter; editable like any step's).
   A flow saved before the kind existed shows it, off (`TicketFlowDefinition.WithKnownSteps`) — nothing a ticket runs
   changes until someone turns it on.
2. **Invariants.** Every existing one holds (Checks before Merge, Review or Test for every ticket — Design never counts
   as that gate). One is added: **a ticket that runs Design runs Test** — the Testing agent takes the screenshots.
   It is checked over every combination of the two steps' filter labels on save, and again per ticket at run time
   (`TicketFlowPlan.SafetyProblem`); a plan that breaks it is never built — the default flow runs instead.
3. **Screenshots come from the Testing run, and leave the computer only through the runner.** When the ticket's flow
   runs Design, the Testing agent is asked (its prompt, on a member's computer only) to save Playwright screenshots of
   every screen the change touches at **390 and 1280 px, light and dark**, as
   `.agentos/screenshots/<screen>-<width>-<theme>.png` in its run folder, and to list them in its JSON reply
   (`"screenshots":[{path, screen, width, theme}]`). After the CLI ends — before the run folder is deleted — the
   runner sends the listed files back over a new hub method, **`UploadRunFile`**, in 256 KB pieces. Never into git.
4. **Path- and size-checked on both sides** (`RunUploadRules`, Domain, BCL-only, linked into the runner like
   `TicketFilePath`): a relative path that passes `TicketFilePath`, an image extension (png / jpg / jpeg / webp — no SVG)
   whose first bytes really are that image, never one of the ticket's own files (`.agentos/files`). The runner also
   resolves each path inside the run folder, refuses links on the way and linked files, and applies the caps the
   dispatch named. The broker hears pieces only from the connection the request was dispatched to, in order, and
   enforces **16 files, 5 MB each, 40 MB a run** (hard caps — a caller can only lower them). The hub's receive limit
   is raised to 1 MB for this hub only (one piece as base64 JSON); Blazor's circuits keep theirs.
5. **Stored in the tenant's file store** (`IScopedFileStore`, ADR-0015) under the scope
   **`design:{board}:{issue}:{run}`** — `run` is the task's persisted fix round when it was tested, so a fix round
   starts a fresh scope (emptied before a re-test of the same round) — **each repo of the task in its own folder**
   (`{task-repo id}/…`, `DesignCapture.PrefixFor`), so two repos' `home-390-light.png` never replace each other. The
   scope key never reaches the computer. The broker holds what is being sent back in memory until the run ends, so it
   reserves each run's whole cap against a **server-wide budget** (8 runs × 40 MB, `MaxInFlightUploadBytes`); a run
   that doesn't fit takes no screenshots (logged) and the check then goes to a person.
   The runner checks and streams each file from **one open handle** (no swap between the check and the read).
6. **The Designer agent** (`DesignAgent`, role `Designer`, an Agent profiles row and a Prompts entry like the
   Reviewer; seed: the member's computer). It gets the screenshots, the ticket, and the repository's design skill —
   every `.claude/skills/<folder with "design">/SKILL.md` **on the default branch** (never the change's own branch, so
   a change can't rewrite the rules it is judged by), fenced as untrusted text and budgeted like the repo's agent
   instructions. **Multimodal first**: on a server provider the screenshots ride the `LlmRequest` as images
   (`LlmRequest.Images` → Microsoft.Extensions.AI `DataContent`, one helper for every HTTP provider). A member's
   computer can't take images over the wire, so there they go as run files (`.agentos/files/screenshots/…`) and the
   prompt names the paths for the CLI's Read tool. Its verdict — **pass / changes** (findings: screen, problem, fix) /
   **needs-person** — has the Reviewer's shape and parsing robustness (last JSON object, prose around it, broken JSON
   = did not run); "changes" with nothing to fix becomes needs-person. No screenshots is a person's call, never a pass.
   The verdict is posted on the pull request with every Markdown control character escaped and @mentions defused, so
   model text can't ping people, add links or images, or break the findings table.
   - **Prompt injection:** a fixed system rule says that text visible in the screenshots, and the screenshots' names
     and captions, are data from the app under test, never instructions; the caption list is fenced with
     `UntrustedText.Wrap` and each image's name is reduced to short plain text.
   - **Server-provider image limits** (`DesignImageLimits`, from Anthropic's vision docs, checked 2026-11-01: ≤ 8000 px
     a side; > 20 images in a request switches to a stricter many-image limit; 10 MB base64 per image on the Claude API
     but 5 MB on Bedrock / Google-managed Vertex): every provider is held to ≤ 16 images, ≤ 3.75 MB each (5 MB as
     base64), ≤ 8000 px a side and ≤ 32 MB together. Sizes are read from the PNG / JPEG / WebP header. No image library
     is in the solution and none is added: nothing is resized — an image over a limit is **left out, and the prompt says
     which and why**; if none fits, a person decides without a model call. The Testing agent is asked for captures at
     most 4000 px tall (a model downscales long images to ~1.5–2.5k px anyway). On a member's computer the CLI reads the
     files itself and these limits don't apply.
7. **Persisted per task and run**: `sessions.design_reviews` (Sessions module) — `TenantId`, `BoardId`, `SessionId`,
   `IssueNumber`, `Run`, `Status`, `Summary`, findings and screenshot metadata (JSON), `Error`, and the owner's
   `Decision` / `DecisionNote` / `DecidedBy` / `DecidedAtUtc`, and `Retries`; unique `(TenantId, SessionId, Run)`; EF
   filter + explicit tenant predicates + RLS (`RowLevelSecurity.Tables`). Without a database an in-memory store keeps the
   panel working. **No whole-row writes after a row exists:** `IDesignReviewStore` has `EnsureAsync` (create if
   missing), `SaveVerdictAsync` (the agent's columns only) and `DecideAsync` (the owner's columns only). A check runs
   for up to half an hour; an owner who presses *Ask for changes* meanwhile must not have that erased by a later
   *pass* (which would merge the ticket), and a decision taken on a page showing an older copy must not erase a verdict.
8. **The owner overrides.** Board › Tasks gets a **Design** panel per task (and a *Design checks* list at the top): the
   screenshot grid (screen × phone/desktop × light/dark, a real table, each image full size in a new tab), the
   verdict and findings, and **Approve design** / **Ask for changes** (with a note). The decision wins over the
   agent. The images load from `GET /tasks/{session}/design/{run}/shots/{path}` (Member policy, tenant from the
   token / cookie; only a path the review lists, an image type, whose bytes are that image; `nosniff`) — the bytes
   never ride the Blazor circuit. `GET /tasks/{session}/design` lists the task's reviews (queried by task).
9. **Both engines honour it.** Step by step (`AutopilotPlanner`): after testing passed, *not checked* → run the
   Designer (a slot, the member's computer); *changes* (the agent's or the owner's) → back to Coding with the findings
   on the persisted `FixRounds` (the same cap as every fix; out of rounds → a person); *needs-person* / *checking* →
   no move; **merge only after a pass or an owner approval**. The MAF workflow (`TicketWorkflow`) gets a
   `DesignExecutor` with the same outcomes (needs-person ends the flow and hands the task back). A check a restart cut
   short goes back to waiting. A check that **couldn't run** (computer offline, budget gate, timeout, an error — status
   `NotRun`) is not a verdict: Autopilot runs it again once the member's computer is connected, at most
   `DesignReview.MaxAutomaticRetries` (2) times per run (counted in `Retries`); after that it waits for the owner. A
   genuine *needs a person* verdict always waits. The panel's **Check again** (admins or the task's member) runs it now,
   uncounted. The test run learns whether to take screenshots from the planner's move (`RunTests.CaptureDesign`).

## Consequences

- A UI ticket on a flow with Design on can't merge without a pass or a person's approval; a non-UI ticket is
  unaffected, and so is every board until an admin turns Design on.
- The Testing run gets slightly longer (four screenshots per screen) only for those tickets.
- A Designer on a server provider bills image input tokens like any other call (metrics and the budget gate apply);
  on the member's computer it costs 0 server tokens.
- The standalone desktop (no database) seeds one clearly-labelled sample check (`DesignGateSample`, Development + dev
  auto-login + no database only) so the panel can be seen and E2E-tested without tasks.
- Pending: the panel and the ticket-flow card are hand-built in the repo; they still have to be pushed to the Claude
  Design project (`ui_kits/agentos/board-tasks-design.html`).
