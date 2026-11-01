# ADR-0017 — Tool calls made on a member's computer are evidence

- **Status:** Accepted · 2026-11-01
- **Context source:** governance audit — "every tool call made through the server is evidence, but a Board run on a
  member's computer leaves none".

## Context

Every tool call the server makes goes through `IToolGateway` → `IToolPolicy` → `IToolInvocationLog`, so Settings ›
Activity log › Tool calls shows each side effect, allowed or refused. A Board "Run on my machine" task is different:
the server sends one prompt (`Execute`) to the member's paired computer, and the CLI there (`claude -p` under scoped
autonomy — `ScopedAutonomy.cs`) calls its **own** tools: `Bash`, `Edit`, `Write`, `Read`… The gateway never sees
them, the decision to allow or refuse each one is the CLI's scoped permissions on that machine, and nothing was
recorded. The comment in `IRemoteAgentBroker.cs` that claimed identical governance described only the server-driven
`runner_shell` path (M4).

## Decision

1. **The runner reports every tool call; the server records each one as evidence.** The CLI already runs with
   `--output-format stream-json --verbose` (live progress). `CliProgress.Read` now reads, in the same single pass per
   line, every `tool_use` (id, tool, input), every `tool_result` (`is_error`; a refusal is recognised by the CLI's
   own text — *"Claude requested permissions to use …"*, *"Permission to use … has been denied"*) and the closing
   result's `permission_denials`. `CliToolCalls` folds them into finished calls: tool, summary, ok / error / refused,
   start, duration. A call that never got a result (Stop, crash) closes as an error; past 2 000 calls a run reports
   the rest as one counted row.
2. **Only a bounded, redacted summary leaves the computer.** The summary is the Bash command, the edited file's path,
   the search pattern — never `Write`'s content, `Edit`'s strings, or any tool output; an unknown (MCP) tool is
   summarised by its input's field *names*. `SecretRedactor` (Domain, BCL-only, **linked into the runner** like
   `TicketFilePath`) keeps the first line only (a heredoc body is file content), redacts credential shapes
   (Authorization / API-key / cookie headers, Bearer/Basic values, `sk-…`/`ghp_`/`github_pat_`/GitLab/Slack/AWS/Google/npm
   tokens, JWTs, `https://user:pass@`, `Password=…;`-style key/values, `--password x`/`--token x`, `curl -u user:pass`,
   `-p`/`-P`/`-a` on credential-taking clients such as `mysql`, `sshpass`, `sqlcmd`, `redis-cli`, `docker login`,
   private-key blocks), bounds it to 500 characters, and fails closed (a regex timeout redacts everything). The server
   applies the same function again on receipt.
3. **Wire: batches during the run.** `ReportToolCalls(requestId, permissions, calls)` on the existing hub — every 25
   calls or 5 s, and once more before `CompleteRequest`. `permissions` says what decided (`scoped`, `yolo`,
   `custom`, `prompt`) and becomes the row's `PolicySource`.
4. **The broker accepts a batch only for a run it dispatched, from the connection it went to.**
   `RemoteAgentLlmClient` calls `TrackRun(requestId, tenant, session, agent role)` before dispatching; the broker binds
   the run to the connection and runner at dispatch. A batch from any other connection, for an unknown run, or more
   than 2 minutes after the run ended (completed, Stop, timeout) is ignored — the grace window exists because Stop
   kills the CLI first and its last batch follows. At most 200 calls per batch and 2 001 per run are accepted; ids,
   tool names, times and durations are bounded, and a refusal is never "ok".
5. **One evidence row per call**, through `IToolInvocationLog` (buffered, best-effort, never fails the run):
   `CallId` = the CLI's tool-use id, `RunId` = the dispatch id, `SessionId` = the Board task, `TenantId` = the run's,
   plus new columns on `tools.tool_invocations` — `Origin` (`member-computer`; null = the server), `AgentRole`,
   `RunnerId`, `PolicySource` (`cli-scoped-permissions`, `cli-skip-permissions`, `cli-custom-args`,
   `cli-default-permissions`; null = the server's `IToolPolicy`) and `Denied`. Migration `MemberComputerEvidence` adds
   them and an index `(TenantId, Origin, StartedUtc)`. The table was already under RLS (ADR-0012); every read stays
   tenant-explicit.
6. **Desktop.** Settings › Activity log › Tool calls has a filter **On a member's computer** (Where / What / Agent /
   status ok · error · refused). A Board task's activity panel links to its own calls (admins), and the run publishes
   one feed line with its totals. Insights shows **Tool calls on members' computers**: runs, calls and refusals per
   agent role, and per run — bars plus a table.

## Consequences

- An admin can answer "what did the agent do on that computer, and what did its permissions stop?" per task.
- The evidence is as trustworthy as the paired runner: it is self-reported by software on the member's machine. It
  is bound to the dispatch (connection, tenant, tracked run), redacted twice and capped, but a modified runner could
  under-report. It complements, and does not replace, the CLI's scoped permissions on that computer.
- Codex runs (no stream-json on the runner yet) and operator-customised `REMOTE_AGENT_ARGS` runs report no tool calls;
  a runner older than this change reports none either — the server simply records nothing for them.
- Server-driven `runner_shell` calls (M4) are unchanged: they still go through `IToolGateway` before dispatch.
