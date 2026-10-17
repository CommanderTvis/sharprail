import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
process.env.PLAYWRIGHT_BROWSERS_PATH ??= path.join(root, '.tools/playwright');
const { chromium } = await import('../../src/SharpRail.Browser/node_modules/playwright/index.mjs');
const bundle = path.join(root, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.woff2': 'font/woff2' };
const server = http.createServer(async (request, response) => {
  response.setHeader('Cross-Origin-Opener-Policy', 'same-origin');
  response.setHeader('Cross-Origin-Embedder-Policy', 'require-corp');
  try {
    const pathname = new URL(request.url, 'http://localhost').pathname;
    const file = path.resolve(bundle, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!file.startsWith(bundle + path.sep)) throw new Error('Outside bundle');
    const stat = await fs.stat(file);
    if (!stat.isFile()) throw new Error('Not a file');
    response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
    createReadStream(file).on('error', () => response.destroy()).pipe(response);
  } catch { response.writeHead(404).end(); }
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const address = `http://127.0.0.1:${server.address().port}`;
const host = spawn(path.join(root, '.tools/dotnet/dotnet'), ['tests/SharpRail.Checks/bin/Release/net10.0/SharpRail.Checks.dll', '--local-browser-fixture'], { cwd: root, stdio: ['pipe', 'pipe', 'pipe'] });
let stderr = '', last;
host.stderr.on('data', data => { stderr += data; });
const replies = [];
const lines = readline.createInterface({ input: host.stdout });
lines.on('line', line => { if (line.startsWith('LOCAL_BROWSER_FIXTURE ')) { last = JSON.parse(line.slice(22)); replies.push(last); } else if (line.includes('/browser/connect') || line.includes('Exception') || line.includes('LocalBrowser')) console.log('HOST ' + line); });
async function until(read, accept, message) {
  const deadline = Date.now() + 45000;
  while (Date.now() < deadline) {
    if (host.exitCode !== null) throw new Error(`Fixture exited ${host.exitCode}: ${stderr}`);
    const value = await read();
    if (accept(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(message);
}
async function command(text) {
  replies.length = 0;
  host.stdin.write(text + '\n');
  return until(() => replies.find(reply => reply.kind === text), Boolean, `No fixture response to ${text}`);
}
let browser, browserServer;
const watchdog = setTimeout(() => { console.error('Browser approval journey exceeded two minutes'); browserServer?.kill().catch(() => {}); host.kill('SIGINT'); }, 120000);
try {
  await until(() => last, value => value?.kind === 'ready', 'The embedded app did not start');
  const endpoint = last.endpoint;
  browserServer = await chromium.launchServer({ channel: process.env.SHARPRAIL_BROWSER_CHANNEL ?? 'chromium', headless: true });
  browser = await chromium.connect(browserServer.wsEndpoint());
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];
  page.on('pageerror', error => { errors.push(error.message); console.error(error.stack); });
  page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
  page.on('response', response => { if (response.url().includes('/browser/connect')) console.log('HTTP ' + response.status() + ' ' + response.url()); });
  page.on('requestfailed', request => console.error(request.url() + ': ' + request.failure()?.errorText));
  await page.goto(address);
  async function exports() {
    await page.waitForFunction(() => typeof getDotnetRuntime === 'function' && !!getDotnetRuntime(0)?.getAssemblyExports);
    await page.waitForFunction(() => document.querySelector('.sharprail-window canvas')?.width > 100);
    await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll'); globalThis.check = exports.SharpRail.Browser.Checks.BrowserChecks; });
  }
  await exports();
  console.log('READY browser exports and connect canvas');
  const fields = () => page.evaluate(async () => JSON.parse(await check.ConnectFields()));
  const status = () => page.evaluate(async () => JSON.parse(await check.ConnectStatus()));
  const snapshot = async () => { let timer; try { return await Promise.race([page.evaluate(async () => JSON.parse(await check.Snapshot())), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Browser dispatcher stopped responding')), 5000); })]); } finally { clearTimeout(timer); } };
  const modifier = process.platform === 'darwin' ? 'Meta' : 'Control';
  async function setAddress(value) {
    await until(async () => {
      const field = (await fields()).find(field => field.Name === 'ConnectAddress');
      if (!field?.Position) return null;
      await page.mouse.click(field.Position.X + 20, field.Position.Y + 15);
      return (await fields()).find(field => field.Name === 'ConnectAddress');
    }, field => field?.IsFocused, 'Address did not receive focus');
    await page.keyboard.press(`${modifier}+a`);
    await until(fields, fields => { const field = fields.find(field => field.Name === 'ConnectAddress'); return Math.abs(field.SelectionEnd - field.SelectionStart) === field.Text.length; }, 'Address selection did not settle');
    await page.keyboard.type(value);
    await until(fields, fields => fields.find(field => field.Name === 'ConnectAddress').Text === value, 'Address edit did not settle');
  }
  async function submit() {
    const button = await status();
    assert(button.Enabled, 'Connect must be enabled with a blank token on loopback');
    await page.mouse.click(button.Position.X + 40, button.Position.Y + 20);
  }
  await setAddress(endpoint);
  console.log('READY local host address');
  assert.equal((await fields())[1].Text ?? '', '');
  assert((await status()).LocalHint);
  console.log('FORM ' + JSON.stringify(await status()));
  assert.equal((await fields())[0].Text, endpoint);
  await submit();
  const pending = await until(() => command('status'), value => value.pending, 'The app did not ask for approval');
  assert.equal(pending.origin, address);
  assert((await status()).Pending);
  await command('deny');
  console.log('PASS app received browser origin and denied consent');
  await until(status, value => value?.Error?.includes('not approved') && !value.Pending, 'Deny did not reach the browser');
  await new Promise(resolve => setTimeout(resolve, 2100));
  await submit();
  await until(() => command('status'), value => value.pending, 'Second approval prompt missing');
  await submit(); // The same button becomes Cancel.
  await until(() => command('status'), value => !value.pending && value.approved === 0, 'Cancel did not dismiss the app prompt');
  await until(status, value => value && !value.Pending, 'The browser did not finish cancelling');
  console.log('PASS browser cancel dismisses app prompt');
  await submit();
  await until(() => command('status'), value => value.pending, 'Allow prompt missing');
  await command('allow');
  await until(snapshot, value => value.length > 0, 'Approved browser did not open its workbench');
  await until(status, value => value?.Connected, 'Approved browser state did not connect');
  console.log('PASS allow opens browser workbench');
  const saved = await page.evaluate(() => JSON.parse(sessionStorage.getItem('sharprail:endpoint')));
  assert(saved.LocalApproval && saved.Token && saved.Token !== 'local-browser-fixture-master');
  console.log('PASS browser saved temporary approval');
  const accepted = await command('status');
  assert.equal(accepted.approved, 1);
  console.log('Reloading browser');
  await page.reload(); await exports();
  console.log('Reloaded browser exports');
  await until(snapshot, value => value.length > 0, 'Reload did not reuse approval');
  await until(status, value => value?.Connected, 'Reloaded browser state did not connect');
  assert.equal((await command('status')).prompts, accepted.prompts, 'Reload must not ask for approval again');
  assert.equal(await page.evaluate(() => JSON.parse(sessionStorage.getItem('sharprail:endpoint')).Token), saved.Token);
  console.log('PASS reload reuses approval');
  await command('revoke');
  await until(status, value => value?.Error?.includes('revoked'), 'Revocation did not return to the approval flow');
  assert.equal((await fields())[1].Text ?? '', '', 'Internal credentials must not appear in the token field');
  await submit();
  await until(() => command('status'), value => value.pending, 'Revoked session did not request fresh consent');
  await command('allow');
  await until(snapshot, value => value.length > 0, 'Fresh approval did not reconnect');
  await until(status, value => value?.Connected, 'Reapproved browser state did not connect');
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  console.log('PASS reapproved browser state connected; restarting listener');
  await command('restart');
  await until(status, value => value?.Error?.includes('expired'), 'Host restart must expire approval');
  assert.equal((await command('status')).approved, 0);
  await setAddress('192.0.2.1:54123');
  const remote = await status();
  assert(!remote.Enabled && !remote.LocalHint, 'Remote hosts must still require a token');
  assert.deepEqual(errors, []);
  console.log('PASS real browser blank-token consent, actual origin, deny/cancel/allow, temporary credential, reload reuse, revocation, restart and remote token requirement');
} catch (error) {
  console.error(error);
  throw error;
} finally {
  let closeTimer;
  await Promise.race([browser?.close().catch(() => {}), new Promise(resolve => { closeTimer = setTimeout(resolve, 5000); })]);
  clearTimeout(closeTimer);
  await browserServer?.kill().catch(() => {});
  if (host.exitCode === null) {
    const exited = once(host, 'exit');
    host.stdin.write('quit\n');
    const timer = setTimeout(() => host.kill('SIGINT'), 10000);
    await exited; clearTimeout(timer);
  }
  clearTimeout(watchdog);
  lines.close();
  await new Promise(resolve => server.close(resolve));
}
