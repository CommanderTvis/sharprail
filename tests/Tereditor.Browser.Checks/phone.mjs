import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { cacheDir } from '../../scripts/cache-dir.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
process.env.PLAYWRIGHT_BROWSERS_PATH ??= cacheDir('playwright');
const { chromium } = await import('../../src/Tereditor.Browser/node_modules/playwright/index.mjs');
const scratch = path.join(root, '.bench/browser-phone/checks');
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
  await fs.writeFile(path.join(fixture, 'notes.md'), '# Phone notes\n\nA document the phone shell keeps open.\n');
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  const address = `http://127.0.0.1:${port}`;
  host = spawn(process.env.DOTNET ?? 'dotnet', [path.join(root, 'src/Tereditor.Host.Remote/bin/Release/net10.0/Tereditor.Host.Remote.dll')], {
    cwd: fixture, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, TEREDITOR_ROOT: fixture, TEREDITOR_STATE_DIR: path.join(scratch, 'state'),
      TEREDITOR_TOKEN: token, TEREDITOR_PORT: String(port), TEREDITOR_BROWSER_ROOT: path.join(root, 'src/Tereditor.Browser/bin/Release/net10.0-browser/AppBundle') }
  });
  let hostErrors = ''; host.stderr.on('data', bytes => { hostErrors += bytes; });
  await until(async () => { try { return (await fetch(address)).ok; } catch { if (host.exitCode !== null) throw new Error(hostErrors); return false; } }, Boolean, 'Host did not start');
  browser = await chromium.launch({ channel: 'chromium', headless: true });
  const errors = [];
  async function open(options) {
    const context = await browser.newContext(options);
    const page = await context.newPage();
    page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
    page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
    await page.addInitScript(({ address, token, fixture }) => {
      sessionStorage.setItem('tereditor:endpoint', JSON.stringify({ Address: address, Token: token }));
      if (localStorage.getItem('tereditor:profile')) return;
      const tab = { Id: 'file:notes.md', Title: 'notes.md', Kind: 'file', Path: 'notes.md' };
      const layout = {
        Center: { GroupId: 'center' }, Groups: [
          { Id: 'center', Region: 'center' },
          { Id: 'projects', Region: 'left', Tools: [{ Id: 'projects', Title: 'Projects', Kind: 'tool' }] },
          { Id: 'files', Region: 'right', Tools: [{ Id: 'files', Title: 'Files', Kind: 'tool' }] }
        ], ActiveWorkspace: fixture,
        Workspaces: { [fixture]: { Documents: { center: [tab] }, Selected: { center: tab.Id }, FocusedCenter: 'center' } }
      };
      localStorage.setItem('tereditor:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture, Layout: layout }] }));
    }, { address, token, fixture });
    await page.goto(address);
    await page.waitForFunction(() => globalThis.getDotnetRuntime?.(0)?.getAssemblyExports, {}, { timeout: 90000 });
    await page.evaluate(async () => { globalThis.check = (await getDotnetRuntime(0).getAssemblyExports('Tereditor.UI.dll')).Tereditor.Browser.Checks.BrowserChecks; });
    return page;
  }
  const state = page => page.evaluate(async () => { const value = await check.PhoneState(); return value === 'null' ? null : JSON.parse(value); });
  const shell = (page, compact, description) => until(() => state(page), value => value && value.Compact === compact && value.Windows === 1 && value.BarVisible === compact, description);

  // The rule itself: width under 600, or a touch screen whose smallest side is under 600; 40 px of hysteresis once in the phone shell.
  const page = await open({ viewport: { width: 1280, height: 900 } });
  const decide = (...args) => page.evaluate(args => check.PhoneDecision(...args), args);
  assert.equal(await decide(false, 599, false, 1080), true, 'Under the breakpoint is a phone');
  assert.equal(await decide(false, 600, false, 1080), false, 'The breakpoint itself is wide');
  assert.equal(await decide(true, 620, false, 1080), true, 'A phone shell stays inside the hysteresis');
  assert.equal(await decide(true, 640, false, 1080), false, 'A phone shell ends past the hysteresis');
  assert.equal(await decide(false, 844, true, 390), true, 'A phone turned sideways stays a phone');
  assert.equal(await decide(false, 820, true, 820), false, 'A tablet keeps the desktop frame');

  // Desktop width: the unified frame, no page bar.
  const wide = await shell(page, false, 'A wide viewport must keep the desktop frame');
  assert.equal(wide.Touch, false);

  // Narrowing the same page swaps the shell and keeps the workspace, the open document and the window.
  await page.setViewportSize({ width: 390, height: 844 });
  const narrow = await shell(page, true, 'A narrow viewport must get the phone shell');
  assert.equal(narrow.Touch, true);
  assert.equal(narrow.SettingsVisible, false, 'The phone shell has no title-bar Settings button');
  assert.equal(narrow.ActiveWorkspace, wide.ActiveWorkspace);
  assert.equal(narrow.Selected, wide.Selected);
  assert.ok(narrow.BarHeight > 0 && Math.abs(narrow.BarBottom - 844) < 2, 'The page bar sits at the bottom of the viewport: ' + JSON.stringify(narrow));
  assert.ok(narrow.Width <= 390, 'The workbench fits the phone width: ' + narrow.Width);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'The page does not scroll sideways');

  await page.setViewportSize({ width: 1280, height: 900 });
  const back = await shell(page, false, 'Widening the page must bring the desktop frame back');
  assert.equal(back.ActiveWorkspace, wide.ActiveWorkspace);
  assert.equal(back.Selected, wide.Selected);
  await page.context().close();

  // A real phone profile: touch, mobile viewport, 390 x 844, in both orientations.
  const phone = await open({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 3 });
  const portrait = await shell(phone, true, 'A touch phone must get the phone shell');
  assert.equal(portrait.Touch, true);
  assert.equal(await phone.evaluate(() => matchMedia('(pointer: coarse)').matches), true);
  await phone.setViewportSize({ width: 844, height: 390 });
  await new Promise(resolve => setTimeout(resolve, 1000));
  const landscape = await shell(phone, true, 'A phone turned sideways must keep the phone shell');
  assert.equal(landscape.ActiveWorkspace, portrait.ActiveWorkspace);
  assert.equal(landscape.Selected, portrait.Selected);
  assert.deepEqual(errors, []);
  console.log('PASS browser phone shell: breakpoint rule, switching both ways with the workspace and document kept, touch phone in both orientations');
} finally {
  if (browser) {
    let timer;
    try { await Promise.race([browser.close(), new Promise(resolve => { timer = setTimeout(resolve, 10000); })]); }
    finally { clearTimeout(timer); }
  }
  if (host && host.exitCode === null) { const stopped = once(host, 'exit'); host.kill('SIGTERM'); await stopped; }
  await fs.rm(scratch, { recursive: true, force: true });
}
