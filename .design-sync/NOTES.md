# Design sync notes

- Target: the existing Claude Design project **AgentOS Design System**
  (`bec4d21d-0fad-4d66-9abe-ed8c711b75c8`, `PROJECT_TYPE_DESIGN_SYSTEM`) — the source of truth named in CLAUDE.md.
  Never create a new project for this repo.
- This repo is a Blazor Server app, not a JS component library: the `/design-sync` converter (storybook / package
  shapes, `_ds_bundle.js`) does not apply, and there is deliberately no `.design-sync/config.json`. The project's
  `components/**` (`.jsx` + `.d.ts` + `.prompt.md`) are hand-kept React twins of the `Btn` / `Field` / `Panel` /
  `Icon` vocabulary in `src/AgentOs.Web.Shell`.
- A UI change is synced as hand-authored screen cards under `ui_kits/agentos/*.html`: first line
  `<!-- @dsCard group="UI kit" viewport="WxH" name="…" subtitle="…" -->`, `<link rel="stylesheet" href="../../styles.css">`,
  only tokens that exist in the project's `tokens/*.css` (check with `get_file` before writing).
- Mirror what the running app shows (capture the lab screen first), monochrome inline SVG icons (an emoji such as ⚡
  renders coloured), `white-space: nowrap` on badges / status text.
- Render each card in light and dark before uploading (local mirror of `styles.css` + tokens, Playwright, `data-theme="dark"`).
- Upload: one `finalize_plan` with exact paths (no deletes unless replacing a card), then `write_files` with
  `localPath`. Update the pushed-screens list in CLAUDE.md (Design workflow section) with the new paths and date.
- 2026-10-22: pushed `board.html` (tabs incl. Autopilot + Knowledge), `board-try-the-app.html`, `board-autopilot.html`,
  `board-knowledge.html`.
- 2026-10-23: pushed `insights-delivery.html` (#272) and `board-knowledge-practices.html` (#273). The MVP dock (#271:
  Home · Board · Insights · Settings) is app data, not a component — no card shows the dock's items.
- 2026-10-24: pushed `settings-autopilot.html` (new, #279) and replaced `agent-profiles.html` (#278 — Board agents /
  Workflow pipeline tables, "Computer's default"), `settings-prompts.html` (#278 — Board agents first) and `board-autopilot.html`
  (#279 — live limits, the real merge rule, the plan-limit wait card). The older `agent-profiles.html` inlined its own
  token copy (217 KB); it now uses the shared frame (`../../styles.css`). Keep a full local mirror of the project's
  `tokens/*.css` (fetch with `get_file`) — a stale mirror hid that dark `--warn-soft` exists and rendered the wait card unreadable.
- 2026-11-01: replaced `settings-autopilot.html` (Run time limits #289, Files for the AI #290, When a run ends without
  its answer #295, Try the app #296) and pushed `workflow-ticket-flow.html` (#285 + the Design step #292),
  `board-design-checks.html` (#292 incl. Check again), `board-ticket-files.html` (#290), `activity-tool-calls.html`
  (#299 — the Where column; outcome badges are ok / refused / error). Token gaps found while authoring: no on-accent
  text token (white on accent is raw `#fff`), no `--danger` (use `--err`), and light `--warn` / `--ok` are too faint as
  text on white or their soft fills (cards darken them with `color-mix(… var(--text))`) — candidates for
  `--warn-text` / `--ok-text` tokens, to be designed in Claude Design first.
- Sub-agents can't call DesignSync: fetch `styles.css` + `tokens/*.css` (and a template card) yourself into the local
  mirror before delegating card authoring.
