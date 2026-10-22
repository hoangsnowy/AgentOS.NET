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
pwsh infra/lab/lab.ps1 unpair   # stop it (disconnect it in Board → Computers to retire the token)
```

`pair` signs in to the lab as the seeded operator, registers a runner, sets **Force provider = RemoteAgent**
for the lab tenant and starts the runner in the background (log: `.lab/runner.log`). The pairing token goes
straight into the runner's environment — it is never printed or written to disk. Runs use your Claude
subscription through `claude -p` in prompt mode (no `--dangerously-skip-permissions`); a server-driven shell
command outside the runner allowlist is denied.

A Board session ("Work on this" → *Run on my machine*) needs more than prompt mode — the CLI has to clone,
edit, build and push, and in prompt mode every one of those tools needs an approval nobody can give
non-interactively. So that request (and only that one) asks the runner for **scoped autonomy**: each such CLI
run starts in its own empty directory under `%LOCALAPPDATA%\AgentOS.NET\runs` (deleted afterwards), file edits
are auto-approved only inside it (`--permission-mode acceptEdits`), and the shell is an allowlist of git /
gh pr / dotnet / npm / read-only commands with force-push, branch deletion, `git config`/`remote`,
`gh auth`/`api` and web access denied (`src/AgentOs.RemoteAgent/ScopedAutonomy.cs`). Pushes use the
machine's own git credentials. Every other request (spec generation, the 5-agent pipeline) stays
answer-only. It is still code from a ticket running on your box; the machine's owner can refuse scoped runs
with `REMOTE_AGENT_AUTONOMY=off`. With the runner paired, `RUN_AGENTOS_E2E_REAL=true AGENTOS_REAL_URL=https://lab.dev.localhost:6180 AGENTOS_E2E_RUN_TIMEOUT_SECONDS=1200 dotnet test --project tests/AgentOs.E2E.Tests --filter-class "*WorkflowAppRealAuthTests"` runs the full 5-agent pipeline live.

The runner keeps two lanes: work runs (scoped: clone, build, push — `REMOTE_AGENT_MAX_PARALLEL`, default 2) and
answer-only runs such as a New idea spec or ticket split (`REMOTE_AGENT_MAX_ANSWERS`, default 2), so a person waiting on
the screen never queues behind Autopilot's coding. Specialist answers get a 5-minute deadline (`LlmRequest.AgentAnswerTimeout`).

### Try the app on the lab

With the runner paired, Board → VNChessAcademy → **Try the app** on a repository runs its latest default branch on
this machine from `%LOCALAPPDATA%\AgentOS.NET\previews\<owner>-<repo>` (a reused checkout, so restores stay warm)
and shows *Open the app* once the recipe's url accepts connections. *Stop* kills the whole process tree; the runner
also stops its previews when it exits and kills leftovers from a hard kill on its next start (`preview.pid`).
Autopilot holds acceptance testing on that board while a preview runs and says so in the Autopilot log.

## Run the lab tests

```powershell
pwsh infra/lab/lab.ps1 test        # lab integration tests + the real-auth UI E2E, wired to the lab ports
```

Without `RUN_AGENTOS_LAB=true` (CI, other machines) every lab test skips with a precise reason. Each test
cleans up what it creates (labels deleted, created issues closed). What they cover today:

| Area | Checks |
|---|---|
| GitHub | token + repo validation, repo listing, README grounding for the Requirement agent, **repo agent instructions (the sandbox's `AGENTS.md`)**, live ticket body, idempotent label sync, Projects board read + ticket creation |
| Azure DevOps | PAT + repo validation, repo listing |
| Multi-tenant (real-auth UI) | a second tenant created through `/signup` sees none of tenant `default`'s boards, runners, evidence or runs, and Users lists only its own member; the tenant, its rows and its Keycloak user are removed afterwards |
| Real-auth UI | sign-in, Users/invitation email (MailHog), budget gate, Board runners, System → About (every service healthy) |

## Lab findings (gaps the lab exposed)

- **Azure DevOps boards are not implemented** — `AzureDevOpsSourceProvider` throws *not supported* for
  listing/validating/reading boards and creating work items, although the Board app offers Azure DevOps.
- **Azure DevOps repo context has no README/tree** — the Requirement agent isn't grounded on ADO repos.

Both are pinned as explicitly skipped lab tests until they're built.
