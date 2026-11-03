# AgentOS.NET Runner (VS Code)

Pair this machine to an AgentOS.NET server and run AI coding sessions locally — a one-click sign-in instead of copying tokens into a terminal.

## Use

1. Run **AgentOS.NET: Connect this machine** from the Command Palette.
2. Approve the pairing in the browser (log in if prompted).
3. The status bar shows **AgentOS.NET: Connected** — this machine is now a runner; AgentOS.NET dispatches sessions to it.

The extension downloads the AgentOS.NET runner binary from your server and runs it for you — nothing to install by hand, no env vars, no token copy-paste.

## API token

Call the AgentOS.NET Api with a personal API token (create one in the desktop: **Settings → API tokens**; the `run`
scope lets you start runs):

1. **AgentOS.NET: Set API token** — paste the `aos_…` token. It is checked against `<apiUrl>/v1/tenants/me` and kept
   in VS Code secret storage (never in `settings.json`).
2. **AgentOS.NET: Run the pipeline on a user story** — sends the selection (or what you type) to `POST /v1/pipeline`;
   the result lands in the output panel.
3. **AgentOS.NET: Check API token** shows the workspace and scopes; **Clear API token** removes it from this machine
   (revoke it in the desktop too).

## Settings

- `agentos.serverUrl` — AgentOS.NET server base URL (default `https://localhost:5180`).
- `agentos.apiUrl` — AgentOS.NET Api base URL for API-token calls (default `https://localhost:5080`).
- `agentos.cliAgent` — the CLI agent the runner invokes: `claude` (default) or `codex`. Uses your own subscription, not metered API; the server can override it per session.
- `agentos.runOnMyMachine` — run sessions autonomously with that CLI (appends `claude --dangerously-skip-permissions` / `codex --full-auto`; the CLI must be installed and logged in). Off by default.

## Commands

- **AgentOS.NET: Connect this machine** — pair and start the runner.
- **AgentOS.NET: Disconnect** — stop the runner and clear the stored token (revoke it server-side from the AgentOS.NET desktop).
- **AgentOS.NET: Show runner logs** — open the runner output channel.

## Notes

- Ships the Windows (win-x64) runner binary for now; macOS/Linux are planned.
- On `localhost` the server's development TLS certificate is accepted automatically.

## Develop

```bash
npm install
npm run build          # esbuild -> dist/extension.js
npm run package        # -> agentos-runner.vsix
code --install-extension agentos-runner.vsix
```
