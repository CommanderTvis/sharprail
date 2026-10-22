import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
process.env.PLAYWRIGHT_BROWSERS_PATH ??= path.join(root, '.tools/playwright');
const { chromium } = await import('../../src/SharpRail.Browser/node_modules/playwright/index.mjs');
const scratch = path.join(root, '.bench/browser-mermaid/checks');
const fixture = path.join(scratch, 'fixture');
const token = randomUUID();
let host, browser;
async function until(get, predicate, message) {
  const deadline = Date.now() + 90000;
  let value;
  while (Date.now() < deadline) {
    value = await get();
    if (predicate(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(message + ': ' + JSON.stringify(value));
}
try {
  await fs.mkdir(fixture, { recursive: true });
  execFileSync('git', ['init', '-q', '-b', 'main', fixture]);
  await fs.writeFile(path.join(fixture, 'diagram.md'), '# Browser diagram\n\n```mermaid\nflowchart LR\nA[Browser] --> B[Host]\n```\n');
  await fs.writeFile(path.join(fixture, 'invalid.md'), '# Invalid diagram\n\n```mermaid\nflowchart LR\nA --> --> broken\n```\n');
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  const address = `http://127.0.0.1:${port}`;
  host = spawn(process.env.DOTNET ?? 'dotnet', [path.join(root, 'src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll')], {
    cwd: fixture, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, SHARPRAIL_ROOT: fixture, SHARPRAIL_STATE_DIR: path.join(scratch, 'state'),
      SHARPRAIL_TOKEN: token, SHARPRAIL_PORT: String(port), SHARPRAIL_BROWSER_ROOT: path.join(root, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle') }
  });
  let hostErrors = ''; host.stderr.on('data', bytes => { hostErrors += bytes; });
  await until(async () => { try { return (await fetch(address)).ok; } catch { if (host.exitCode !== null) throw new Error(hostErrors); return false; } }, Boolean, 'Host did not start');
  browser = await chromium.launch({ channel: 'chromium', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
  page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
  page.on('requestfailed', request => console.error('Request failed: ' + request.url() + ' ' + request.failure()?.errorText));
  await page.addInitScript(({ address, token, fixture }) => {
    sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
    if (localStorage.getItem('sharprail:profile')) return;
    const tab = { Id: 'file:diagram.md', Title: 'diagram.md', Kind: 'file', Path: 'diagram.md' };
    const layout = {
      Center: { GroupId: 'center' }, Groups: [
        { Id: 'center', Region: 'center' },
        { Id: 'projects', Region: 'left', Tools: [{ Id: 'projects', Title: 'Projects', Kind: 'tool' }] },
        { Id: 'files', Region: 'right', Tools: [{ Id: 'files', Title: 'Files', Kind: 'tool' }] }
      ], ActiveWorkspace: fixture,
      Workspaces: { [fixture]: { Documents: { center: [tab] }, Selected: { center: tab.Id }, FocusedCenter: 'center' } }
    };
    localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture, Layout: layout }] }));
  }, { address, token, fixture });
  async function exportsReady() {
    await page.waitForFunction(() => globalThis.getDotnetRuntime?.(0)?.getAssemblyExports, {}, { timeout: 90000 });
    await page.evaluate(async () => { globalThis.check = (await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll')).SharpRail.Browser.Checks.BrowserChecks; });
  }
  const state = () => page.evaluate(async () => JSON.parse(await check.MermaidState()));
  async function rendered(description, diff = false) {
    const result = await until(state, value => (!diff || value?.ActiveKind === 'diff' && value?.RenderedDiff) && (value?.Images.length || value?.Errors.length), description);
    assert.equal(result.Errors.length, 0, description + ': ' + result.Errors.join('; '));
    assert.ok(result.Images.some(image => image.Width > 0 && image.Height > 0 && image.SourceWidth > 0 && image.SourceHeight > 0), description);
  }
  await page.goto(address); await exportsReady();
  await rendered('An initially restored browser Markdown tab must render its Mermaid diagram');
  await page.evaluate(() => check.OpenDocument('invalid.md'));
  const invalid = await until(state, value => value?.Errors.length, 'Invalid Mermaid must show a readable error');
  assert.match(invalid.Errors.join(' '), /failed|error|parse/i);
  assert.doesNotMatch(invalid.Errors.join(' '), /not available on this platform/);
  await page.evaluate(() => check.OpenDocument('diagram.md'));
  await rendered('Opening a Markdown tab normally must render Mermaid');
  await page.reload(); await exportsReady();
  await rendered('Reloading the saved Markdown tab must render Mermaid');
  await page.evaluate(() => check.OpenMermaidDiff('diagram.md'));
  await rendered('A rendered Markdown diff must render Mermaid', true);
  assert.deepEqual(errors, []);
  console.log('PASS browser Mermaid: initial restored tab, ordinary open, invalid-syntax error, saved-tab reload and Markdown diff');
} finally {
  if (browser) {
    let timer;
    try { await Promise.race([browser.close(), new Promise(resolve => { timer = setTimeout(resolve, 10000); })]); }
    finally { clearTimeout(timer); }
  }
  if (host && host.exitCode === null) { const stopped = once(host, 'exit'); host.kill('SIGTERM'); await stopped; }
  await fs.rm(scratch, { recursive: true, force: true });
}
