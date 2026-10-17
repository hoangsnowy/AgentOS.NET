# AgentOS lab — a test server for real integrations

The dev stack proves the app runs; the **lab** proves it works against the real world: a real GitHub repo
and Projects board, a real Azure DevOps project, and (optionally) a real LLM with a hard budget cap. It is a
second, long-running full stack on this machine that never collides with the dev stack.

| | Dev stack | Lab stack |
|---|---|---|
| Start | `dotnet run --project infra/AgentOs.AppHost` | `pwsh infra/lab/lab.ps1 up` |
| Desktop | https://localhost:5180 | **https://localhost:6180** |
| Api / Scalar | https://localhost:5080 | https://localhost:6080 |
| Keycloak / MailHog | :8080 / :8025 | :9080 / :9025 |
| Aspire dashboard | https://localhost:17050 | https://localhost:18050 |
| Code | your working copy | its own git worktree `..\AgentOs-lab` (rebuilding dev never disturbs it) |
| Data | Aspire default volumes | `agentos-lab-postgres-data`, `agentos-lab-keycloak-data` |

Login on both: `operator` / `operator` (realm `agentic`).

> **Use a separate browser profile (or an incognito window) for the lab.** Cookies are scoped to the host,
> not the port, so the dev and lab Keycloaks (and every other app on `localhost`) share one cookie jar; once
> it overflows, sign-in fails with **HTTP 431** on both stacks. A fresh profile/incognito window fixes it
> instantly; clearing `localhost` site data also works.

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
   "AgentOS Lab" → add issues #1 and #2 from `hoangsnowy/agentos-lab`. Note its number (the URL ends in `/projects/<n>`).
2. Create a token: https://github.com/settings/tokens → *Generate new token (classic)*, expiry 30 days,
   scopes **`repo`** and **`project`** only. (Fine-grained tokens can't reach user-owned Projects boards yet.)
3. Store it:
   ```powershell
   dotnet user-secrets set "Lab:GitHub:Token" "<token>" --id agentos-lab-tests
   dotnet user-secrets set "Lab:GitHub:ProjectNumber" "<n>" --id agentos-lab-tests
   ```

### 2. Azure DevOps

1. https://dev.azure.com → create an organization (e.g. `agentos-lab`) and a project **AgentOS Lab**.
   In *Repos*, initialise the default repository **with a README** (an empty repo has no default branch).
2. Create a PAT (User settings → Personal access tokens), expiry 30 days, scopes: **Code — Read & write**,
   **Work Items — Read & write**, **Project and Team — Read**.
3. Store it:
   ```powershell
   dotnet user-secrets set "Lab:AzureDevOps:Org" "agentos-lab" --id agentos-lab-tests
   dotnet user-secrets set "Lab:AzureDevOps:Project" "AgentOS Lab" --id agentos-lab-tests
   dotnet user-secrets set "Lab:AzureDevOps:Token" "<pat>" --id agentos-lab-tests
   ```

### 3. A real LLM (optional, capped)

```powershell
dotnet user-secrets set "Lab:Claude:ApiKey" "sk-ant-..." --project infra/AgentOs.AppHost
pwsh infra/lab/lab.ps1 update
```

The key is seeded into the **lab** stack only (the dev stack ignores `Lab:*`). Then sign in to
https://localhost:6180 → **Insights → Budget**: set a monthly cap (e.g. `5`) and turn **Enforce** on, so
the lab can never overspend.

## Run the lab tests

```powershell
pwsh infra/lab/lab.ps1 test        # lab integration tests + the real-auth UI E2E, wired to the lab ports
```

Without `RUN_AGENTOS_LAB=true` (CI, other machines) every lab test skips with a precise reason. Each test
cleans up what it creates (labels deleted, created issues closed). What they cover today:

| Area | Checks |
|---|---|
| GitHub | token + repo validation, repo listing, README grounding for the Requirement agent, live ticket body, idempotent label sync, Projects board read + ticket creation |
| Azure DevOps | PAT + repo validation, repo listing |

## Lab findings (gaps the lab exposed)

- **Azure DevOps boards are not implemented** — `AzureDevOpsSourceProvider` throws *not supported* for
  listing/validating/reading boards and creating work items, although the Board app offers Azure DevOps.
- **Azure DevOps repo context has no README/tree** — the Requirement agent isn't grounded on ADO repos.

Both are pinned as explicitly skipped lab tests until they're built.
