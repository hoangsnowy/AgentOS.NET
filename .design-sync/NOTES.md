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
