import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cacheDir } from '../../scripts/cache-dir.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const assets = process.env.SHARPRAIL_BROWSER_ASSETS_ROOT ?? root;
process.env.PLAYWRIGHT_BROWSERS_PATH ??= cacheDir('playwright');
const { chromium } = await import(path.join(assets, 'src/SharpRail.Browser/node_modules/playwright/index.mjs'));
const vendor = path.join(assets, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle/vendor');
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost').pathname;
    if (url === '/') { response.end('<!doctype html><body><button id="other">Other input</button></body>'); return; }
    const file = url.startsWith('/vendor/') ? path.join(vendor, url.slice(8)) : path.join(root, 'src/SharpRail.Browser/wwwroot', url.slice(1));
    response.setHeader('Content-Type', file.endsWith('.wasm') ? 'application/wasm' : file.endsWith('.js') ? 'text/javascript' : 'application/octet-stream');
    response.end(await fs.readFile(file));
  } catch { response.statusCode = 404; response.end(); }
});
let browser;
try {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  browser = await chromium.launch({ channel: 'chromium' });
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.evaluate(async () => {
    globalThis.bridge = await import('/restty-bridge.js');
    const events = await import('/sharprail.js');
    globalThis.messages = [];
    events.registerEvents(message => {
      if (!message.startsWith('restty:')) return;
      const value = JSON.parse(message.slice(7));
      messages.push(value);
      if (value.activity) bridge.resumeHint(current, '', '', '');
    });
    globalThis.current = bridge.create(); current.style.width = '900px'; current.style.height = '500px'; document.body.append(current);
    await bridge.initialize(current, 1, JSON.stringify({ raw: {}, colors: { background: { r: 0, g: 0, b: 0 }, foreground: { r: 255, g: 255, b: 255 }, palette: [] } }));
    document.getElementById('other').focus();
    globalThis.before = { columns: current.dataset.columns, rows: current.dataset.rows, width: current.clientWidth, height: current.clientHeight };
  });
  assert.equal(await page.locator('[role=status]').count(), 0);
  await page.evaluate(() => bridge.resumeHint(current, 'Press Enter to resume this tab’s previous session.', 'rgb(20, 20, 20)', 'rgb(220, 220, 220)'));
  await page.locator('[role=status]').waitFor({ state: 'visible' });
  assert.deepEqual(await page.evaluate(() => ({ columns: current.dataset.columns, rows: current.dataset.rows, width: current.clientWidth, height: current.clientHeight })), await page.evaluate(() => before));
  assert.equal(await page.evaluate(() => document.activeElement.id), 'other');
  assert.equal(await page.locator('[role=status]').evaluate(element => getComputedStyle(element).pointerEvents), 'none');
  await page.evaluate(() => { messages.length = 0; bridge.output(current, new TextEncoder().encode('\x1b[6n')); });
  await page.waitForFunction(() => messages.some(message => message.text?.endsWith('R')));
  assert.equal(await page.evaluate(() => messages.some(message => message.activity)), false);
  assert.equal(await page.locator('[role=status]').count(), 1);
  await page.locator('canvas').click();
  await page.evaluate(() => messages.length = 0);
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => messages.some(message => message.text === '\r'));
  assert.equal(await page.evaluate(() => messages.filter(message => message.text === '\r').length), 1);
  assert.equal(await page.locator('[role=status]').count(), 0);
  await page.evaluate(() => {
    bridge.watch(current, 80, 24); messages.length = 0;
    current.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    current.dispatchEvent(new InputEvent('input', { bubbles: true }));
    current.dispatchEvent(new ClipboardEvent('paste', { bubbles: true }));
  });
  assert.equal(await page.evaluate(() => messages.some(message => message.activity)), false);
  await page.evaluate(() => {
    bridge.destroy(current); messages.length = 0;
    current.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    current.dispatchEvent(new InputEvent('input', { bubbles: true }));
  });
  assert.equal(await page.evaluate(() => messages.length), 0);
  assert.equal(await page.locator('[role=status]').count(), 0);
  console.log('PASS Restty resume hint: visible noninteractive overlay, unchanged grid/focus, user activity versus query replies, exactly-once Enter, read-only and listener teardown');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
