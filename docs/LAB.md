# AgentOS.NET lab — a test server for real integrations

The dev stack proves the app runs; the **lab** proves it works against the real world: a real GitHub repo
and Projects board, a real Azure DevOps project, and (optionally) a real LLM with a hard budget cap. It is a
second, long-running full stack on this machine that never collides with the dev stack.

| | Dev stack | Lab stack |
|---|---|---|
| Start | `dotnet run --project infra/AgentOs.AppHost` | `pwsh infra/lab/lab.ps1 up` |
| Desktop | https://localhost:5180 | **https://lab.dev.localhost:6180** |
| Api / Scalar | https://localhost:5080 | https://localhost:6080 |
| Keycloak / MailHog | https://localhost:8080 / :8025 | https://labkc.dev.localhost:9080 / :9025 |
| Aspire dashboard | https://localhost:17050 | https://localhost:18050 |
| Code | your working copy | its own git worktree `..\AgentOs-lab` (rebuilding dev never disturbs it) |
| Data | Aspire default volumes | `agentos-lab-postgres-data`, `agentos-lab-keycloak-data` |

Login on both: `operator` / `operator` (realm `agentic`).

> **Why `*.dev.localhost`?** Cookies are scoped to the host, not the port. On plain `localhost` the lab would
> share one cookie jar with the dev stack, its Keycloak and every other local app — sessions overwrite each
> other and the jar overflows (*HTTP 431*, *"Correlation failed"* at `/signin-oidc`). `lab.dev.localhost` and
> `labkc.dev.localhost` resolve to loopback in every modern browser and in .NET with no hosts-file edit, and
> the ASP.NET dev certificate already covers `*.dev.localhost`, so the lab gets its own cookie jars. Each
> instance also uses its own cookie names (`Auth:CookiePrefix`), and a sign-in callback that can't be
> completed restarts the login once instead of failing.
>
> The **dev** stack still lives on `localhost` with everything else; if its sign-in ever reports *HTTP 431*,
> clear `localhost` site data (or use a private window).

## Lab commands

```powershell
pwsh infra/lab/lab.ps1 up                      # create the worktree if needed, build, start, wait for healthy
pwsh infra/lab/lab.ps1 update                  # move to the latest origin/main, rebuild, restart
pwsh infra/lab/lab.ps1 update -Ref origin/feat/x   # try a branch on the lab
pwsh infra/lab/lab.ps1 status | logs | down
pwsh infra/lab/lab.ps1 reset                   # wipe lab data (asks first)
```

### The lab outlives the shell that started it

`up` / `update` start the AppHost, and `pair` starts the runner, **outside the launching shell's job object**
(`infra/lab/start-detached.ps1`), so closing that terminal — or ending the Claude Code session whose tool ran
`lab.ps1` — leaves the lab and its runner running. Only `down` / `unpair` (or `update` / `pair`, which restart them)
stop them.

*Symptom this fixes — "the lab died with the session":* the desktop reports `down` in `lab.ps1 status`, the lab
log just stops (no error, no shutdown lines), `.lab\apphost.pid` / `.lab\runner.pid` point at processes that no
longer exist, and Autopilot quietly makes no progress — all from about the moment the session that last ran
`lab.ps1 up | update | pair` ended. A child started with `Start-Process` or node's `spawn({ detached: true })` stays in
its parent's Windows job object, and a session's tool shell runs in a job that is killed when the session ends; the
AppHost (with its Aspire processes) and the runner went down with it. Recovery for an older checkout:
`lab.ps1 up` then `lab.ps1 pair` from a terminal you keep open.

How it works (Windows): the process is created through WMI (`Win32_Process.Create`), so its parent is `WmiPrvSE`
and it is in none of the caller's jobs. A WMI-created process does not inherit the caller's environment, so the
caller's whole environment block is handed over in that same call — in memory only: the runner's pairing token in
it never appears on a command line or in a file. Output redirection lives in a generated launcher,
`.lab\<name>.launch.cmd` (working directory, command, log paths — no environment values), run by a hidden `cmd.exe`;
the pid files hold the real `dotnet` / `AgentOs.RemoteAgent` pid (the launcher's child), so `status`, `down`,
`update`, `pair`, `unpair` and `reset` work as before and `down` still kills the AppHost's whole process tree. Without
WMI (not Windows) `lab.ps1` falls back to `Start-Process` and `pair` to node's detached spawn, which do not survive a
job that kills its processes.

## One-time setup (you)

Run the `dotnet user-secrets` commands **in your own terminal** — not in the AI chat (not even with `!`),
so tokens never land in a transcript. The lab tests read a dedicated store, `agentos-lab-tests`.

### 1. GitHub (sandbox repo `hoangsnowy/agentos-lab`, already created)

1. Create a Projects board: https://github.com/users/hoangsnowy/projects → *New project* → name it
   "AgentOS.NET Lab" → add issues #1 and #2 from `hoangsnowy/agentos-lab`. Note its number (the URL ends in `/projects/<n>`).
2. Create a token: https://github.com/settings/tokens → *Generate new token (classic)*, expiry 30 days,
   scopes **`repo`** and **`project`** only. (Fine-grained tokens can't reach user-owned Projects boards yet.)
3. Store it:
   ```powershell
   dotnet user-secrets set "Lab:GitHub:Token" "<token>" --id agentos-lab-tests
   dotnet user-secrets set "Lab:GitHub:ProjectNumber" "<n>" --id agentos-lab-tests
   ```

### 2. Azure DevOps

1. https://dev.azure.com → create an organization (e.g. `agentos-lab`) and a project **AgentOS.NET Lab**.
   In *Repos*, initialise the default repository **with a README** (an empty repo has no default branch).
2. Create a PAT (User settings → Personal access tokens), expiry 30 days, scopes: **Code — Read & write**,
   **Work Items — Read & write**, **Project and Team — Read**.
3. Store it:
   ```powershell
   dotnet user-secrets set "Lab:AzureDevOps:Org" "agentos-lab" --id agentos-lab-tests
   dotnet user-secrets set "Lab:AzureDevOps:Project" "AgentOS.NET Lab" --id agentos-lab-tests
   dotnet user-secrets set "Lab:AzureDevOps:Token" "<pat>" --id agentos-lab-tests
   ```

### 2b. Jira Cloud (ticket tracker, E1.8)

1. A free Jira Cloud site (https://www.atlassian.com/software/jira/free) with a Scrum or Kanban project, e.g. key
   **LAB**; the lab account is its administrator.
2. An API token for that account: https://id.atlassian.com/manage-profile/security/api-tokens.
3. Store it (the Azure DevOps PAT above also needs **Identity — Read** for the Boards maintainer roles):
   ```powershell
   dotnet user-secrets set "Lab:Jira:Site" "https://<you>.atlassian.net" --id agentos-lab-tests
   dotnet user-secrets set "Lab:Jira:Project" "LAB" --id agentos-lab-tests
   dotnet user-secrets set "Lab:Jira:Email" "<account e-mail>" --id agentos-lab-tests
   dotnet user-secrets set "Lab:Jira:Token" "<api token>" --id agentos-lab-tests
   # optional: work items in another ADO project than the repo's
   dotnet user-secrets set "Lab:AzureDevOps:BoardsProject" "<project>" --id agentos-lab-tests
   ```

### 3. A real LLM (optional, capped)

```powershell
dotnet user-secrets set "Lab:Claude:ApiKey" "sk-ant-..." --project infra/AgentOs.AppHost
pwsh infra/lab/lab.ps1 update
```

The key is seeded into the **lab** stack only (the dev stack ignores `Lab:*`). Then sign in to
https://lab.dev.localhost:6180 → **Insights → Budget**: set a monthly cap (e.g. `5`) and turn **Enforce** on, so
the lab can never overspend.

## Real agent runs without an API key

```powershell
pwsh infra/lab/lab.ps1 pair     # pair THIS machine as a runner; lab agents run on your local `claude -p`
pwsh infra/lab/lab.ps1 pair -Data "CHESS_LIBRARY_PATH=D:\Chess"   # also lets the agents read test data (read-only env vars)
pwsh infra/lab/lab.ps1 unpair   # stop it and its Try-the-app previews (disconnect it in Board → Computers to retire the token)
```

`pair` signs in to the lab as the seeded operator, registers a runner, sets **Force provider = RemoteAgent**
for the lab tenant and starts the runner in the background, outside the shell's job so it keeps running when the
session ends (log: `.lab/runner.log`, see [above](#the-lab-outlives-the-shell-that-started-it)). The pairing token
goes straight into the runner's environment — it is never printed or written to disk. Runs use your Claude
subscription through `claude -p` in prompt mode (no `--dangerously-skip-permissions`); a server-driven shell
command outside the runner allowlist is denied.

A Board session ("Work on this" → *Run on my machine*) needs more than prompt mode — the CLI has to clone,
edit, build and push, and in prompt mode every one of those tools needs an approval nobody can give
non-interactively. So that request (and only that one) asks the runner for **scoped autonomy**: each such CLI
run starts in its own empty directory under `%LOCALAPPDATA%\AgentOS.NET\runs` (deleted afterwards), file edits
are auto-approved only inside it (`--permission-mode acceptEdits`), and the shell is an allowlist of git /
gh pr / dotnet / npm / read-only commands with force-push, branch deletion, `git config`/`remote`,
`gh auth`/`api` and web access denied (`src/AgentOs.RemoteAgent/ScopedAutonomy.cs`). A PreToolUse hook
(`AgentOs.RemoteAgent guard`, `ScopedWriteGuard.cs`) refuses any shell command or file edit that would write outside
the run folder (`cd ..`, `git worktree add ../x`, `> ../out.txt`) and running a new script the agent wrote before it is
committed (*"Commit it first … and run it again"* — committed or edited scripts of the repository and gitignored build
outputs run as before); the agent gets the reason, and the call shows as *refused* in Settings › Activity log › On a
member's computer. The hook fails open (Claude Code runs the call when the hook can't run), so it is a guard rail, not a
sandbox. Each run's `TEMP`/`TMP`/`TMPDIR` is its own `runs\<run>.tmp`, removed with it. When a run ends (ok, failed, stopped, timed out) the runner
lets the run's Aspire orchestrator (DCP) stop its containers, kills what is left, and removes the containers and
`aspire-session-network-*` networks whose DCP creator was one of **that run's** processes (`docker rm -f -v`,
`docker network rm`), then the named volumes those containers mounted that were created during the run (each run folder
gives the AppHost a new volume name) — the lab stack's own Postgres/Keycloak/MailHog, its network and volumes and a
*Try the app* preview's are never touched; it also removes anything a run created next to the run folders. The console
logs each step (`[agent] <id>: removed 4 container(s) …`, `removed 1 network(s) …`, `removed 1 volume(s) …`).
Orphaned session networks matter: Docker's default pools hold ~31 bridge networks, then every AppHost start fails.
`REMOTE_AGENT_KEEP_CONTAINERS=1` keeps a run's containers, networks and volumes, `REMOTE_AGENT_KEEP_VOLUMES=1` its volumes ([ADR-0018](adr/0018-runs-leave-nothing-behind.md)). Leftovers from runs before this change (old
`aspire-container-network-tunnelproxy-*` containers, `aspire-session-network-*` networks, `thuvienco-*-data-*` volumes,
`runs\base-122`, `runs\trx84`) are not attributable to a run and stay until you remove them. Pushes use the machine's own git credentials. Every other request (spec generation, the 5-agent pipeline) stays
answer-only. It is still code from a ticket running on your box; the machine's owner can refuse scoped runs
with `REMOTE_AGENT_AUTONOMY=off`. With the runner paired, `RUN_AGENTOS_E2E_REAL=true AGENTOS_REAL_URL=https://lab.dev.localhost:6180 AGENTOS_E2E_RUN_TIMEOUT_SECONDS=1200 dotnet test --project tests/AgentOs.E2E.Tests --filter-class "*WorkflowAppRealAuthTests"` runs the full 5-agent pipeline live.

The runner keeps two lanes: work runs (scoped: clone, build, push — `REMOTE_AGENT_MAX_PARALLEL`, default 2) and
answer-only runs such as a New idea spec or ticket split (`REMOTE_AGENT_MAX_ANSWERS`, default 2), so a person waiting on
the screen never queues behind Autopilot's coding. Specialist answers get the *Answer time limit* deadline
(`Agents:AnswerTimeoutMinutes`, default 15 min, Settings › Agent profiles). A profile model claude can't serve (e.g.
`gpt-4.1`) runs as `REMOTE_AGENT_CLAUDE_FALLBACK_MODEL` (default `sonnet`; empty = the CLI's own default) instead of
silently becoming the machine's default model.

### Try the app on the lab

With the runner paired, Board → VNChessAcademy → **Try the app** on a repository runs its latest default branch on
this machine from `%LOCALAPPDATA%\AgentOS.NET\previews\<owner>-<repo>` (a reused checkout, so restores stay warm)
and shows *Open the app* once the recipe's url accepts connections. The app runs outside the runner's process tree
(on Windows the runner starts `cmd.exe` suspended inside a named job that the app's own processes keep alive; on Linux
its own session via `setsid`; output in `previews\running\<id>.log`, rotated at 10 MB with one older part kept), so
`lab.ps1 update`, `lab.ps1 pair` or a runner crash no longer kills it: the state file next to the log (pid, process
start time, port, recipe, board/repo) lets the next runner re-attach it — only if that pid is still the same process
(start time, and still the `cmd`/`sh` it started) — and announce it, and the Board card keeps showing *running* with
the same URL. The server rebuilds a card it forgot (a Web restart) only for a repo of that board in the tenant's own
workspace store and a `localhost` url; anything else is stopped. A preview whose process is gone is reported stopped;
a state file the runner can't use has its app stopped (only when it is still the preview's own process). While the
runner is away the server keeps the card for 15 minutes, then says the app may still be running. *Stop* kills the whole
process tree (also after a restart; pressed while the runner is away, it is kept for 7 days and applied when the runner
is back); a preview nobody has had open for `Preview:IdleMinutes` (Settings › Autopilot, default 120) stops by itself.
`lab.ps1 unpair` and `lab.ps1 down` stop the previews too (`AgentOs.RemoteAgent stop-previews`). To check by hand:
start a preview, `lab.ps1 update` (or restart the runner), and the card stays *Ready* with the same *Open the app*;
*Stop* still closes it.
Autopilot holds acceptance testing on that board while a preview runs and says so in the Autopilot log — unless the
project is listed in Settings › Autopilot › *Test while trying the app* (only when the app keeps its data separately
for each checkout).
The test data the runner was paired with (`-Data`, e.g. `CHESS_LIBRARY_PATH`) reaches the app too, so a project that loads
real data when it is there shows it.

## Run the lab tests

```powershell
pwsh infra/lab/lab.ps1 test        # lab integration tests + the real-auth UI E2E, wired to the lab ports (Web, Api :6080,
                                   # Keycloak, MailHog, Postgres) with a 20-min live-run budget for the Workflow test
```

Without `RUN_AGENTOS_LAB=true` (CI, other machines) every lab test skips with a precise reason. Each test
cleans up what it creates (labels deleted, created issues closed). What they cover today:

| Area | Checks |
|---|---|
| GitHub | token + repo validation, repo listing, README grounding for the Requirement agent, **repo agent instructions (the sandbox's `AGENTS.md`)**, live ticket body, **the ticket's discussion (a maintainer comment kept with its authority, an AgentOS.NET-signed one left out; both deleted afterwards)**, idempotent label sync, Projects board read + ticket creation |
| Azure DevOps | PAT + repo validation, repo listing; **Boards**: project + WIQL validation, a created work item read back with its comment (maintainer role), moved to Active, linked to a PR, then destroyed |
| Jira | project + JQL validation, a created issue read back with its comment (maintainer role), transitioned, remote-linked to a PR, then deleted |
| Multi-tenant (real-auth UI) | a second tenant created through `/signup` sees none of tenant `default`'s boards, runners, evidence or runs, and Users lists only its own member; the tenant, its rows and its Keycloak user are removed afterwards |
| Real-auth UI | sign-in, Users/invitation email (MailHog), budget gate, Board runners, System → About (every service healthy) |

## Lab findings (gaps the lab exposed)

- **Azure DevOps repo context has no README/tree** — the Requirement agent isn't grounded on ADO repos.

It is pinned as an explicitly skipped lab test until it's built. (Azure DevOps Boards landed with E1.8 — docs/INTEGRATIONS.md.)
