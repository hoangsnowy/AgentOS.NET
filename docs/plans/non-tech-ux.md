# Non-technical builders — UX plan

Most people who will use AgentOS.NET are not developers: the first real customer is a xiangqi academy owner who
wants a teaching platform built (DeltaByteLab/VNChessAcademy, built through the lab as the acceptance test). A
feature that needs an environment variable, a terminal, or reading an agent's raw output is not done.

Research (2026-10-20) of Lovable, Bolt, Replit Agent, v0, Devin, GitHub Copilot coding agent, Cursor background
agents, Claude Code on the web and Base44, grounded in the current Home and Board screens. Ranked by impact for a
non-technical user against effort.

| # | Change | Borrowed from | Effort | Status |
|---|---|---|---|---|
| 1 | **Failure card + "Fix it for me"** — classify a failed task (no AI account, computer offline, GitHub access expired, build failed, tests failed, agents couldn't agree), show what happened + one primary action, raw text under *Details*; cap automatic retries at 3, then "This needs a person". | Lovable "Try to fix", Bolt auto-fix | M | *Try again* shipped (#228) |
| 2 | **"Connect this computer" with a code** — download the helper, open it, type a short code; the page turns "Connected ✓" live. Reuses the existing one-time-code exchange (`/runner/pair/exchange`). | Claude Code Remote Control, `gh auth login` device codes | M | |
| 3 | **Plain-language progress** — fixed steps (Understanding → Planning → Writing the code → Checking it builds → Testing → Ready for you) instead of a timestamped log; *Stop* and *Tell the agent something*; raw log one click away. | Copilot session logs, Replit checkpoints | M | *Stop* shipped (#230) |
| 4 | **Plan first by default** — Home's main action is "Show me a plan" → "Looks good — build it"; auto-pick the board/repo when there is one. | Replit / Lovable Plan mode | M | |
| 5 | **Hide engine/CLI choices** — one question "Where should the AI work? On our servers / On my computer"; the CLI is detected from the paired computer; engine under *Advanced* as Fast / Thorough; "Create session" → "Start working". | — | S | Shipped (#229); CLI auto-detect open |
| 6 | **Review a preview, not a PR** — summary + screenshots from the test run + preview link; "Looks good — publish" (merges) / "Ask for changes"; the PR link under *Developer details*. | Cursor cloud agents, v0 Deploy, Lovable preview | L | |
| 7 | **"Connect GitHub" (GitHub App) instead of the PAT form**, repo picker, "Start a new app" creates the repo. | Claude Code on the web, Lovable | L | |
| 8 | **Cost before and after** — "≈ $0.40–1.20, about 10 min (estimate)" before starting, actual per task, "You've used $18 of $20 this month". Estimates always carry ≈. | Replit per-checkpoint cost | M | |
| 9 | **Undo + "Needs your OK"** — undo a change (close/revert the PR, delete the branch) with a confirm listing what is affected; a badge + notification when the agent needs a decision. | Replit rollback, Devin confidence | M | |
| 10 | **Onboarding copy + sample project** — workspace display name instead of the tenant id; plain checklist titles; a read-only sample project with a finished run; a real-world composer example. | Lovable 2026 onboarding | S | Copy shipped (#229); sample project open |

## Vocabulary

UI copy uses the plain term; code, logs and the developer details keep the technical one.

| Today | Plain term |
|---|---|
| Runner / paired dev machine / Register runner | Your computer / Connect this computer |
| Revoke (runner) | Disconnect |
| Session / Create session | Task / Start working |
| Sessions · Log · Boards · Start from idea (tabs) | Tasks · Activity · Projects · New idea |
| Provider / model key | AI service / AI account |
| Engine Quick / Quality | Fast / Thorough |
| CLI agent / CLI profile | AI helper on your computer (detected) |
| PR / pull request | Proposed change |
| Branch / repo | Draft copy / code project |
| `ai:ready` · Run all ai:ready | OK for AI to start · Start all approved |
| PAT / access token | GitHub connection |
| MaxIterationReached | Stopped — the agents couldn't agree; review needed |
| Failed / Draft / Closed | Couldn't finish / Not started / Done |
| 0 server tokens | Free on our side — uses your own AI plan |

## Sources

Lovable error handling (docs.lovable.dev), Replit Agent + checkpoints (docs.replit.com), GitHub Copilot session
tracking (docs.github.com), Cursor background agents (cursor.com/help), Claude Code on the web
(code.claude.com/docs), Devin 2.1 (cognition.com/blog). GitHub Spark closed to new users on 2026-08-04, so it is
not a model to copy.
