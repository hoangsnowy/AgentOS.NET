// Lab: pair THIS machine as a RemoteAgent runner and route the lab tenant's agents to it — real agent runs on
// the local `claude -p` (your Claude subscription), no API key on the server. Invoked by `lab.ps1 pair`.
//
//   1. signs in to the lab desktop as the seeded operator (lab-only test credentials, local .localhost host);
//   2. Board → Runners → registers a runner, reads the one-time pairing token from the readout;
//   3. Settings → LLM & providers → Providers → Force provider = RemoteAgent;
//   4. starts the runner DETACHED with the token in its environment only — the token is never printed or
//      written to disk; its console output goes to .lab/runner.log, its pid to .lab/runner.pid.
// The runner runs in prompt→answer mode (no --dangerously-skip-permissions): agents get model output; any
// server-driven shell command outside the allowlist is denied (no console to approve).
//
// Env: PW_PACKAGE (Playwright package dir), LAB_URL, RUNNER_EXE, STATE_DIR.

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const pw = require(process.env.PW_PACKAGE);

const labUrl = process.env.LAB_URL || 'https://lab.dev.localhost:6180';
const runnerExe = process.env.RUNNER_EXE;
const stateDir = process.env.STATE_DIR;

(async () => {
  if (!runnerExe || !fs.existsSync(runnerExe)) { throw new Error(`runner not found: ${runnerExe}`); }
  const browser = await pw.chromium.launch();
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } })).newPage();
  let id, token;
  try {
    await page.goto(labUrl + '/');
    await page.locator('#username').fill('operator');
    await page.locator('#password').fill('operator');
    await page.locator('#kc-login, button[type=submit]').first().click();
    await page.locator('.dock').waitFor({ timeout: 30000 });

    // Register a runner.
    await page.locator('.dock-item[title="Board"]').first().click();
    let win = page.locator('.appwin.focused');
    await win.locator('.board-tab', { hasText: 'Runners' }).click();
    await win.getByPlaceholder("Hoang's laptop").fill(`lab · ${require('os').hostname()} · claude -p`);
    await win.getByRole('button', { name: 'Register runner' }).click();
    const readout = await win.locator('.cred-readout textarea').inputValue({ timeout: 15000 });
    id = /REMOTE_AGENT_ID=(\S+)/.exec(readout)?.[1];
    token = /REMOTE_AGENT_TOKEN=(\S+)/.exec(readout)?.[1];
    if (!id || !token) { throw new Error('could not read the pairing readout'); }

    // Route every agent of the operator's tenant to the paired runner.
    await page.locator('.dock-item[title="Settings"]').first().click();
    win = page.locator('.appwin.focused');
    await win.locator('.prefs-cat', { hasText: 'LLM & providers' }).first().click();
    await win.locator('.prefs-cat', { hasText: 'Providers' }).last().click();
    await win.locator('select.prefs-select').first().selectOption('RemoteAgent');
    await win.getByRole('button', { name: 'Save', exact: true }).first().click();
    await page.waitForTimeout(1500);
  } finally {
    await browser.close();
  }

  fs.mkdirSync(stateDir, { recursive: true });
  const log = fs.openSync(path.join(stateDir, 'runner.log'), 'a');
  const child = spawn(runnerExe, [], {
    env: { ...process.env, REMOTE_AGENT_HUB: labUrl + '/hubs/remote-agent', REMOTE_AGENT_ID: id, REMOTE_AGENT_TOKEN: token, REMOTE_AGENT_CLI: 'claude' },
    detached: true,
    stdio: ['ignore', log, log],
    windowsHide: true,
  });
  child.unref();
  fs.writeFileSync(path.join(stateDir, 'runner.pid'), String(child.pid));
  console.log(`Paired runner ${id} (pid ${child.pid}); lab agents now use RemoteAgent → claude -p on this machine.`);
})().catch(e => { console.error(e.message); process.exit(1); });
