// Lab: pair THIS machine as a RemoteAgent runner and route the lab tenant's agents to it — real agent runs on
// the local `claude -p` (your Claude subscription), no API key on the server. Invoked by `lab.ps1 pair`.
//
//   1. signs in to the lab desktop as the seeded operator (lab-only test credentials, local .localhost host);
//   2. Board → Computers → registers a runner, reads the one-time pairing token from the readout;
//   3. Settings → AI → Routing → Force provider = RemoteAgent;
//   4. starts the runner DETACHED with the token in its environment only — the token is never printed or
//      written to disk; its console output goes to .lab/runner.log, its pid to .lab/runner.pid.
// The runner runs in prompt→answer mode (no --dangerously-skip-permissions): agents get model output; any
// server-driven shell command outside the allowlist is denied (no console to approve).
//
// Env: PW_PACKAGE (Playwright package dir), LAB_URL, RUNNER_EXE, STATE_DIR; UNPAIR=1 only resets Force provider.

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const pw = require(process.env.PW_PACKAGE);

const labUrl = process.env.LAB_URL || 'https://lab.dev.localhost:6180';
const runnerExe = process.env.RUNNER_EXE;
const stateDir = process.env.STATE_DIR;

async function signIn(page) {
  await page.goto(labUrl + '/');
  await page.locator('#username').fill('operator');
  await page.locator('#password').fill('operator');
  await page.locator('#kc-login, button[type=submit]').first().click();
  await page.locator('.dock').waitFor({ timeout: 30000 });
}

// Settings → AI → Routing → Force provider (empty = per-agent configuration). Each setting row saves on its own:
// pick + Save to force, Reset to drop the workspace's value.
async function setForceProvider(page, value) {
  await page.locator('.dock-item[title="Settings"]').first().click();
  const win = page.locator('.appwin.focused');
  await win.locator('.prefs-cat', { hasText: 'Routing' }).first().click();
  const select = win.locator('select[aria-label="Force provider"]');
  await select.waitFor();
  const row = win.locator('.prefs-row', { has: page.locator('select[aria-label="Force provider"]') });
  if (value) {
    await select.selectOption(value);
    await row.getByRole('button', { name: 'Save', exact: true }).click();
  } else if (await row.getByRole('button', { name: 'Reset', exact: true }).isEnabled()) {
    await row.getByRole('button', { name: 'Reset', exact: true }).click();
  }
  await page.waitForTimeout(1500);
}

(async () => {
  const browser = await pw.chromium.launch();
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 900 } })).newPage();
  if (process.env.UNPAIR === '1') {
    try { await signIn(page); await setForceProvider(page, ''); } finally { await browser.close(); }
    console.log('Force provider reset — lab agents use their per-agent configuration again.');
    return;
  }
  if (!runnerExe || !fs.existsSync(runnerExe)) { await browser.close(); throw new Error(`runner not found: ${runnerExe}`); }
  let id, token;
  try {
    await signIn(page);

    // Register a runner.
    await page.locator('.dock-item[title="Board"]').first().click();
    let win = page.locator('.appwin.focused');
    await win.locator('.board-tab', { hasText: 'Computers' }).click();
    // Re-pairing used to pile up one row (and one live token) per run: disconnect this machine's earlier lab runners first.
    const label = `lab · ${require('os').hostname()} · claude -p`;
    await win.locator('.data-table').first().waitFor({ timeout: 15000 });
    for (let i = 0; i < 50; i++) {
      const stale = win.locator('.data-table tbody tr', { hasText: label }).filter({ has: page.getByRole('button', { name: 'Disconnect', disabled: false }) }).first();
      if (!(await stale.count())) break;
      await stale.getByRole('button', { name: 'Disconnect' }).click();
      await page.waitForTimeout(800);
    }
    await win.getByPlaceholder("My laptop").fill(label);
    await win.getByRole('button', { name: 'Connect this computer' }).click();
    const readout = await win.locator('.cred-readout textarea').inputValue({ timeout: 15000 });
    id = /REMOTE_AGENT_ID=(\S+)/.exec(readout)?.[1];
    token = /REMOTE_AGENT_TOKEN=(\S+)/.exec(readout)?.[1];
    if (!id || !token) { throw new Error('could not read the pairing readout'); }

    // Route every agent of the operator's tenant to the paired runner.
    await setForceProvider(page, 'RemoteAgent');
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
