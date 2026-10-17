# AgentOS Design System

> Single source of truth for the AgentOS desktop UI. Read this before adding any screen,
> component, or style. If a value isn't here, it goes through a **token** — never a magic number.

## North star

**An operating system for agents — a desktop world, authentic GNOME / Adwaita.** AgentOS isn't a
dashboard with a sidebar; it's a *desktop metaphor*: a wallpaper, a top bar, the GNOME Dash, CSD
draggable/resizable windows, **virtual workspaces**, a full-screen **Activities overview**, and a
GDM lock/sign-in. Every feature is an **app** you launch into a window, not a route you navigate to.

**The desktop boots to a clean EMPTY wallpaper — no desktop icons, no widgets, no auto-opened
window.** Apps live only in the Dash + the Activities overview; the glanceable control plane is the
**Overview app** (pinned first), NOT a desktop widget. (DesktopWidgets was tried and removed — GNOME
has no desktop widgets. **Do not re-add them.**)

Three feelings, in priority order:

1. **Familiar OS** — anyone who's used GNOME knows where things are. Top bar = Activities + active
   title + clock + status/quick-settings. Dash = launch pinned apps + running-app dots (focused =
   wider accent pill). Windows = stronger shadow when focused, Adwaita CSD controls (min/max/close),
   drag, resize, minimize, grab-a-maximized-window-to-restore.
2. **Calm enterprise** — low-chroma single accent (`#3584e4`), small radii, flat surfaces, restrained
   shadows. It runs all day without fatigue. Color carries *state*, never decoration.
3. **A touch of motion** — alive, not sterile: windows scale-in, the wallpaper drifts, the focused
   dock dot grows to a pill. The Dash is flat — **no magnify-on-hover** (that's macOS/KDE, not GNOME).
   Motion is a reward, never a blocker (`prefers-reduced-motion` kills all of it).

When a design decision is ambiguous, resolve it in that order: **familiar > calm > playful.** A
playful flourish that hurts familiarity or calm loses.

## Where the system lives (the actual source)

| Layer | File | Rule |
|---|---|---|
| **Tokens** | `src/AgentOs.Web/wwwroot/app.css` `:root` block | Every color/space/radius/font/shadow/motion value. Components reference `var(--…)` only. |
| **Theme axes** | same file, `:root[data-theme]` + `:root[data-wallpaper]` | `data-theme` = light \| dark (color). `data-wallpaper` = enterprise-light/dark \| aurora \| midnight \| sunset (bg + glass). Orthogonal — any theme × any wallpaper. |
| **Components** | `src/AgentOs.Web.Shell/UI/*.razor` (primitives + shell host) | The vocabulary. Reuse before you invent. (`TopBar.razor` is the one UI piece still under `AgentOs.Web/Components/UI`.) |
| **Icons** | `src/AgentOs.Web.Shell/UI/Icon.razor` (+ [icon-map.md](icon-map.md)) | One monochrome 24×24 Lucide-style set, `currentColor`. Never inline an `<svg>` or emoji in a page. |
| **Theme JS** | `src/AgentOs.Web/wwwroot/theme.js` | `agenticTheme.*` — persists + applies theme/wallpaper/glass to `<html>` data-attrs. |
| **KC login skin** | `infra/keycloak/themes/agentos/login/` | Mirrors these tokens so sign-in matches the shell. |

## Tokens (the contract)

Don't memorize values — reference these names. Full list in `app.css`; the families:

- **Surfaces**: `--bg`, `--bg-2`, `--bg-3`, `--bg-sunk`, plus role aliases `--surface-0..3` and the
  chrome surfaces `--bg-topbar/-titlebar/-toolbar/-statusbar/-sidebar`.
- **Borders**: `--line`, `--line-strong` (= `--border-subtle`, `--border-strong`).
- **Text**: `--txt`, `--txt-soft`, `--txt-dim`, `--txt-faint`, `--txt-on-accent` (= `--text-primary`
  … `--text-disabled`). Four levels of emphasis — pick by hierarchy, don't hand-pick greys.
- **Accent**: `--accent` `#3584e4` (dark `#62a0ea`) + `-hover` / `-active` / `-soft`. **One** accent;
  do not introduce a second brand hue.
- **State**: `--ok` `--warn` `--err` `--idle` (= `--state-success/-warning/-danger/-info`). Color
  = state only.
- **Radii**: `--r-1`(3) `--r-2`(5) `--r-3`(7) `--r-4`(9) `--r-5`(12). Small. Cards `--r-4`(9), pills 999.
- **Spacing**: 4-base — `--space-1`(4) … `--space-8`(48), plus `--s-1..10`. No raw px gaps.
- **Type**: `--font` (Inter), `--mono` (JetBrains Mono). Sizes `--fs-xs`(11) … `--fs-2xl`(28).
  Weights `--fw-regular/medium/semibold` (400/500/600 — never 700+ for UI text; 700 is reserved for
  the brand mark + tiny uppercase labels).
- **Elevation**: `--shadow-0..4` + `--shadow-window-focused/-unfocused` + `--shadow-inset`. Higher =
  more "lifted" (menus, dialogs, focused window). Don't stack ad-hoc box-shadows.
- **Motion**: `--duration-fast`(80ms) `-base`(160) `-slow`(240); `--ease-standard`, `-emphasized`.
  Hover/press = fast. Open/close = base. Never animate layout-affecting props on a timer.
- **Focus**: `--focus-ring`. Every interactive element shows it on `:focus-visible`. Non-negotiable.

## Components (reuse, don't reinvent)

| Component | Use for | Don't |
|---|---|---|
| `Btn` (Primary/Default/Ghost/Danger × Sm/Md) | Every button | Hand-roll a `<button class>`; raw buttons miss focus ring + states |
| `IconBtn` | Square icon-only action | Put a bare `<Icon>` in a clickable `<span>` |
| `Icon` | All iconography | Inline SVG, emoji, icon fonts |
| `Panel` / `.card` | Titled content block | Nest panels >1 deep |
| `Field` | Label + input + hint wrapper | A naked `<label><input>` |
| `Toggle` | Boolean setting | A checkbox for an on/off *preference* |
| `Dialog` | Modal confirm / form | A window for a yes/no question |
| `Dropdown` | Top-bar flyout (clock, user) | Reuse for in-page selects (use `<select class="prefs-select">`) |
| `ContextMenu` | Right-click menu | A dropdown anchored to the cursor |
| `Badge` / `Chip` / `Grade` | Status pills | Color-only status with no text/icon (a11y) |
| `Toast` (via `ToastService`) | Transient feedback | A dialog for "saved" |
| `Spinner` / `Progress` | Loading / determinate progress | A "Loading…" string |
| `AppFrame` + `WindowManagerService` | **Any new app** = a window | A full-page route; AgentOS apps live in windows |
| `ModelPicker` | Choosing an LLM model (priced, provider-filtered, `Custom…` for aliases) | A `<datalist>` input — it hides every option once the box has a value |
| `AgentMascot` (role × `MascotState`) | Showing *which agent* and *what it is doing* (Workflow nodes, Agent profiles) | Ad-hoc agent emoji/avatars; a mascot as the **only** status signal (keep the text/badge beside it) |

### Living icons

Every `Icon` carries `ic-<name>` and has its **own** micro-animation (gear spins, bell rings, trash
shakes, play nudges, house hops… — all 48, enforced by a unit test). It plays when the enclosing control
is hovered or keyboard-focused, when a toast arrives, or continuously with `<Icon Loop="true">` for a busy
state. Rules: declare a new icon's motion as `--ic-anim` (+ `--ic-origin`) in the "Living icons" block of
`app.css`; animate `transform`/`opacity` only; nothing loops at rest unless `Loop` says so; Dash tiles lift
on hover and squish on press. Same opt-outs as the mascots (`prefers-reduced-motion`, System → Appearance
→ *Animations*).

### Agent mascots

One cartoon character per role, defined once in `Components/UI/AgentMascot.razor` + the `.mascot` block
in `app.css`; the gallery of every role × state is on `/_/components`.

- **Roles**: Orchestrator (violet, baton), Requirement (amber, clipboard + pencil), Coding (blue,
  laptop), Testing (green, magnifier), QA (rose, shield stamp); anything else (plugins, custom agents)
  gets the slate antenna bot. An **Evaluator** node is drawn as QA — it runs as the QA agent.
- **States**: idle (still; hops + blinks when you point at it, its node or its row) · running (bob + prop works — the only state that loops) · done (hop, ^^ eyes, sparkles twice) ·
  failed (shake, X eyes, sweat) · canned (asleep, grey, zzz a few times — never the happy look on offline output) ·
  skipped (grey, faded). The face carries the state, so it reads with motion off.
- **Colors**: only `--mascot-*` tokens (illustration palette, identical in both schemes). Props that
  stick out past the body use mid-tones so they read on light **and** dark surfaces.
- **Performance**: nothing loops at rest (a test enforces it — idle loops inside glass windows cost 60 → 20 fps); animate `transform`/`opacity` only; no `filter` on or above animated content (greying
  is a palette swap); no `<defs>`/ids, so any number can share a page.
- **Motion off**: `prefers-reduced-motion`, or System → Appearance → *Animations* (per viewer,
  `<html data-agent-motion="off">`, restored by `theme.js` before Blazor mounts).

### Desktop shell anatomy (and who owns what)

- **TopBar** (`Components/UI/TopBar.razor`) — GNOME top panel: LEFT = an **Activities** button + the
  active-window title; CENTER = the **clock** (deep-links System → Date & time); RIGHT = theme toggle +
  a GNOME system menu (aggregated network/sound/battery + gateway health dot + avatar → quick settings).
  Dark in both themes. No brand mark, no standalone workspace item.
- **Dash (dock)** (`Taskbar.razor` in `AgentOs.Web.Shell/UI`) — flat translucent GNOME Dash, no Start
  button, no magnify. **Seven pinned apps**: Overview · Agents (5-agent pipeline) · Workflow · Board ·
  Settings (hub) · Terminal · Files. Running apps show a dot; the focused app's dot widens to an accent
  pill. Unpinned admin/system apps (Users, Evidence, Cost, Policy, Prompts, Plugins, MCP, System) are
  reached via the Settings hub + Activities search. Reads `AppCatalog` (respect `AdminOnly`).
- **Activities overview** (`AppShellLayout.razor`, `.ko-overview` — the old `.kickoff` selector) —
  full-screen launcher opened from the top-bar Activities button: a search field, a **virtual-workspace
  thumbnail strip** (each thumb previews its windows as app-colour dots, active outlined white), a
  **window exposé** of the active workspace, the pinned-app grid, and recents.
- **Virtual workspaces** (`WindowManagerService`: `WorkspaceCount` / `ActiveWorkspace` / `SwitchWorkspace`
  / `WindowsOn`) — N GNOME workspaces. A window belongs to a workspace; `WindowHost` renders only the
  active workspace's windows, and the TopBar title + dock pill reflect it. Switch from the Activities
  thumbnail strip.
- **Windows** (`AppFrame` ← `WindowHost` ← `WindowManagerService`) — one entry per open app; z-order
  on focus; CSD drag/resize/maximize; grab a maximized window's titlebar to restore it under the cursor.
- **Settings hub** (`SettingsHub.razor`, pinned "Settings") — one window collecting every admin/system
  surface behind a category rail (LLM & providers, Prompts, Tool policy, Cost, Evidence, Users, MCP,
  Plugins, **System**). Each category is also independently launchable + deep-linkable.
- **System category** (`SystemApp.razor`, the System tab inside the hub — unpinned standalone) — OS
  preferences: General, Appearance, **Date & time**, Notifications, About, Session. **Device/shell
  preferences live here**; admin governance lives in the hub's other categories. (The clock dropdown
  *deep-links* here via `WM.RequestLaunchTab("system","datetime")`.)

## Rules (the part that keeps it coherent)

1. **New feature = new app, in a window.** Register it in `AppCatalog`, render it in `WindowHost`,
   gate with `AdminOnly` if needed. Don't add top-level routes/pages.
2. **Settings live in the Settings hub.** Device/shell preferences go in the **System** category
   (`SystemApp`); admin/governance config goes in its own hub category — never scattered into a
   feature page or a top-bar dropdown. Surfaces may *deep-link* to the right category, but the control
   + persistence live in the hub.
3. **Tokens or nothing.** No raw hex, px spacing, or px radius in a component. If a needed value
   doesn't exist as a token, add the token first.
4. **One accent, color = state.** Blue accent for interactivity/selection; ok/warn/err for state.
   No decorative color. No second brand hue.
5. **Reuse the component vocabulary.** Before writing markup + CSS, check the table above. New
   shared widget → it goes in `Components/UI/` with tokens, not inline in a page.
6. **Icons through `Icon`.** One set, `currentColor`, sized via `Size=`. New glyph → add a case to
   `Icon.razor` + a row to `icon-map.md`.
7. **Every interactive element: keyboard + focus ring + a11y name.** `:focus-visible` shows
   `--focus-ring`; icon-only controls carry `aria-label`/`Title`; status never color-only.
8. **Motion is opt-out-able and non-blocking.** Use the motion tokens; honor
   `prefers-reduced-motion`; never gate an action behind an animation finishing.
9. **Light + dark, both wallpapers.** Test any new surface on `data-theme` light *and* dark, over a
   light *and* a dark wallpaper. Glass surfaces must stay legible on all four.
10. **KC login mirrors the shell.** A token change that affects sign-in chrome
    (accent, font, card, radius) updates `infra/keycloak/themes/agentos/login/` too.

## Adding a screen — the checklist

- [ ] Is it an **app**? Register in `AppCatalog` + `WindowHost`; don't add a route.
- [ ] Reuses `Btn`/`Field`/`Panel`/`Toggle`/`Dialog`/`Icon` — no hand-rolled equivalents.
- [ ] Only `var(--…)` tokens — zero raw hex/px.
- [ ] One accent; state colors only for state.
- [ ] Keyboard-reachable; `:focus-visible` ring on every control; a11y names on icon-only buttons.
- [ ] Legible in light+dark × light+dark wallpaper; glass stays readable.
- [ ] Settings (if any) live in the System app, deep-linked — not inline.
- [ ] Motion via tokens, `prefers-reduced-motion` respected.

## Known debt (don't widen it)

- Two card primitives exist (`.card` legacy, `.panel` preferred) — prefer `Panel`; don't add new
  `.card` usages.
