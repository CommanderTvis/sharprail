import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cacheDir } from '../../scripts/cache-dir.mjs';

// Open Playground must survive a module fetch that fails once (a network blip or a deploy in flight): the Connect screen
// says so, and the next click opens the Playground instead of repeating the same error until the page is reloaded.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
process.env.PLAYWRIGHT_BROWSERS_PATH ??= cacheDir('playwright');
const { chromium } = await import('../../src/Tereditor.Browser/node_modules/playwright/index.mjs');
const bundle = path.join(root, 'src/Tereditor.Browser/bin/Release/net10.0-browser/AppBundle');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.woff2': 'font/woff2' };
const server = http.createServer(async (request, response) => {
  response.setHeader('Cross-Origin-Opener-Policy', 'same-origin');
  response.setHeader('Cross-Origin-Embedder-Policy', 'require-corp');
  try {
    const pathname = new URL(request.url, 'http://localhost').pathname;
    const file = path.resolve(bundle, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!file.startsWith(bundle + path.sep)) throw new Error('Outside bundle');
    if (!(await fs.stat(file)).isFile()) throw new Error('Not a file');
    response.setHeader('Content-Type', types[path.extname(file)] ?? 'application/octet-stream');
    createReadStream(file).on('error', () => response.destroy()).pipe(response);
  } catch { response.writeHead(404).end(); }
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
async function until(read, accept, message) {
  const deadline = Date.now() + 45000;
  while (Date.now() < deadline) {
    const value = await read();
    if (accept(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(message);
}
let browser;
try {
  browser = await chromium.launch({ channel: process.env.TEREDITOR_BROWSER_CHANNEL ?? 'chromium', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
  let blocked = 0;
  // A static import of tabhost.js, so the error names tabhost.js itself, as the live site's did.
  await page.route(url => url.pathname.endsWith('/tabhost/disk.js'), route => blocked++ ? route.continue() : route.abort('failed'));
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.waitForFunction(() => typeof getDotnetRuntime === 'function' && !!getDotnetRuntime(0)?.getAssemblyExports);
  await page.waitForFunction(() => document.querySelector('.tereditor-window canvas')?.width > 100);
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('Tereditor.UI.dll'); globalThis.check = exports.Tereditor.Browser.Checks.BrowserChecks; });
  const status = () => page.evaluate(async () => JSON.parse(await check.ConnectStatus()));
  const snapshot = () => page.evaluate(async () => JSON.parse(await check.Snapshot()));
  async function openPlayground() {
    const { Playground } = await until(status, value => value?.Playground, 'The Connect screen has no Open Playground button');
    await page.mouse.click(Playground.X + 40, Playground.Y + 20);
  }
  // The first click can come before the button takes input, so click until the tab host is asked for.
  for (let attempt = 0; attempt < 10 && blocked === 0; attempt++) {
    await openPlayground();
    for (const end = Date.now() + 2000; Date.now() < end && blocked === 0;) await new Promise(resolve => setTimeout(resolve, 100));
  }
  const failed = await until(status, value => value?.Error, 'A failed tab host module showed no error');
  assert.match(failed.Error, /^The tab host could not start: .*tabhost\.js/);
  assert.equal(blocked, 1);
  console.log('PASS a failed module fetch is reported: ' + failed.Error);
  // The error moves the button down while it lays out, so a click may land beside it: click again until it opens.
  for (let attempt = 0; attempt < 5 && (await snapshot()).length === 0; attempt++) {
    await openPlayground();
    for (const end = Date.now() + 6000; Date.now() < end && (await snapshot()).length === 0;) await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert((await snapshot()).length > 0, `Open Playground did not open after the fetch recovered: ${JSON.stringify(await status())}`);
  console.log('PASS Open Playground opens on the next click');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
