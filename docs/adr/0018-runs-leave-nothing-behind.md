# ADR-0018 — A run on a member's computer leaves nothing behind

- **Status:** Accepted · 2026-11-02
- **Context source:** lab dogfood — the Testing agent accepting VNChess #122 reported *"TaskStop could not Ctrl+C the
  AppHost and `docker stop` needed approval, so this run's containers postgres-…, keycloak-…, mailhog-… and
  aspire-container-network-tunnelproxy-… are still running. A main-snapshot folder ../base-122 was also left outside
  the run directory."*

## Context

A Board run on a member's computer (`claude -p` under scoped autonomy, `ScopedAutonomy.cs`) runs in its own folder
under `%LOCALAPPDATA%\AgentOS.NET\runs` and inside a Windows Job Object with kill-on-close (`ProcessJob.cs`). Two things
still escaped it:

1. **Containers.** The Testing agent started the project's Aspire AppHost (`dotnet run --project infra/X.AppHost`).
   Aspire's orchestrator DCP (`dcp start-apiserver --monitor <AppHost pid>` → `dcp run-controllers`) asks Docker to run
   Postgres, Keycloak, MailHog and a tunnel proxy. When the run ended, the job killed the AppHost **and** DCP in the
   same instant — DCP removes its containers when its AppHost exits, but it was dead too — and Docker, not the run's
   process tree, runs the containers. Inspection: every such container carries
   `com.microsoft.developer.usvc-dev.creatorProcessId` + `…creatorProcessStartTime` (the `dcp run-controllers` process,
   start time to the millisecond); the leftovers' creator pid no longer existed.
2. **Writes outside the run folder.** `acceptEdits` auto-approves the CLI's edits inside its working directory, and
   Bash rules are prefix matches. The agent's own transcript shows: `git worktree add ../base` → needs approval
   (denied); `mkdir -p ../base-122 && git archive … | tar -x -C ../base-122` → denied; then it **wrote
   `.tester-base.mjs` into its run folder** (auto-approved) and ran `node ./.tester-base.mjs` (allowed by
   `Bash(node ./*)`, meant for the repository's own scripts), which extracted main into `../base-122`. Earlier runs had
   also left `trx84/` and `validate-out.json` beside the run folders.

The Claude Code sandbox (`sandbox.enabled`) confines child processes at the OS level, but it is macOS / Linux / WSL2
only — the lab and most members run native Windows — and it restricts the network by default, which an issue run
(clone, restore, push) needs.

## Decision

1. **End-of-run cleanup, on every outcome** (ok, failed, stopped, timed out; CLI runs and server-driven `shell` calls),
   before the run folder is deleted (`RunCleanup.EndAsync`):
   - The job is **sampled** every 3 s (Windows: `QueryInformationJobObject` process list; elsewhere: the root's
     descendants via `ps`), so the cleanup knows the run's processes as pid + start time even after Stop killed them.
   - **Graceful first:** when one of the run's live processes is DCP, every *other* process of the run is killed
     (the AppHost among them); DCP notices its monitored AppHost is gone and removes its containers itself. Bounded
     wait (30 s).
   - **Force:** the job is closed. Off Windows the job also kills every sampled process still alive (a server
     re-parented to init).
   - **Containers:** only when the run had a DCP process, `docker ps -a --filter label=…creatorProcessId` lists DCP's
     containers; a container is removed (`docker rm -f`, never `-v` — **named volumes stay**, data separation is the
     project's business) only when its creator pid **and** start time (±1 s) match a process sampled in this run's
     job. The lab stack's and a *Try the app* preview's containers have a creator that was never in the run's job (a
     different AppHost, alive, not started from the run folder — previews run in their own named job), so they never
     match; a reused pid fails the start-time check.
   - `REMOTE_AGENT_KEEP_CONTAINERS=1` keeps containers (no graceful stop, no removal); processes still end. Every
     action is logged on the runner console (container names only, control characters stripped).
2. **A PreToolUse guard confines writes** (`ScopedWriteGuard.cs`). Every scoped claude run gets `--settings <file>`
   (written per run to the temp folder, outside the checkout) installing `AgentOs.RemoteAgent guard` as a PreToolUse
   hook for `Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit`; the run folder travels in `AGENTOS_RUN_DIR`. The guard
   tokenises the command (quotes, heredocs, `$(…)`, redirections; Bash and PowerShell rules), follows `cd` within it
   from the session's `cwd`, resolves every write target (`~`, Git Bash `/c/…`, `..`) and blocks (exit 2, reason to the
   agent) when one is outside the run folder: file tools' paths, redirections, file-writing commands' operands, `cd` out,
   anything run from outside, git `-C`/`--git-dir`/`--work-tree`/`worktree add`/`clone <dest>`/`init`/`archive -o`/
   `format-patch -o`/`bundle create`, `gh repo clone <dest>`, package/build/download output options and global installs.
   Scripts run by an interpreter must be committed, unchanged files of the repository (`git status --porcelain
   --ignored` lists nothing for them) — a helper the agent wrote is refused, as is inline code (`node -e`). Inside the
   run folder, `.git/` internals and `.claude/settings*.json` are refused too (a hook or a permission a follow-up run
   resumed in the folder would load). Unexpected input fails closed. Verified live: `claude -p` 2.1.283 on Windows runs
   the hook and reports the block to the model.
3. **Defence in depth — the runs root is swept** (`RunsRootWatch`): an entry that appeared next to the run folders
   while runs were in flight and is not one of the runner's own names (`yyyyMMdd-HHmmss-<id>`, its `.parked` marker) is
   removed when the last of those runs ends, and logged. Under a custom `REMOTE_AGENT_WORKROOT` (which may be shared)
   it is only reported.

## Consequences

- A Testing run that starts the project's AppHost no longer leaves its containers running; the lab's own stack and
  previews are untouched (unit tests with a fake docker CLI and a fake process tree prove both directions, including
  the case where Stop killed DCP before the cleanup).
- A Stop or the end of a run may take up to ~30 s longer when the run left an Aspire app running (DCP stopping its
  containers); runs without DCP never call Docker.
- The guard costs one short process start per shell / file-tool call.
- **Residual:** code the repository itself runs (an npm script, an MSBuild target, a test) still has the member's
  rights; a script the agent commits *and then* runs passes the clean-file check (it is on the pushed branch, visible
  in the pull request). The sweep removes what such code leaves beside the run folders, not elsewhere. The real
  boundary for that is an OS sandbox: enable the Claude Code sandbox on macOS / Linux runners once its network
  allowlist covers clone / restore / push, and a Windows equivalent (AppContainer / a restricted token) — tracked under
  GAP-07.
