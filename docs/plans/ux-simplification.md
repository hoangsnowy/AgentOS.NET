# UX simplification — fewer places, task-first (2026-10-18)

The owner's call: AgentOS.NET has too many features on the surface. This plan, based on an audit of the
code and on GNOME/Apple/Microsoft guidance, NN/g research and the agent platforms users already know,
cuts the number of places users have to look. Every capability stays; fewer of them are on the first screen.
Every UI step still round-trips through Claude Design and is verified in light and dark mode.

## 1 — What the audit found

| # | Problem | Evidence |
|---|---|---|
| 1 | No task-first entry. A first live run takes about 7 clicks across 2 windows and 3 sub-tabs, from an empty desktop | `Desktop.razor`, `SettingsHub.razor` (admin lands on a read-only Status table) |
| 2 | Settings are 3 levels deep. Two of the 13 hub categories open a second rail (LLM: 5 tabs, System: 6 tabs), giving about 22 admin screens | `SettingsHub.razor:51-66`, `Settings.razor:32-36`, `SystemApp.razor:14-19` |
| 3 | One concept is set in several places: the provider in 3 (Force / Default / per agent); the QA cap in 2, where Workflow ignored the tenant cap; model keys split across 2 tabs; the GitHub token in 2 | `Settings.razor`, `AgentProfilesApp.razor`, `OrchestrationStudio.razor` |
| 4 | Settings can't be found. Search covers app names only. "All apps" lists only the 6 pinned apps, so 11 apps can be found only by typing. Ctrl+K is taken by Workflow ("Add node") | `AppShellLayout.razor`, `workflow-keys.js` |
| 5 | Mixed save models on one screen: a form-level Save next to per-field Save (about 23 buttons on Providers) | `Settings.razor:57-126` |
| 6 | Expert fields are shown up front (temperature, max output, pricing alias) | `AgentProfilesApp.razor` (21 inputs) |
| 7 | Names and icons collide: "API keys" vs "API tokens", "Data" vs "Storage", 4 × `lock` icon. Terminal and Files take pinned slots for low-frequency, read-only views | `AppCatalog.cs`, `SettingsHub.razor` |
| 8 | The design contract has drifted (7 pinned apps, "settings live in System"), and Rule 1, "new feature = new app", breeds sprawl | `docs/design/design-system.md` |

**Principles applied:**
- Show the few most important options. More than 2 disclosure levels tests badly (NN/g, progressive disclosure).
- Categories must be specific and must not overlap (NN/g).
- Preferences are pages and groups, and they are searchable (libadwaita `AdwPreferencesDialog`).
- Every preference has a price (Pennington); fewer than 5% of users change settings at all (Spool).
- Pair smart defaults with immediate effect (Microsoft app-settings guidelines).
- Empty states give direct pathways (NN/g); avoid deck-of-cards onboarding (NN/g).
- The competitor pattern: home is a prompt box plus a live runs list (GitHub Copilot agents panel, Cursor `/agents`, Devin). Ctrl/⌘K reaches pages, actions and settings (Linear, Devin).

## 2 — Target information architecture

**The Dash: 5 pinned apps.**

| App | Absorbs |
|---|---|
| **Home** (Insights renamed) | A composer ("What should the agents build?"), live runs grouped by status (Working / Waiting / Blocked / Done), a KPI strip, the setup checklist |
| **Board** | Boards, tickets, sessions. Terminal becomes a session **Log** tab; Files becomes a **Files** tab on a repo or session |
| **Workflow** | Unchanged (power users). Node caps **inherit** the tenant QA cap |
| **Insights** | Full analytics (cost, budget, latency). Pinned for admins only |
| **Settings** | Everything else |

**Settings: one rail, pages with groups, search at the top, 2 levels at most.**

```
Settings  [Search settings…]
├─ Get started        admin; until the checklist is done
├─ Models & keys      one card per provider (key / endpoint · default model · Test · status);
│                     routing = default provider, with "Force all agents" under Advanced
├─ Agents             model per agent + QA cap; temperature / max output / provider under Advanced
├─ Connections        Git hosts & tokens (pipeline + board tokens in one list) · paired machines · MCP · plugins
├─ Governance         tool policy · Activity log (Evidence + Audit log, filter by kind) · Prompts under Advanced
├─ People & access    members · API tokens
├─ Data               export · purge
├─ Personal           appearance · date & time · notifications · session (was System)
├─ About              version · service health · effective configuration (was LLM → Status / Storage)
└─ Platform           platform admin only
```

**One save model.** Choices and toggles apply immediately, with an undo toast. Secrets and URLs keep an
explicit Save on their card.

## 3 — Interaction patterns

1. **Global Ctrl/⌘K command palette.** It searches apps, every setting (deep-linking to the field), actions
   ("Run pipeline…", "New ticket", "Pair this machine", "Invite member") and recent runs. Modules and plugins
   contribute commands. Workflow's "Add node" becomes a contextual command that is listed first while
   Workflow has focus.
2. **Task-first Home:** a composer, live runs grouped by status, then KPIs.
3. **Setup checklist**, computed from real state, never a tutorial: add a model key and test it · connect a
   repo · (optional) pair a machine · first live run · set a budget · invite a member.
4. **Fix-it deep links.** Every reported gap (the offline chip, empty states, a failed Test) opens the exact
   settings page instead of telling the user where to go.
5. **Defaults plus Advanced disclosure**, with a "Modified" badge so a non-default value is never hidden.
6. **Role-based Dash and landing.** Members land on Home; admins also get Insights and the checklist;
   platform admins also get Platform.
7. **Clear labels:**
   - "API keys" → "Model keys"
   - "Storage" folded into About
   - one distinct icon per governance area
   - plain-language subtitles

## 4 — Phases

| Phase | Work | Status |
|---|---|---|
| **0 — Quick wins** | "All apps" lists every app, grouped · settings in Activities search · Workflow inherits the tenant QA cap (bug) · no "Anthropic" duplicate in the provider list · fix-it buttons · admin Settings lands on Providers · relabel and re-icon · unpin Terminal / Files · fix design-system drift and amend Rule 1 | In progress |
| **1 — Find & simplify** | ✅ Ctrl/⌘+K search with actions ([#218]) · global command palette with a command-source contract · ✅ flat Settings (one rail, pages + groups, rail search) ([#219]) · ✅ provider cards with per-provider Test ([#220]) · merged routing · Agents page with Advanced rows · one Activity log · one save model | Planned |
| **2 — Task-first** | Home composer + live runs + checklist · Board's "Start from idea" folded into the composer · Log / Files tabs in Board · role-based Dash | Planned |
| **3 — Validate** | About 5 users per persona (member developer, tenant admin) on scripted tasks | Ongoing |

**Rule going forward:** a new capability is a page in an existing app, a palette command or a settings group.
It gets its own app only when it is a daily primary task. The parity track
([competitive-parity.md](competitive-parity.md)) follows this rule: scheduled runs live on Home / Board, not in
a new app.

## 5 — Metrics

Metrics are tenant-scoped, and estimates are labelled.

| Metric | Today | Target |
|---|---|---|
| Clicks from login to a started live run (new admin) | ≈7, 2 windows | ≤3 |
| Places one concept is configured (provider · QA cap · keys · Git tokens) | 3 · 2 · 2 tabs · 2 | 1 each |
| Settings disclosure depth | 3 | 2 |
| Palette / settings-search zero-result rate | n/a | <10% |
| Checklist completed in the first session | n/a | ≥70% |
| Scripted settings tasks unaided ("add an OpenAI key", "who changed the budget?", "why is this run offline?") | measure | ≥90%, ≤30 s median |

## Sources

developer.gnome.org/hig/principles.html · developer.gnome.org/hig/patterns/nav/view-switchers.html ·
gnome.pages.gitlab.gnome.org/libadwaita/doc/main/class.PreferencesDialog.html · ometer.com/preferences.html ·
developer.apple.com/design/human-interface-guidelines/settings (secondary) ·
learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings ·
nngroup.com/articles/progressive-disclosure/ · nngroup.com/articles/flat-vs-deep-hierarchy/ ·
nngroup.com/articles/complex-application-design/ · nngroup.com/articles/flexibility-efficiency-heuristic/ ·
nngroup.com/articles/empty-state-interface-design/ · nngroup.com/articles/mobile-app-onboarding/ ·
archive.uie.com/brainsparks/2011/09/14/do-users-change-their-settings/ ·
github.blog/changelog/2025-08-19-agents-panel-launch-copilot-coding-agent-tasks-anywhere-on-github-com ·
cursor.com/help/ai-features/background-agents · docs.devin.ai/release-notes/2026 ·
docs.openhands.dev/openhands/usage/settings/llm-settings · docs.agno.com/agent-os/control-plane.md ·
linear.app/enablement/guide/navigating-linear
