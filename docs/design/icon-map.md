# Icon catalog

Every icon is one monochrome 24×24 stroke glyph in `src/AgentOs.Web.Shell/UI/Icon.razor`, drawn in
`currentColor` — use `<Icon Name="…" Size="16" />`, never an inline `<svg>` or emoji in a page. The
live gallery is on `/_/components`. Adding one: add a `case` to `Icon.razor` and a row here.

| Name | Meaning / where it's used |
|---|---|
| `house` | Home / Insights (landing dashboard) |
| `graph` | Workflow (the visual graph editor) |
| `git-pull-request` | Board, pull requests |
| `gear` | Settings, LLM & providers |
| `wrench` | System settings, configuration |
| `hammer` | Build / build verifier |
| `terminal` | Terminal, runner output |
| `folder` / `file` | Files app, repository tree |
| `user` | Users, account |
| `lock` | Locked / restricted state, sign-in |
| `key` | Model keys (LLM provider keys) |
| `shield` | Tool policy |
| `scroll` | Evidence (tool-call trail) |
| `server` | Platform settings |
| `chart` | Insights (analytics) |
| `diamond` | Agent profiles, agent nodes |
| `lightning` | Prompts, quick actions |
| `squares-stack` | Plugins |
| `squares-four` | Activities overview / app grid |
| `arrow-square-out` | External link, MCP servers |
| `palette` | Appearance |
| `clock` / `clock-counter` | Date & time / history, recent runs |
| `bell` | Notifications |
| `info` / `question` / `warning` | Information / help / warning states |
| `check` / `x` | Success, confirm / close, dismiss, failure |
| `play` / `pause` / `stop` | Run / pause / stop a run |
| `arrow-clockwise` | Refresh, retry |
| `arrow-uturn-left` / `arrow-uturn-right` | Undo / redo |
| `arrow-left` / `arrow-right` / `chevron-right` | Navigation, disclosure |
| `floppy-disk` | Save |
| `trash` | Delete |
| `search` | Search |
| `tag` | Labels, versions |
| `power` | Session / sign out |
| `moon` / `sun` | Dark / light theme toggle |
| `minus` / `square` | Window minimize / maximize |
| `circle-fill` | Status dot |
| `device-mobile` | Paired device / runner |
| `wifi` / `speaker-high` / `battery` | Top-bar system indicators (decorative) |
