// AgentOS.NET Runner — VS Code extension. Pairs this machine to an AgentOS.NET server with a "sign in" flow and
// runs the AgentOS.NET runner locally. It does NOT reimplement the runner: it downloads the self-contained
// AgentOs.RemoteAgent binary from the server and spawns it as a child process. The runner's whole config
// is env vars (REMOTE_AGENT_HUB|ID|TOKEN|ARGS), so wrapping it is a thin shell.
//
// Pairing: Connect -> open <server>/pair/vscode in the browser -> the member approves -> the server
// redirects to vscode://agentos.runner/paired?code=... -> we exchange the one-time code (token never
// rides the URL) for {runnerId, token, hubUrl} -> store in SecretStorage -> download + spawn the runner.
//
// API token (E1.3): "Set API token" stores a personal API token (aos_..., created in Settings > API tokens) in
// SecretStorage — never in settings.json — and checks it against <apiUrl>/v1/tenants/me. "Run the pipeline" then calls
// POST <apiUrl>/v1/pipeline with it (the token needs the "run" scope).

import * as vscode from 'vscode';
import * as https from 'node:https';
import * as http from 'node:http';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { spawn, ChildProcess } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { URL, URLSearchParams } from 'node:url';

const SECRET_KEY = 'agentos.runner.credentials';
const API_TOKEN_KEY = 'agentos.apiToken';
const API_TOKEN_PREFIX = 'aos_';
const CALLBACK = 'vscode://agentos.runner/paired';

interface Creds {
  runnerId: string;
  token: string;
  hubUrl: string;
}

let status: vscode.StatusBarItem;
let output: vscode.OutputChannel;
let child: ChildProcess | undefined;
let pendingState: string | undefined;
let stopping = false;

export function activate(context: vscode.ExtensionContext): void {
  output = vscode.window.createOutputChannel('AgentOS.NET Runner');
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  context.subscriptions.push(output, status);
  setStatus('disconnected');

  context.subscriptions.push(
    vscode.commands.registerCommand('agentos.connect', () => connect()),
    vscode.commands.registerCommand('agentos.disconnect', () => disconnect(context)),
    vscode.commands.registerCommand('agentos.showLogs', () => output.show()),
    vscode.commands.registerCommand('agentos.setApiToken', () => setApiToken(context)),
    vscode.commands.registerCommand('agentos.checkApiToken', () => checkApiToken(context)),
    vscode.commands.registerCommand('agentos.clearApiToken', () => clearApiToken(context)),
    vscode.commands.registerCommand('agentos.runPipeline', () => runPipeline(context)),
    vscode.window.registerUriHandler({ handleUri: (uri) => onUri(context, uri) }),
  );

  // Resume an existing pairing on startup so the runner reconnects without a click.
  void context.secrets.get(SECRET_KEY).then((raw) => {
    if (raw) {
      void startRunner(context, JSON.parse(raw) as Creds);
    }
  });
}

export function deactivate(): void {
  stopRunner();
}

function serverUrl(): string {
  const cfg = vscode.workspace.getConfiguration('agentos').get<string>('serverUrl');
  return (cfg || 'https://localhost:5180').replace(/\/+$/, '');
}

function apiUrl(): string {
  const cfg = vscode.workspace.getConfiguration('agentos').get<string>('apiUrl');
  return (cfg || 'https://localhost:5080').replace(/\/+$/, '');
}

interface TenantMe {
  tenantId: string;
  userName?: string;
  authMethod?: string;
  scopes?: string[];
}

async function setApiToken(context: vscode.ExtensionContext): Promise<void> {
  const token = await vscode.window.showInputBox({
    title: 'AgentOS.NET API token',
    prompt: 'Paste a personal API token (Settings > API tokens). It is kept in VS Code secret storage.',
    password: true,
    ignoreFocusOut: true,
    validateInput: (v) => (v.trim().startsWith(API_TOKEN_PREFIX) ? undefined : `An API token starts with ${API_TOKEN_PREFIX}`),
  });
  if (!token) {
    return;
  }
  try {
    const me = (await requestJson('GET', `${apiUrl()}/v1/tenants/me`, undefined, token.trim())) as TenantMe;
    await context.secrets.store(API_TOKEN_KEY, token.trim());
    void vscode.window.showInformationMessage(`AgentOS.NET: token saved — ${describe(me)}.`);
  } catch (e) {
    void vscode.window.showErrorMessage(`AgentOS.NET: the Api refused this token — ${errMsg(e)}`);
  }
}

async function checkApiToken(context: vscode.ExtensionContext): Promise<void> {
  const token = await context.secrets.get(API_TOKEN_KEY);
  if (!token) {
    void vscode.window.showWarningMessage('AgentOS.NET: no API token — run "AgentOS.NET: Set API token".');
    return;
  }
  try {
    const me = (await requestJson('GET', `${apiUrl()}/v1/tenants/me`, undefined, token)) as TenantMe;
    void vscode.window.showInformationMessage(`AgentOS.NET: ${describe(me)}.`);
  } catch (e) {
    void vscode.window.showErrorMessage(`AgentOS.NET: token check failed (revoked or expired?) — ${errMsg(e)}`);
  }
}

async function clearApiToken(context: vscode.ExtensionContext): Promise<void> {
  await context.secrets.delete(API_TOKEN_KEY);
  void vscode.window.showInformationMessage('AgentOS.NET: API token removed from this machine (revoke it in Settings > API tokens).');
}

async function runPipeline(context: vscode.ExtensionContext): Promise<void> {
  const token = await context.secrets.get(API_TOKEN_KEY);
  if (!token) {
    void vscode.window.showWarningMessage('AgentOS.NET: no API token — run "AgentOS.NET: Set API token".');
    return;
  }
  const editor = vscode.window.activeTextEditor;
  const selected = editor && !editor.selection.isEmpty ? editor.document.getText(editor.selection) : '';
  const description = await vscode.window.showInputBox({
    title: 'AgentOS.NET: run the pipeline',
    prompt: 'The user story to turn into requirements, code and tests',
    value: selected,
    ignoreFocusOut: true,
    validateInput: (v) => (v.trim() ? undefined : 'Describe the user story'),
  });
  if (!description) {
    return;
  }
  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: 'AgentOS.NET: running the pipeline…' },
    async () => {
      try {
        const result = (await requestJson('POST', `${apiUrl()}/v1/pipeline`, { description: description.trim() }, token)) as {
          status?: string;
          iterations?: number;
        };
        output.appendLine(JSON.stringify(result, null, 2));
        output.show(true);
        void vscode.window.showInformationMessage(`AgentOS.NET: pipeline ${result.status ?? 'finished'} — details in the output panel.`);
      } catch (e) {
        void vscode.window.showErrorMessage(`AgentOS.NET: pipeline failed — ${errMsg(e)}`);
      }
    },
  );
}

function describe(me: TenantMe): string {
  const scopes = me.scopes && me.scopes.length ? me.scopes.join(' ') : 'n/a';
  return `${me.userName ?? 'unknown'} in workspace ${me.tenantId} (scopes: ${scopes})`;
}

async function connect(): Promise<void> {
  pendingState = randomUUID();
  const url = `${serverUrl()}/pair/vscode?callback=${encodeURIComponent(CALLBACK)}&state=${pendingState}`;
  setStatus('pairing');
  await vscode.env.openExternal(vscode.Uri.parse(url));
  void vscode.window.showInformationMessage('AgentOS.NET: approve the pairing in your browser to connect this machine.');
}

async function onUri(context: vscode.ExtensionContext, uri: vscode.Uri): Promise<void> {
  const q = new URLSearchParams(uri.query);
  const code = q.get('code');
  const state = q.get('state');
  if (!code) {
    return;
  }
  if (!pendingState || state !== pendingState) {
    void vscode.window.showErrorMessage('AgentOS.NET: pairing state mismatch — run "AgentOS.NET: Connect" again.');
    return;
  }
  pendingState = undefined;

  try {
    const creds = (await requestJson('POST', `${serverUrl()}/runner/pair/exchange`, { code })) as Creds;
    await context.secrets.store(SECRET_KEY, JSON.stringify(creds));
    await startRunner(context, creds);
    void vscode.window.showInformationMessage('AgentOS.NET: connected — this machine is now a runner.');
  } catch (e) {
    setStatus('disconnected');
    void vscode.window.showErrorMessage(`AgentOS.NET pairing failed: ${errMsg(e)}`);
  }
}

async function startRunner(context: vscode.ExtensionContext, creds: Creds): Promise<void> {
  stopRunner();
  stopping = false;

  let exe: string;
  try {
    exe = await ensureRunnerBinary(context);
  } catch (e) {
    setStatus('disconnected');
    void vscode.window.showErrorMessage(`AgentOS.NET: could not fetch the runner binary — ${errMsg(e)}`);
    return;
  }

  const cfg = vscode.workspace.getConfiguration('agentos');
  const onMyMachine = cfg.get<boolean>('runOnMyMachine') ?? false;
  const cliAgent = cfg.get<string>('cliAgent') ?? 'claude';
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    REMOTE_AGENT_HUB: creds.hubUrl,
    REMOTE_AGENT_ID: creds.runnerId,
    REMOTE_AGENT_TOKEN: creds.token,
    // Default CLI profile; the server can still override it per session. The runner builds the right
    // flags per profile and appends the autonomous flags only when YOLO is set (the run-on-my-machine opt-in).
    REMOTE_AGENT_CLI: cliAgent,
    REMOTE_AGENT_YOLO: onMyMachine ? '1' : '',
  };

  output.appendLine(`[agentos] starting runner: ${exe}`);
  child = spawn(exe, [], { env });
  child.stdout?.on('data', (d: Buffer) => output.append(d.toString()));
  child.stderr?.on('data', (d: Buffer) => output.append(d.toString()));
  child.on('exit', (exitCode) => {
    output.appendLine(`[agentos] runner exited (code ${exitCode}).`);
    child = undefined;
    setStatus('disconnected');
    if (!stopping) {
      // Unexpected exit — reconnect after a short backoff using the stored credentials.
      setTimeout(() => {
        void context.secrets.get(SECRET_KEY).then((raw) => {
          if (raw) {
            void startRunner(context, JSON.parse(raw) as Creds);
          }
        });
      }, 3000);
    }
  });
  setStatus('connected');
}

function stopRunner(): void {
  stopping = true;
  if (child) {
    child.kill();
    child = undefined;
  }
}

async function disconnect(context: vscode.ExtensionContext): Promise<void> {
  stopRunner();
  await context.secrets.delete(SECRET_KEY);
  setStatus('disconnected');
  void vscode.window.showInformationMessage(
    'AgentOS.NET: disconnected. Revoke the runner in the AgentOS.NET Runners tab to remove it server-side.',
  );
}

// Map this machine to a runner RID the server publishes (scripts/build-runner.ps1 builds these).
function runnerRid(): string {
  const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
  if (process.platform === 'win32') {
    return 'win-x64';
  }
  if (process.platform === 'darwin') {
    return `osx-${arch}`;
  }
  return 'linux-x64';
}

// Build token for a RID's published binary, or null when offline / not built (then keep what we have).
async function fetchRunnerToken(rid: string): Promise<string | null> {
  try {
    const t = await requestJson('GET', `${serverUrl()}/runner/version?rid=${rid}`);
    return typeof t === 'string' ? t.trim() : null;
  } catch {
    return null;
  }
}

// Download + cache the self-contained runner exe under the extension's global storage, picking the
// binary for this OS/arch. Auto-updates: a side-car .runner-meta.json records the RID + server build
// token; on (re)start we re-download when the binary is missing, the RID changed, or the server
// reports a newer token. The check runs before spawn (no child is running), so replacing the file is
// safe; if the version probe fails (offline), the existing binary is kept.
async function ensureRunnerBinary(context: vscode.ExtensionContext): Promise<string> {
  const dir = context.globalStorageUri.fsPath;
  fs.mkdirSync(dir, { recursive: true });
  const isWin = process.platform === 'win32';
  const exe = path.join(dir, isWin ? 'agentos-runner.exe' : 'agentos-runner');
  const metaPath = path.join(dir, '.runner-meta.json');
  const rid = runnerRid();

  const have = fs.existsSync(exe);
  let stored: { rid?: string; token?: string } | undefined;
  if (have && fs.existsSync(metaPath)) {
    try {
      stored = JSON.parse(fs.readFileSync(metaPath, 'utf8')) as { rid?: string; token?: string };
    } catch {
      stored = undefined;
    }
  }

  let needsDownload = !have;
  if (have) {
    const serverToken = await fetchRunnerToken(rid);
    if (serverToken && (stored?.rid !== rid || stored?.token !== serverToken)) {
      output.appendLine('[agentos] runner update available — refreshing binary…');
      needsDownload = true;
    }
  }

  if (needsDownload) {
    output.appendLine(`[agentos] downloading runner binary (${rid})…`);
    await download(`${serverUrl()}/runner/download?rid=${rid}`, exe);
    if (!isWin) {
      fs.chmodSync(exe, 0o755);
    }
    const token = await fetchRunnerToken(rid);
    fs.writeFileSync(metaPath, JSON.stringify({ rid, token }));
  }
  return exe;
}

// ── tiny HTTP helpers (accept the dev cert on localhost only) ─────────────────────────────────────

function isLocal(urlStr: string): boolean {
  try {
    const h = new URL(urlStr).hostname;
    return h === 'localhost' || h === '127.0.0.1' || h === '::1';
  } catch {
    return false;
  }
}

function requestJson(method: string, urlStr: string, body?: unknown, bearer?: string): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const u = new URL(urlStr);
    const lib = u.protocol === 'https:' ? https : http;
    const data = body !== undefined ? Buffer.from(JSON.stringify(body)) : undefined;
    const req = lib.request(
      u,
      {
        method,
        rejectUnauthorized: !isLocal(urlStr),
        headers: {
          'content-type': 'application/json',
          ...(data ? { 'content-length': String(data.length) } : {}),
          ...(bearer ? { authorization: `Bearer ${bearer}` } : {}),
        },
      },
      (res) => {
        const chunks: Buffer[] = [];
        res.on('data', (c: Buffer) => chunks.push(c));
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString();
          const sc = res.statusCode ?? 500;
          if (sc >= 400) {
            reject(new Error(`${sc}: ${text}`));
            return;
          }
          try {
            resolve(text ? JSON.parse(text) : {});
          } catch {
            resolve(text);
          }
        });
      },
    );
    req.on('error', reject);
    if (data) {
      req.write(data);
    }
    req.end();
  });
}

function download(urlStr: string, dest: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const u = new URL(urlStr);
    const lib = u.protocol === 'https:' ? https : http;
    const req = lib.get(u, { rejectUnauthorized: !isLocal(urlStr) }, (res) => {
      const sc = res.statusCode ?? 500;
      if (sc >= 400) {
        reject(new Error(`download failed: ${sc}`));
        return;
      }
      const file = fs.createWriteStream(dest);
      res.pipe(file);
      file.on('finish', () => file.close(() => resolve()));
      file.on('error', reject);
    });
    req.on('error', reject);
  });
}

function setStatus(state: 'connected' | 'disconnected' | 'pairing'): void {
  const text: Record<typeof state, string> = {
    connected: '$(check) AgentOS.NET: Connected',
    disconnected: '$(plug) AgentOS.NET: Disconnected',
    pairing: '$(sync~spin) AgentOS.NET: Pairing…',
  };
  status.text = text[state];
  status.tooltip = state === 'connected' ? 'Click to disconnect this runner' : 'Click to pair this machine';
  status.command = state === 'connected' ? 'agentos.disconnect' : 'agentos.connect';
  status.show();
}

function errMsg(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
