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
   start time to the millisecond); the leftovers' creator pid no longer existed. The same labels sit on DCP's
   `aspire-session-network-*` networks (13 orphaned on the lab machine) and its data volumes are named per project path
   (19 `thuvienco-postgres-data-<hash>` on the lab machine, one per run folder).
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
     descendants via `ps`; one process snapshot per sample), so the cleanup knows the run's processes as pid + start
     time even after Stop killed them. Past 4 096 remembered processes only DCP is still recorded. The sampler is
     awaited (`DisposeAsync`) and the job handle is read under a lock, so no sample races the cleanup.
   - **Graceful first:** when one of the run's live processes is DCP, every *other* process of the run is killed
     (the AppHost among them); DCP notices its monitored AppHost is gone and removes its containers itself. Bounded
     wait (30 s).
   - **Force:** the job is closed. Off Windows the job also kills every sampled process still alive (a server
     re-parented to init).
   - **Docker** (only when the run had a DCP process): DCP labels its containers **and its `aspire-session-network-*`
     networks** with `com.microsoft.developer.usvc-dev.creatorProcessId` + `…creatorProcessStartTime`. A container or
     network is removed only when that creator's pid **and** start time (±1 s) match a process sampled in this run's
     job. The lab stack's and a *Try the app* preview's have a creator that was never in the run's job (a different
     AppHost, alive, not started from the run folder — previews run in their own named job), so they never match; a
     reused pid fails the start-time check. In order:
     1. `docker inspect` the run's containers for the **named volumes** they mount;
     2. `docker rm -f -v` the containers — persistent-lifetime ones too (the run created them); `-v` removes only their
        anonymous volumes, never a named one;
     3. `docker network rm` the run's networks — Docker's default address pools hold ~31 bridge networks, so orphaned
        session networks end in every AppHost on the computer (the lab's included) failing to start (13 were found);
     4. `docker volume rm` the named volumes from step 1 **created at or after the run started** — the AppHost names a
        volume after its project path, and every run folder is a new path, so a run's volume is never reused (19
        orphaned `thuvienco-postgres-data-<hash>` were found); an older volume a run merely mounted stays.
   - `REMOTE_AGENT_KEEP_CONTAINERS=1` keeps containers, networks and volumes (no graceful stop, no removal);
     `REMOTE_AGENT_KEEP_VOLUMES=1` keeps volumes only (`docker rm -f` without `-v`). Processes always end. Every action
     is logged on the runner console (names only, control characters stripped).
2. **A PreToolUse guard confines writes** (`ScopedWriteGuard.cs`). Every scoped claude run gets `--settings <file>`
   (written per run to the runner's temp folder, outside the checkout) installing `AgentOs.RemoteAgent guard` as a
   PreToolUse hook for `Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit`. The CLI's environment carries the run folder
   (`AGENTOS_RUN_DIR`), the run's own temp folder (`AGENTOS_RUN_TMP` — `<run>.tmp` beside the run folder, which must
   start empty for the clone; also the CLI's `TEMP` / `TMP` / `TMPDIR`, so tools' temp files go with the run) and a
   ledger (`AGENTOS_GUARD_LEDGER`). The guard:
   - tokenises the command (quotes, heredocs, here-strings, redirections incl. `>&file` and `<>`, Bash and PowerShell
     rules), recurses into `$(…)` / backticks (a heredoc commit message or PR body is text, not commands),
     `sh -c` / `bash -lc` / `-c --`, `pwsh -Command` / `-EncodedCommand` (decoded) / `-File`, `cmd /c`, `iex`,
     Start-Process, `find -exec`, `xargs`, `npm exec` / `npx` / `pnpm dlx` / `yarn dlx|exec|node`;
   - follows `cd` (also `cd -`, and `cd "$(git rev-parse --show-toplevel)"`) from the session's `cwd`, expands the
     variables it can vouch for (`$TEMP` / `$TMP` / `$TMPDIR` / `$env:TEMP`, `$AGENTOS_RUN_DIR`, `$PWD`, `$HOME`; Git
     Bash's `/tmp` on Windows = the run's temp), resolves every write target (`~`, Git Bash `/c/…`, `..`, attached
     short values like `tar -C../x`, `curl -so../x`, `dotnet -o:../x`, MSBuild `-p:OutputPath=…`) and blocks (exit 2,
     reason to the agent) when one is outside the run folder and its temp folder: file tools' paths, redirections,
     file-writing commands' operands, `cd` out, anything run from outside, git `-C`/`--git-dir`/`--work-tree`/
     `worktree add`/`clone <dest>`/`init`/`archive -o`/`format-patch -o`/`bundle create`, `gh repo clone <dest>`,
     package/build/download output options, global installs and `npm pkg set scripts.*`; a computed path it can't
     follow is refused;
   - lets **code run only when the run could not have slipped it in unseen**: a script given to an interpreter (node,
     deno, bun, tsx, ts-node, python, sh/bash, `source`/`.`, pwsh, `dotnet run x.cs` / `--file`, `dotnet fsi`,
     `dotnet exec`, a program named by its path like `./x.sh`, `NODE_OPTIONS=--require …`, `node -r/--import`) must be a
     file the repository **tracks** (committed, or tracked and changed — either way it ships in the pull request's diff)
     or a **gitignored build output** (dist/, bin/, node_modules/) that the run did not write itself (the ledger records
     every write target of every allowed call). A **new untracked script** is refused with *"Commit it first
     (`git add <file> && git commit -m "…"`) — it then ships in the pull request — and run it again."* Inline code
     (`node -e`, `python -c`, a heredoc or pipe into an interpreter) is refused. This is what closes the `../base-122`
     path: the incident script was a new untracked file.
   - refuses writes inside the run folder to `.git/` internals, `.husky/` hooks and `.claude/settings*.json` (a hook or
     a permission a follow-up run resumed in the folder would load);
   - fails closed on unexpected input (exit 2). Verified live with `claude -p` 2.1.283 on Windows: the hook runs, the
     model receives the block reason. Note that Claude Code itself already refuses shell redirections to `/tmp` or
     `$TEMP` in these runs (*"needs approval … outside the working directories"*), so agents write logs inside the run
     folder; the run's temp folder serves the tools' own temp files.
3. **Defence in depth — the runs root is swept** (`RunsRootWatch`): an entry that appeared next to the run folders
   while runs were in flight and is not one of the runner's own names (`yyyyMMdd-HHmmss-<id>`, its `.parked` marker, its
   `.tmp` folder) is removed when the last of those runs ends, and logged. Under a custom `REMOTE_AGENT_WORKROOT`
   (which may be shared) it is only reported. Run temp folders a crashed runner left are removed at the next start.

## Consequences

- A Testing run that starts the project's AppHost no longer leaves its containers, its session network or its data
  volume behind; the lab's own stack and previews are untouched (unit tests with a fake docker CLI and a fake process
  tree prove both directions, including the case where Stop killed DCP before the cleanup).
- A Stop or the end of a run may take up to ~30 s longer when the run left an Aspire app running (DCP stopping its
  containers); runs without DCP never call Docker.
- The guard costs one short process start per shell / file-tool call (~80 ms; ~130 ms when it asks git about a script).
- **The hook is a guard rail, not a sandbox — it fails open.** Claude Code blocks a tool call only on exit 2: if the
  hook can't run (the runner's executable replaced or removed mid-run by an update, a crash, its 30 s timeout) or hooks
  are switched off (`disableAllHooks`, `allowManagedHooksOnly` in managed settings), the call proceeds.
- **Residual:** code the repository itself runs (an npm script, an MSBuild target, a test — including a test or a
  build script the agent wrote and committed) still has the member's rights; a script the agent commits *and then* runs
  passes the tracked-file check (it is on the pushed branch, visible in the pull request); `npx -y <package>` runs a
  downloaded package (the scoped allow rules only let `npx playwright` through). The sweep removes what such code
  leaves beside the run folders, not elsewhere. The real boundary for that is an OS sandbox: enable the Claude Code
  sandbox on macOS / Linux runners once its network allowlist covers clone / restore / push, and a Windows equivalent
  (AppContainer / a restricted token) — tracked under GAP-07.
