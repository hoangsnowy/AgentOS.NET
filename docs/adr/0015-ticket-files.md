# ADR-0015 — Ticket files: the only way files reach an agent

- **Status:** Accepted · 2026-10-25
- **Context source:** VNChess ticket #108 failed — the agent was asked to vendor a set of design skills and could not
  fetch them; owner: "a person has no way to hand the agent files it must use".

## Context

An agent working a ticket on a member's computer runs `claude -p` (or Codex) in a fresh per-run directory under
**scoped autonomy** (`ScopedAutonomy.cs`): edits only inside that directory, an allowlist of build / test / VCS
commands, and `WebFetch` / `WebSearch` denied. That policy is deliberate — an agent that can download can also be told
(by a ticket, a README, a dependency) to download and run anything. But it left no way at all to give the agent a
logo, art assets, a sample data file or a third-party folder it must copy into the repository. Server-driven runs
(`runner_shell`) had the same gap.

## Decision

1. **A person attaches files to a ticket; AgentOS.NET stores them; every run of the ticket finds them in its working
   folder.** Downloading from a URL stays unsupported — on purpose: the person, not the agent, decides what crosses in.
2. **Storage — generic, tenant-scoped.** `sessions.ticket_files` (Sessions module, which owns Board tasks): `Id`,
   `TenantId`, `ScopeKey`, `Path`, `Content` (bytea), `Size`, `Sha256`, `ContentType`, `UploadedBy`, `UploadedAtUtc`;
   unique `(TenantId, ScopeKey, Path)` — a re-upload replaces. Contract `IScopedFileStore` (Put / List / Get / LoadAll /
   Delete / DeleteScope, every call tenant-explicit) so the next consumer (design-gate screenshots) reuses it with its
   own scope. Scopes (`TicketFileScope`): `ticket:{board}:{n}`, `session:{id}` (a task without a number),
   `draft:{id}` (a New idea before its tickets exist — copied onto every ticket created from it). Tenant isolation:
   EF filter + explicit predicates + RLS (`RowLevelSecurity.Tables`). Without a database (standalone Development) an
   in-memory store keeps the feature working, bounded to 512 MB.
3. **Rules in one place.** `TicketFilePath` (Domain, BCL-only, linked into the runner): relative, `/`-separated, no
   `.` / `..`, no leading `/` or `~`, no `:` (drive letters, NTFS streams), no `< > " | ? *` or control characters, no
   Windows device names, no segment ending in a dot or space, ≤ 200 characters. `ITicketFiles` applies it, the
   tenant's **limits** (UI settings, ADR-0011: `TicketFiles:MaxFileMegabytes` 10, `MaxTotalMegabytes` 50, `MaxFiles`
   500 — Settings › Autopilot), refuses names that differ only in capitals, and records SHA-256. Any file type is
   allowed; nothing ever executes one (REST downloads are always `application/octet-stream` attachments).
4. **Delivery — pulled by the runner, before the CLI starts.** The dispatch (`RemoteExecRequest.Files`) carries only a
   manifest (path, size, SHA-256); the bytes wait in the broker (`StageFiles`), fetchable only by the connection the
   run was dispatched to, in 256 KB pieces (`ReadRunFile` hub method — no SignalR message-size surprises). The runner
   (`RunFiles.cs`) re-checks every path with the same rules, resolves it under `<run dir>/.agentos/files/` and refuses
   anything that would land outside, never overwrites, verifies size + SHA-256, caps a run at 512 MB, and fails the run
   **before the CLI starts** on any refusal. A file's bytes are released as soon as it was fetched.
   Server-driven runs (`runner_shell`): `IRunnerFileDelivery` sends one `write_files` tool call ahead of the loop (same
   approval gate as every runner tool call); the runner writes into a fresh `<work root>/<date>-files-<id>/` folder
   (cleaned after 3 days) and answers with its path.
5. **The prompt says where, and that they are data.** Coding (both IssueWork templates), Reviewer and Testing get one
   section — "Files attached to this ticket" — only when there are files **and** they reach the agent's computer: the
   list (fenced as untrusted text — the names come from a person), where they are, that nothing may be downloaded, to
   clone into a subfolder (the run folder is no longer empty), and how to copy them (`cp ../.agentos/files/…`, the one
   copy rule added to scoped autonomy). Tickets can say "the files attached to this ticket".
6. **Evidence.** Every delivery is an evidence entry (`ticket_files`, run id, session, each file's path / size /
   SHA-256, how many the runner fetched to the end) and one line on the task's feed. Not an LLM call — Insights is
   unaffected.
7. **Surfaces.** Desktop: *Files for the AI* (`TicketFilesPanel`) on Board › Tasks (per task), the *Work on this*
   dialog, and both New idea steps — pick files, drop them, or add a folder (`webkitdirectory`, relative paths kept);
   list with path / size / uploader; remove. REST: `GET|PUT|DELETE /workspaces/{boardId}/tickets/{n}/files[/{**path}]`
   (Member policy, tenant from the token; upload is a raw-body `PUT`, so no form CSRF on the cookie host).

## Consequences

- An agent gets exactly what a person attached, checked twice (server and runner) and recorded — and still nothing
  from the internet.
- Files live in Postgres rows. Fine at these sizes (bounded per ticket); a blob store would be a later swap behind
  `IScopedFileStore`.
- A runner older than this change ignores the manifest: the run proceeds without the files and the evidence says
  "Delivered 0 of N" — update the helper. The Thorough (server pipeline) engine does not use ticket files (its feed
  says so).
- Not done: expanding an uploaded `.zip` (a folder upload covers the case without zip-slip risk); a cleanup of New idea
  drafts abandoned without *Start another* (they stay as tenant data until deleted); concurrent uploads to one ticket
  may overshoot a limit by one file (the check and the write are not one transaction).
