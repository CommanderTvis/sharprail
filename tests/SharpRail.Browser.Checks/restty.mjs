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
const scratch = path.join(root, process.argv.includes('--wheel') ? '.bench/terminal-wheel/checks' : process.argv.includes('--file-drop') ? '.bench/file-drop/browser' : process.argv.includes('--settings') ? '.bench/settings-flicker/checks' : '.bench/restty-default/checks');
const fixture = path.join(scratch, 'fixture');
const token = randomUUID();
const profile = process.argv.includes('--profile');
let host, browser;
const wheelDeadline = process.argv.includes('--wheel') ? setTimeout(() => { void browser?.close(); }, 90000) : null;
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
  const page = await browser.newPage({ viewport: process.argv.includes('--wheel') ? { width: 1500, height: 900 } : { width: 1840, height: 1340 }, deviceScaleFactor: Number(process.env.PROFILE_DPR ?? 1) });
  if (process.argv.includes('--settings') && Number(process.env.PROFILE_DPR ?? 1) !== 1)
    await page.addInitScript(() => Object.defineProperty(ResizeObserverEntry.prototype, 'devicePixelContentBoxSize', { get: () => undefined }));
  const errors = []; page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('404')) { errors.push(message.text()); console.error(message.text()); } });
  await page.addInitScript(({ address, token, fixture }) => {
    sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
    // Restore an explicitly saved terminal; fresh workspaces no longer create one automatically.
    const terminal = { Id: 'terminal:restty-check', Title: 'Terminal 1', Kind: 'terminal' };
    const layout = {
      Center: { GroupId: 'browser-center' },
      Groups: [
        { Id: 'browser-center', Region: 'center' },
        { Id: 'browser-projects', Region: 'left', Tools: [{ Id: 'projects', Title: 'Projects', Kind: 'tool' }] },
        { Id: 'browser-files', Region: 'right', Tools: [{ Id: 'files', Title: 'Files', Kind: 'tool' }] }
      ],
      Workspaces: { [fixture]: { Documents: { 'browser-center': [terminal] }, Selected: { 'browser-center': terminal.Id }, FocusedCenter: 'browser-center' } },
      ActiveWorkspace: fixture
    };
    localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture, Layout: layout }] }));
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
  await page.waitForFunction(() => [...document.querySelectorAll('.sharprail-restty')].some(element => element.dataset.ready === 'true' && element.querySelector('canvas') && element.checkVisibility()), {}, { timeout: 90000 });
  const term = page.locator('.sharprail-restty:has(canvas):visible').first();
  const screen = () => page.evaluate(async () => {
    const element = [...document.querySelectorAll('.sharprail-restty')].find(element => element.querySelector('canvas') && element.checkVisibility());
    return element ? (await import('./restty-bridge.js')).screen(element) : '';
  });
  await until(screen, value => value.includes('fixture'), 'Host prompt did not reach restty');
  if (process.argv.includes('--watch-replay')) {
    const replay = async () => JSON.parse(await page.evaluate(async () => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return await exports.SharpRail.Browser.Checks.BrowserChecks.CheckWatchedReplay();
    }));
    const watched = await replay();
    assert.equal(watched.Watching, true);
    assert.equal(watched.Renderer, "restty");
    assert.equal(watched.Columns, 100); assert.equal(watched.Rows, 30);
    assert.match(watched.Text, /RECORDED_SCREEN/); assert.match(watched.Text, /Working 5UPDATE_SCREEN/);
    assert.deepEqual(errors, []);
    console.log('PASS restty read-only recorded alternate screen and incremental updates without resize or takeover');
    await page.evaluate(() => {
      const original = HTMLCanvasElement.prototype.getContext;
      HTMLCanvasElement.prototype.getContext = function (kind, ...args) {
        if (kind === 'webgl2' && this.closest('.sharprail-restty')) return null;
        return original.call(this, kind, ...args);
      };
    });
    const fallback = await replay();
    assert.equal(fallback.Watching, true);
    assert.equal(fallback.Renderer, "skia");
    assert.match(fallback.Text, /RECORDED_SCREEN/); assert.match(fallback.Text, /Working 5UPDATE_SCREEN/);
    assert.deepEqual(errors, []);
    console.log('PASS Skia fallback read-only recorded alternate screen and incremental updates');
  } else if (process.argv.includes('--wheel')) {
    console.log('Wheel client mounted');
    const snapshot = client => client.evaluate(async () => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return JSON.parse(await exports.SharpRail.Browser.Checks.BrowserChecks.Snapshot())[0].ProjectsScroll;
    });
    const prepare = client => client.evaluate(async () => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      await exports.SharpRail.Browser.Checks.BrowserChecks.PrepareWheelFixture();
    });
    const feed = client => client.evaluate(async () => {
      const bridge = await import('./restty-bridge.js');
      bridge.output([...document.querySelectorAll('.sharprail-restty')].find(element => element.querySelector('canvas') && element.checkVisibility()), new TextEncoder().encode('\x1b[?25l' + Array.from({ length: 300 }, (_, index) => 'WHEEL_ROW_' + String(index).padStart(3, '0')).join('\r\n') + '\r\n'));
    });
    await prepare(page);
    const projects = await until(() => snapshot(page), value => value && value.Extent > value.Viewport + 300, 'Projects fixture did not overflow');
    const sidebar = { x: projects.Position.X + projects.Width / 2, y: projects.Position.Y + Math.min(projects.Height / 2, 350) };
    await page.mouse.move(sidebar.x, sidebar.y);
    await page.mouse.wheel(0, 300);
    await until(() => snapshot(page), value => value.Offset > projects.Offset, 'Projects did not scroll under its own pointer');
    await feed(page);
    await until(screen, value => value.includes('WHEEL_ROW_299'), 'Scrollback fixture did not render');
    const bounds = await term.boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await page.waitForTimeout(300);
    const sidebarBeforeTerminal = await snapshot(page);
    const before = await term.screenshot();
    await page.mouse.wheel(0, -500);
    await page.waitForTimeout(300);
    assert.equal((await snapshot(page)).Offset, sidebarBeforeTerminal.Offset, 'Writable terminal wheel leaked into Projects');
    assert.notDeepEqual(await term.screenshot(), before, 'Writable terminal viewport did not scroll');
    await page.mouse.move(sidebar.x, sidebar.y);
    const beforeReturn = await snapshot(page);
    await page.mouse.wheel(0, beforeReturn.Offset > 100 ? -200 : 200);
    await until(() => snapshot(page), value => value.Offset !== beforeReturn.Offset, 'Returning to Projects did not restore sidebar scrolling');

    console.log('PASS writable terminal wheel stays local and Projects scrolls again on return');
    const watcher = await browser.newPage({ viewport: { width: 1200, height: 650 } });
    await watcher.addInitScript(({ address, token, fixture }) => {
      sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
      localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture }] }));
    }, { address, token, fixture });
    await watcher.goto(address);
    await watcher.waitForFunction(() => [...document.querySelectorAll('.sharprail-restty')].some(element => element.dataset.watching === 'true' && element.querySelector('canvas') && element.checkVisibility()), {}, { timeout: 60000 });
    const watched = watcher.locator('.sharprail-restty:has(canvas):visible').first();
    await until(() => watcher.evaluate(async () => (await import('./restty-bridge.js')).screen([...document.querySelectorAll('.sharprail-restty')].find(element => element.querySelector('canvas') && element.checkVisibility()))), value => value.includes('fixture'), 'Read-only terminal replay did not complete');
    await prepare(watcher);
    await feed(watcher);
    const watcherProjects = await until(() => snapshot(watcher), value => value && value.Extent > value.Viewport + 300, 'Watched Projects fixture did not overflow');
    const watcherSidebar = { x: watcherProjects.Position.X + watcherProjects.Width / 2, y: watcherProjects.Position.Y + Math.min(watcherProjects.Height / 2, 250) };
    await watcher.mouse.move(watcherSidebar.x, watcherSidebar.y);
    await watcher.mouse.wheel(0, 200);
    const watchedSidebarAfter = await until(() => snapshot(watcher), value => value.Offset > watcherProjects.Offset, 'Watched sidebar did not scroll');
    const overflow = await until(() => watched.evaluate(element => {
      const scroller = [...element.querySelectorAll('*'), element].find(child => /auto|scroll/.test(getComputedStyle(child).overflowY) && child.scrollHeight > child.clientHeight + 100);
      return scroller ? { height: scroller.clientHeight, scroll: scroller.scrollHeight, top: scroller.scrollTop } : null;
    }), value => value && value.top > 100, 'Watched terminal replay did not create scrollback');
    const watchedBounds = await watched.boundingBox();
    await watcher.mouse.move(watchedBounds.x + watchedBounds.width / 2, watchedBounds.y + watchedBounds.height / 2);
    await watcher.mouse.wheel(0, -250);
    await watcher.waitForTimeout(150);
    assert.equal((await snapshot(watcher)).Offset, watchedSidebarAfter.Offset, 'Read-only terminal wheel leaked into Projects');
    await until(() => watched.evaluate(element => {
      const scroller = [...element.querySelectorAll('*'), element].find(child => /auto|scroll/.test(getComputedStyle(child).overflowY) && child.scrollHeight > child.clientHeight + 100);
      return scroller?.scrollTop;
    }), value => value < overflow.top, 'Read-only terminal scrollback did not scroll');
    assert.equal((await snapshot(watcher)).Offset, watchedSidebarAfter.Offset, 'Read-only terminal wheel leaked into Projects');
    await watcher.mouse.move(watcherSidebar.x, watcherSidebar.y);
    const watchedBeforeReturn = await snapshot(watcher);
    await watcher.mouse.wheel(0, watchedBeforeReturn.Offset > 100 ? -200 : 200);
    await until(() => snapshot(watcher), value => value.Offset !== watchedBeforeReturn.Offset, 'Returning from read-only terminal did not restore sidebar scrolling');
    await watcher.close();
    console.log('PASS read-only terminal wheel stays local and Projects scrolls again on return');
    await page.evaluate(async () => {
      const bridge = await import('./restty-bridge.js');
      const parent = document.createElement('div');
      parent.style.cssText = 'position:fixed;left:-1000px;top:-1000px;width:320px;height:180px';
      const element = bridge.create(); parent.append(element); document.body.append(parent);
      let bubbled = 0; parent.addEventListener('wheel', () => bubbled++);
      try {
        await bridge.initialize(element, -1, JSON.stringify({ raw: {}, colors: { background: { r: 0, g: 0, b: 0 }, foreground: { r: 255, g: 255, b: 255 }, palette: [] } }));
        const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 10 });
        element.dispatchEvent(event);
        if (bubbled || event.defaultPrevented) throw new Error('Terminal wheel ownership must contain bubbling without cancelling native scrolling');
        bridge.destroy(element);
        element.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 10 }));
        if (bubbled !== 1) throw new Error('Terminal wheel listener survived disposal');
      } finally { bridge.destroy(element); parent.remove(); }
    });
    assert.deepEqual(errors, []);
    console.log('PASS browser terminal wheel stays local to writable scrollback and read-only overflow, with Projects scrolling restored on return and ownership removed on dispose');
  } else if (process.argv.includes('--file-drop')) {
    const invoke = (method, ...args) => page.evaluate(async ({ method, args }) => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return await exports.SharpRail.Browser.Checks.BrowserChecks[method](...args);
    }, { method, args });
    await invoke('FileDropProbe', true);
    const drop = () => page.evaluate(() => {
      const terminal = document.querySelector('.sharprail-restty');
      const bounds = terminal.getBoundingClientRect();
      const dataTransfer = new DataTransfer();
      dataTransfer.items.add(new File(['browser dropped\0bytes 🚀'], 'client file.txt', { type: 'application/octet-stream' }));
      Object.defineProperty(dataTransfer, 'effectAllowed', { value: 'copy' });
      for (const type of ['dragenter', 'dragover', 'drop']) terminal.querySelector('canvas').dispatchEvent(new DragEvent(type, {
        bubbles: true, cancelable: true, dataTransfer, clientX: bounds.left + 30, clientY: bounds.top + 30
      }));
    });
    await drop();
    await page.waitForTimeout(300);
    const paths = await until(async () => JSON.parse(await invoke('DroppedFiles')), paths => paths.length === 1, 'Browser drop did not reach the agent accessory');
    assert.ok(paths[0].startsWith(path.join(scratch, 'state/attachments') + path.sep));
    assert.equal(path.basename(paths[0]), 'client file.txt');
    assert.equal(await fs.readFile(paths[0], 'utf8'), 'browser dropped\0bytes 🚀');
    await invoke('FileDropProbe', false);
    await drop();
    await page.waitForTimeout(300);
    assert.equal(JSON.parse(await invoke('DroppedFiles')).length, 1);
    assert.deepEqual(errors, []);
    console.log('PASS browser file drops: restty DOM coordinates, client bytes uploaded to host, agent callback and inactive rejection');
  } else if (process.argv.includes('--settings')) {
    const invoke = (method, ...args) => page.evaluate(async ({ method, args }) => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return exports.SharpRail.Browser.Checks.BrowserChecks[method](...args);
    }, { method, args });
    for (const section of ['Appearance', 'Layout', 'Plugins']) {
      await invoke('OpenSettings', section);
      const dialog = page.locator('.sharprail-dialog');
      await page.waitForFunction(() => document.querySelector('.sharprail-workbench').inert);
      await until(() => dialog.boundingBox(), bounds => bounds?.height === page.viewportSize().height * .8, 'Settings did not finish sizing');
      await invoke('RepaintEmbeddedRoots');
      for (let frame = 0; frame < 4; frame++) {
        await page.waitForTimeout(100);
        assert.equal(await dialog.count(), 1);
        assert.equal(await dialog.evaluate(element => element.style.visibility), 'visible');
        const pixels = await page.screenshot();
        const magenta = await page.evaluate(async data => {
          const image = new Image(); image.src = 'data:image/png;base64,' + data; await image.decode();
          const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
          const context = canvas.getContext('2d'); context.drawImage(image, 0, 0);
          const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
          let count = 0;
          for (let i = 0; i < pixels.length; i += 4) if (pixels[i] === 255 && pixels[i + 1] === 0 && pixels[i + 2] === 255) count++;
          return count;
        }, pixels.toString('base64'));
        assert.equal(magenta, 0, 'The temporary embedded renderer painted over a real window');
      }
      await invoke('ClickSettingsButton', 'SettingsClose');
      await page.waitForFunction(() => !document.querySelector('.sharprail-workbench').inert);
      assert.equal(await dialog.count(), 0);
    }
    await term.locator('canvas').click();
    await page.keyboard.type("printf '%s_%s\\n' SETTINGS PASS");
    await page.keyboard.press('Enter');
    await until(screen, value => value.includes('SETTINGS_PASS'), 'Terminal input did not recover after Settings');
    assert.deepEqual(errors, []);
    console.log('PASS Settings stays painted through embedded-root repaints, reopen and close; terminal input recovers');
  } else {
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
  }
} finally {
  clearTimeout(wheelDeadline);
  await browser?.close();
  if (host && host.exitCode === null && host.signalCode === null) { const stopped = once(host, 'exit'); host.kill('SIGINT'); await stopped; }
  await fs.rm(scratch, { recursive: true, force: true });
}
