---
name: design-review
description: >
  Enforce the AgentOS.NET design system on any Blazor UI work so screens stay visually consistent
  with the desktop shell. Loads docs/design/design-system.md as the contract, runs a phantom-CSS
  class detector (catches class names used in .razor but defined in no CSS — the Spine/Users
  layout-breakage root cause), maps stray markup back to the defined vocabulary (prefs-*,
  .page-head, .settings-table, Panel/Btn/Field/Icon), and applies the design-system checklist
  (tokens-only, one accent, a11y, Keycloak-login mirrors the shell, "AgentOS.NET" branding). Use when
  the user says "review this UI", "design check", "is this consistent", "fix the layout", before
  adding/editing any screen or component, or invokes "/design-review".
---

Review and align one Blazor screen/component (or the whole `src/AgentOs.Web` UI) to the AgentOS.NET
design system. The shell is a desktop metaphor (KDE Breeze); every app is a window. Inconsistency
is the bug this skill exists to kill.

## The contract

[docs/design/design-system.md](../../../docs/design/design-system.md) is the single source of truth
— tokens, components, motion, a11y, the "adding a screen" checklist, known debt. Read it first.
Tokens live in `src/AgentOs.Web/wwwroot/app.css` `:root`; components in `Components/UI/*.razor`.

## Step 1 — Phantom-class scan (do this first, always)

A phantom class is referenced in `.razor` but defined in **no** CSS, so it renders unstyled. This
caused the Spine/Users "no spacing, doesn't match other windows" breakage.

```pwsh
pwsh .claude/skills/design-review/check-classes.ps1
```

It lists `token <- files`. Triage each hit:

- **Structural phantom** (`admin-page`, `admin-head`, `admin-section`, `admin-tbl`, `admin-invite*`)
  → real bug, fix in Step 2.
- **Marker class** (`spine-app`, `users-app`, `evidence-app`) → intentional per-app root hook for
  tests/JS, usually fine to leave unstyled. Confirm it carries no layout expectation.
- **Possible false positive** → a third-party class (e.g. `Z.Blazor.Diagrams`) or a name applied
  only via interpolation. Verify before "fixing".

## Step 2 — Map to the DEFINED vocabulary (reuse, don't invent)

The polished apps (SystemApp) use these. Match them:

| Need | Use (defined) | NOT (phantom / hand-rolled) |
|---|---|---|
| Window content padding | top-level children are `.page-head` / `Panel` / `.card` (`.appwin-body` pads these) | a wrapper like `.admin-page` |
| Page title + subtitle | `.page-head` + `.page-title` | `.admin-head` / `.admin-sub` |
| Titled section | `<Panel>` / `.panel` | `.admin-section` |
| Data table | `.settings-table` or `.prefs-tbl` | `.admin-tbl` |
| Settings rows | `.prefs-section` / `.prefs-row` / `.prefs-label` | bespoke grid |
| Button / input / icon | `Btn` / `Field` / `Icon` | raw `<button>` / `<input>` / inline `<svg>`/emoji |
| Form (width-constrained) | flex/grid wrapper with `gap: var(--space-…)`; don't let inputs span the full window | a phantom `.admin-invite` (block → full-width input) |

Rule: **only `var(--…)` tokens** — zero raw hex/px. If a value is missing, add the token first.
If a genuinely new pattern is needed, define it in `app.css` (token-based) rather than leaving a
phantom — and note it, the design-system doc warns against parallel vocabularies.

## Step 3 — Checklist (from design-system.md §"Adding a screen")

- [ ] App registered in `AppCatalog` + rendered in `WindowHost` (not a route).
- [ ] Reuses `Btn`/`Field`/`Panel`/`Icon`; no hand-rolled equivalents.
- [ ] Only `var(--…)` tokens; no raw hex/px.
- [ ] One accent (`--accent`); state colors (`--ok/--warn/--err`) only for state, never decoration.
- [ ] Keyboard-reachable; `:focus-visible` ring on every control; a11y name on icon-only buttons.
- [ ] Legible in light + dark × light + dark wallpaper; glass stays readable.
- [ ] Settings (if any) live in the System app, deep-linked — not inline.
- [ ] Branding is **AgentOS.NET** everywhere (never "AgentOs"); the Keycloak login theme
      (`infra/keycloak/themes/agentos`) mirrors the shell tokens + mark.

## Step 4 — Verify (match the surface to the change; see CLAUDE.md "Verification")

- **Pure UI / layout** → standalone Web is enough: `dotnet run --project src/AgentOs.Web`, open the
  app, screenshot light + dark. Re-run Step 1 after edits — it must come back clean (or only
  intentional markers).
- **Touches auth / data / tenant** → standalone only proves the degraded path. Verify on the full
  Aspire stack (`dotnet run --project infra/AgentOs.AppHost`; Web `https://localhost:5180`, login
  `operator`/`operator`, realm `agentic`) and hand the user a drivable URL so they can confirm too.

## Output

A short report: phantom hits (triaged), the vocab swaps applied, checklist pass/fail, and the
verification surface used with proof (screenshot or the user-drivable URL). No praise; just what is
inconsistent and the fix.
