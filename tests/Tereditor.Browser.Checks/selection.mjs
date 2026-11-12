import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cacheDir } from '../../scripts/cache-dir.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
process.env.PLAYWRIGHT_BROWSERS_PATH ??= cacheDir('playwright');
const { chromium } = await import('../../src/SharpRail.Browser/node_modules/playwright/index.mjs');
const bundle = path.resolve(process.env.SHARPRAIL_BROWSER_BUNDLE ?? path.join(root, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle'));
const origin = 'http://127.0.0.1:54129';
const types = { '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.json': 'application/json' };
const server = await chromium.launchServer({ channel: process.env.SHARPRAIL_BROWSER_CHANNEL ?? 'chromium', headless: true });
const browser = await chromium.connect(server.wsEndpoint());
const deadline = setTimeout(() => server.kill().catch(() => {}), 45000);
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  await page.route(origin + '/**', async route => {
    const pathname = new URL(route.request().url()).pathname;
    const headers = { 'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp' };
    if (pathname === '/') return route.fulfill({ contentType: 'text/html', headers, body: '<!doctype html><body></body>' });
    const file = path.resolve(bundle, '.' + decodeURIComponent(pathname));
    assert(file.startsWith(bundle + path.sep));
    let body;
    try { body = await fs.readFile(file); }
    catch (error) { if (error.code === 'ENOENT') return route.fulfill({ status: 404, body: '' }); throw error; }
    if (pathname === '/restty-bridge.js') {
      const source = body.toString();
      assert(source.includes('state.runtime = createResttyRuntime({'));
      body = source.replace('state.runtime = createResttyRuntime({', 'state.runtime = globalThis.runtime = createResttyRuntime({');
    }
    await route.fulfill({ body, headers, contentType: types[path.extname(file)] ?? 'application/octet-stream' });
  });
  await page.goto(origin);
  await page.evaluate(async () => {
    const { registerEvents } = await import('./sharprail.js');
    globalThis.messages = [];
    registerEvents(message => { if (message.startsWith('restty:')) messages.push(JSON.parse(message.slice(7))); });
    globalThis.bridge = await import('./restty-bridge.js');
    const host = document.createElement('div'); host.className = 'avalonia-container'; host.style.cssText = 'position:fixed;inset:0';
    globalThis.terminal = bridge.create(); terminal.style.cssText = 'position:absolute;left:300px;top:40px;width:650px;height:550px;overflow:hidden';
    host.append(terminal); document.body.append(host);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async text => globalThis.copied = text } });
    await bridge.initialize(terminal, -1, JSON.stringify({ raw: {}, colors: { background: { r: 0, g: 0, b: 0 }, foreground: { r: 255, g: 255, b: 255 }, palette: [] } }));
    bridge.output(terminal, new TextEncoder().encode('\x1b[?25l\x1b[2J\x1b[H' + Array.from({ length: 15 }, (_, i) => `ROW${i}_ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz`).join('\r\n')));
    globalThis.downs = []; globalThis.ups = [];
    // Reproduce Avalonia 12.1.3 MouseDevice's implicit container capture without starting the app.
    host.addEventListener('pointerdown', event => { downs.push(event.button); host.setPointerCapture(event.pointerId); event.preventDefault(); });
    host.addEventListener('pointerup', event => ups.push(event.button));
  });
  const selected = () => page.evaluate(async () => { globalThis.copied = ''; const active = await runtime.interaction.copySelectionToClipboard(); return { active, text: copied }; });
  await page.mouse.move(325, 52); await page.mouse.down(); await page.mouse.move(455, 52, { steps: 8 }); await page.mouse.up();
  const released = await selected();
  await page.mouse.move(600, 70);
  assert.deepEqual(await selected(), released, 'Selection must stop changing after release');
  assert(released.active && released.text, 'The completed drag must retain selected text');
  await page.mouse.click(630, 100);
  assert.deepEqual(await selected(), { active: false, text: '' }, 'A plain click must clear selection');
  await page.mouse.move(325, 72); await page.mouse.down(); await page.mouse.move(455, 72, { steps: 5 });
  await page.mouse.move(1000, 72); await page.mouse.up();
  const outside = await selected();
  await page.mouse.move(550, 120);
  assert.deepEqual(await selected(), outside, 'Releasing outside the terminal must end the drag');
  assert(outside.active && outside.text);
  assert.deepEqual(await page.evaluate(() => downs), [], 'Primary presses must not reach managed capture');
  await page.mouse.click(550, 120, { button: 'right' });
  assert.deepEqual(await page.evaluate(() => downs), [2], 'Right-click routing must remain available');
  assert.deepEqual(await page.evaluate(() => ups), [2]);
  await page.evaluate(() => bridge.output(terminal, new TextEncoder().encode('\x1b[?1000h\x1b[?1006h')));
  await page.mouse.click(550, 120);
  const reports = await page.evaluate(() => messages.filter(message => message.text).map(message => message.text));
  assert(reports.some(text => /^\x1b\[<0;\d+;\d+M$/.test(text)) && reports.some(text => /^\x1b\[<0;\d+;\d+m$/.test(text)), 'Native application mouse mode must receive press and release');
  assert(await page.evaluate(() => messages.some(message => message.focus)), 'Terminal presses must notify client-side pane focus');
  await page.evaluate(() => bridge.destroy(terminal));
  console.log('PASS native terminal selection: release, click-clear, outside capture, focus notification, right click and application mouse reports');
} finally {
  clearTimeout(deadline);
  await server.kill();
}
