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
const scratch = path.join(root, '.bench/restty-default/checks');
const fixture = path.join(scratch, 'fixture');
const token = randomUUID();
const profile = process.argv.includes('--profile');
let host, browser;
async function until(get, predicate, description) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    const value = await get();
    if (predicate(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(description);
}
try {
  await fs.mkdir(fixture, { recursive: true });
  execFileSync('git', ['init', '-q', '-b', 'main', fixture]);
  const common = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: root, encoding: 'utf8' }).trim();
  await fs.mkdir(path.join(fixture, '.git/objects/info'), { recursive: true });
  await fs.writeFile(path.join(fixture, '.git/objects/info/alternates'), path.join(common, 'objects') + '\n');
  execFileSync('git', ['update-ref', 'refs/heads/main', execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim()], { cwd: fixture });
  execFileSync('git', ['sparse-checkout', 'set', '--no-cone', '/README.md'], { cwd: fixture });
  execFileSync('git', ['checkout', '-q', 'main'], { cwd: fixture });
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port; await new Promise(resolve => server.close(resolve));
  const address = `http://127.0.0.1:${port}`;
  host = spawn(path.join(root, '.tools/dotnet/dotnet'), [path.join(root, 'src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll')], {
    cwd: fixture, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, NO_COLOR: '', DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE: 'false',
      SHARPRAIL_ROOT: fixture, SHARPRAIL_STATE_DIR: path.join(scratch, 'state'), SHARPRAIL_TOKEN: token, SHARPRAIL_PORT: String(port),
      SHARPRAIL_BROWSER_ROOT: process.env.SHARPRAIL_BROWSER_BUNDLE ?? path.join(root, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle') }
  });
  let hostError = ''; host.stderr.on('data', data => { hostError += data; });
  await until(async () => { try { return await fetch(address); } catch { if (host.exitCode !== null) throw new Error(hostError); } }, r => r?.ok, 'Host did not start');
  browser = await chromium.launch({ channel: 'chromium', headless: true,
    args: process.env.PROFILE_UNCAPPED ? ['--disable-frame-rate-limit', '--disable-gpu-vsync'] : [] });
  const page = await browser.newPage({ viewport: { width: 1840, height: 1340 }, deviceScaleFactor: Number(process.env.PROFILE_DPR ?? 1) });
  const errors = []; page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('404')) { errors.push(message.text()); console.error(message.text()); } });
  await page.addInitScript(({ address, token, fixture }) => {
    sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
    localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture }] }));
    globalThis.gpuFrames = 0; globalThis.animationFrames = 0; let dirty = false;
    const draw = WebGL2RenderingContext.prototype.drawArraysInstanced;
    WebGL2RenderingContext.prototype.drawArraysInstanced = function (...args) {
      if (this.canvas.closest?.('.sharprail-restty')) dirty = true;
      return draw.apply(this, args);
    };
    const tick = () => { globalThis.animationFrames++; if (dirty) { globalThis.gpuFrames++; dirty = false; } requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
  }, { address, token, fixture });
  await page.goto(address);
  await page.waitForFunction(() => document.querySelector('.sharprail-restty')?.dataset.ready === 'true', {}, { timeout: 90000 });
  const term = page.locator('.sharprail-restty');
  const screen = () => page.evaluate(async () => (await import('./restty-bridge.js')).screen(document.querySelector('.sharprail-restty')));
  await until(screen, value => value.includes('fixture'), 'Host prompt did not reach restty');
  assert.equal(await term.getAttribute('data-backend'), 'webgl2');
  const command = async text => { await term.locator('canvas').click(); await page.keyboard.type(text); await page.keyboard.press('Enter'); };
  await command("printf '\\033[31m%s_%s\\033[0m\\n' RESTTY PASS");
  await until(screen, value => value.includes('RESTTY_PASS'), 'Keyboard input/Enter or host output failed');
  console.log('PASS restty GPU initialization, live host input, Enter and output');
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.evaluate(() => navigator.clipboard.writeText("printf '%s_%s\\n' CLIPBOARD PASS"));
  await term.locator('canvas').click();
  await page.keyboard.press(process.platform === 'darwin' ? 'Meta+V' : 'Control+V');
  await page.keyboard.press('Enter');
  await until(screen, value => value.includes('CLIPBOARD_PASS'), 'Native paste failed');
  console.log('PASS native clipboard text paste');
  if (!process.env.SHARPRAIL_BROWSER_BUNDLE) {
    const invoke = (method, ...args) => page.evaluate(async ({ method, args }) => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return exports.SharpRail.Browser.Checks.BrowserChecks[method](...args);
    }, { method, args });
    const before = await page.screenshot();
    const bounds = await term.boundingBox();
    const gridBefore = [await term.getAttribute('data-columns'), await term.getAttribute('data-rows')];
    await invoke('OpenSettings', 'Layout');
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.sharprail-restty'), '::after').backgroundColor === 'rgba(0, 0, 0, 0.5)');
    await page.waitForFunction(() => document.querySelector('.sharprail-dialog')?.getBoundingClientRect().height < innerHeight);
    const dialog = await page.locator('.sharprail-dialog').boundingBox();
    const point = [[bounds.x + 5, bounds.y + 5], [bounds.x + 5, bounds.y + bounds.height - 5],
      [bounds.x + bounds.width - 5, bounds.y + 5]].find(([x, y]) => x < dialog.x || x >= dialog.x + dialog.width || y < dialog.y || y >= dialog.y + dialog.height);
    assert.ok(point, 'No exposed terminal area to check');
    const pixel = buffer => page.evaluate(async ({ data, point }) => {
      const image = new Image(); image.src = 'data:image/png;base64,' + data; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
      const context = canvas.getContext('2d'); context.drawImage(image, 0, 0);
      return [...context.getImageData(Math.floor(point[0] * devicePixelRatio), Math.floor(point[1] * devicePixelRatio), 1, 1).data];
    }, { data: buffer.toString('base64'), point });
    const original = await pixel(before);
    const dimmed = await pixel(await page.screenshot());
    for (let i = 0; i < 3; i++) assert.ok(Math.abs(dimmed[i] - original[i] * 127 / 255) <= 3, 'Terminal pixels were not dimmed with the workbench');
    await invoke('ClickSettingsButton', 'SettingsClose');
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.sharprail-restty'), '::after').backgroundColor === 'rgba(0, 0, 0, 0)');
    assert.deepEqual(await pixel(await page.screenshot()), original);
    assert.deepEqual([await term.getAttribute('data-columns'), await term.getAttribute('data-rows')], gridBefore);
    await command("printf '%s_%s\\n' MODAL PASS");
    await until(screen, value => value.includes('MODAL_PASS'), 'Terminal input did not recover after closing Settings');
    console.log('PASS Settings backdrop dims terminal pixels and restores input without changing the grid');
  }
  const originalColumns = await term.getAttribute('data-columns');
  await page.setViewportSize({ width: 1500, height: 1100 });
  await until(() => term.getAttribute('data-columns'), value => value !== originalColumns, 'Resize did not update terminal grid');
  await command("printf 'GRID:'; stty size");
  await until(screen, value => /GRID:\d+ \d+/.test(value), 'PTY resize command did not execute');
  const grid = (await screen()).match(/GRID:(\d+) (\d+)/);
  assert.equal(Number(grid[1]), Number(await term.getAttribute('data-rows')));
  assert.equal(Number(grid[2]), Number(await term.getAttribute('data-columns')));
  console.log('PASS canvas and host PTY resize agree');
  await page.evaluate(async () => {
    const { output, screen } = await import('./restty-bridge.js');
    const element = document.querySelector('.sharprail-restty');
    const bytes = new TextEncoder().encode('\r\nSPLIT_UTF8:é界\r\n');
    for (const byte of bytes) output(element, new Uint8Array([byte]));
    if (!screen(element).includes('SPLIT_UTF8:é界')) throw new Error('Split UTF-8 was corrupted');
  });
  console.log('PASS split UTF-8 output preserves terminal state');
  const newClient = async () => {
    const client = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
    await client.addInitScript(({ address, token, fixture }) => {
      sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
      localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture }] }));
    }, { address, token, fixture });
    return client;
  };
  const watcher = await newClient();
  await watcher.goto(address);
  await watcher.waitForFunction(() => document.querySelector('.sharprail-restty')?.dataset.watching === 'true', {}, { timeout: 60000 });
  await command("printf '%s_%s\\n' WATCH PASS");
  const watchScreen = () => watcher.evaluate(async () => (await import('./restty-bridge.js')).screen(document.querySelector('.sharprail-restty')));
  await until(watchScreen, value => value.includes('WATCH_PASS'), 'Restty watcher missed live output');
  await page.setViewportSize({ width: 1600, height: 1200 });
  await until(() => watcher.locator('.sharprail-restty').getAttribute('data-columns'),
    value => Number(value) !== Number(grid[2]), 'Restty watcher missed holder grid change');
  assert.equal(await watcher.locator('.sharprail-restty').getAttribute('data-columns'), await term.getAttribute('data-columns'));
  const watchedGrid = await watcher.locator('.sharprail-restty').getAttribute('data-columns');
  await watcher.setViewportSize({ width: 900, height: 700 });
  await watcher.waitForTimeout(200);
  assert.equal(await watcher.locator('.sharprail-restty').getAttribute('data-columns'), watchedGrid);
  assert.equal(await watcher.locator('.sharprail-restty canvas').evaluate(canvas => canvas.style.pointerEvents), 'none');
  await watcher.close();
  console.log('PASS shared terminal watching stays on restty and follows the holder grid');
  if (profile) {
    await page.setViewportSize({ width: 1840, height: 1340 });
    await page.waitForTimeout(500);
    const bounds = await term.boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y - 34);
    await page.mouse.down(); await page.mouse.move(bounds.x + bounds.width / 2, 110, { steps: 30 }); await page.mouse.up();
    await page.waitForTimeout(500);
    await command('term-bench --duration 15 --fps 120');
    await until(screen, value => value.includes('Render Bench'), 'Benchmark did not start');
    const start = await page.evaluate(() => ({ frames: gpuFrames, raf: animationFrames, time: performance.now() }));
    await page.waitForTimeout(13000);
    const end = await page.evaluate(() => ({ frames: gpuFrames, raf: animationFrames, time: performance.now(), dpr: devicePixelRatio }));
    const result = { seconds: (end.time - start.time) / 1000, redraws: end.frames - start.frames, animationFrames: end.raf - start.raf,
      dpr: end.dpr, columns: Number(await term.getAttribute('data-columns')), rows: Number(await term.getAttribute('data-rows')), text: await screen() };
    const name = `restty${process.env.PROFILE_UNCAPPED ? '-uncapped' : ''}-dpr${end.dpr}`;
    await fs.mkdir(path.join(root, 'artifacts/terminal-profile'), { recursive: true });
    await fs.writeFile(path.join(root, `artifacts/terminal-profile/${name}.json`), JSON.stringify(result, null, 2));
    console.log('PROFILE', JSON.stringify({ ...result, text: undefined, fps: result.redraws / result.seconds }));
    await page.screenshot({ path: path.join(scratch, 'terminal.png') });
    await page.waitForTimeout(2200);
  }
  await page.reload();
  console.log('Reloaded browser document');
  await page.waitForFunction(() => document.querySelector('.sharprail-restty')?.dataset.ready === 'true', {}, { timeout: 90000 });
  await until(screen, value => value.includes('fixture'), 'Host terminal did not survive reload');
  await command("printf '%s_%s\\n' REATTACH PASS");
  await until(screen, value => value.includes('REATTACH_PASS'), 'Input failed after reattach');
  assert.deepEqual(errors, []);
  console.log('PASS restty detach, host session survival and reattach');
  if (!process.env.SHARPRAIL_BROWSER_BUNDLE) {
    for (const failure of ['bridge', 'module', 'gpu']) {
      console.log(`Checking restty ${failure} failure`);
      const fallback = await newClient();
      if (failure === 'gpu') await fallback.addInitScript(() => {
        const get = HTMLCanvasElement.prototype.getContext;
        HTMLCanvasElement.prototype.getContext = function (kind, ...args) {
          if (kind === 'webgl2' && this.closest('.sharprail-restty')) return null;
          return get.call(this, kind, ...args);
        };
      });
      else await fallback.route(failure === 'bridge' ? '**/restty-bridge.js' : '**/vendor/restty/**', route => route.abort());
      await fallback.goto(address);
      await fallback.waitForFunction(() => document.querySelector('.sharprail-workbench canvas')?.width > 100, {}, { timeout: 60000 });
      const snapshot = () => fallback.evaluate(async () => {
        const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
        return JSON.parse(await exports.SharpRail.Browser.Checks.BrowserChecks.Snapshot());
      });
      await until(snapshot, windows => windows[0]?.Terminals.some(terminal => terminal.Text.includes('REATTACH_PASS')), `Skia fallback failed after ${failure} failure`);
      assert.equal(await fallback.locator('.sharprail-restty').count(), 0);
      await fallback.close();
      console.log(`PASS Skia fallback after restty ${failure} initialization failure`);
    }
  }
} finally {
  await browser?.close();
  if (host && host.exitCode === null && host.signalCode === null) { const stopped = once(host, 'exit'); host.kill('SIGINT'); await stopped; }
  await fs.rm(scratch, { recursive: true, force: true });
}
