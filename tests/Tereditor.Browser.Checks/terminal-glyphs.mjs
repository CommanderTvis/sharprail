import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cacheDir } from '../../scripts/cache-dir.mjs';

// The Playground terminal must draw every glyph row: at DPR 1 and 14 px the bottom bar of "E" was lost, so README read as
// RFADMF. Runs the production bridge and the pinned runtime headless, at fractional scales and across the zoom range,
// and needs the built browser vendor assets like restty-lifecycle.mjs.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const assetsRoot = process.env.TEREDITOR_BROWSER_ASSETS_ROOT ?? root;
process.env.PLAYWRIGHT_BROWSERS_PATH ??= cacheDir('playwright');
const { chromium } = await import(path.join(assetsRoot, 'src/Tereditor.Browser/node_modules/playwright/index.mjs'));
const vendor = path.join(assetsRoot, 'src/Tereditor.Browser/bin/Release/net10.0-browser/AppBundle/vendor');
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost').pathname;
    const file = url.startsWith('/vendor/') ? path.join(vendor, url.slice(8)) : path.join(root, 'src/Tereditor.Browser/wwwroot', url.slice(1));
    if (url === '/') { response.end('<!doctype html><body style="margin:0;background:#000"></body>'); return; }
    response.setHeader('Content-Type', file.endsWith('.wasm') ? 'application/wasm' : file.endsWith('.js') ? 'text/javascript' : 'application/octet-stream');
    response.end(await fs.readFile(file));
  } catch { response.statusCode = 404; response.end(); }
});
// JetBrains Mono's cap height is 730 units per em; E, D and B end in a flat bar on the baseline.
const capHeight = 0.73;
let browser;
const failures = [];
try {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  browser = await chromium.launch({ channel: process.env.TEREDITOR_BROWSER_CHANNEL ?? 'chromium', headless: true,
    executablePath: process.env.TEREDITOR_BROWSER_EXECUTABLE || undefined });
  for (const scale of [1, 1.25, 1.5, 1.75, 2]) {
    const page = await browser.newPage({ viewport: { width: 480, height: 420 }, deviceScaleFactor: scale });
    page.on('pageerror', error => { throw error; });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.evaluate(async () => {
      const bridge = await import('/restty-bridge.js');
      const element = bridge.create();
      element.style.width = '440px'; element.style.height = '400px';
      document.body.append(element);
      globalThis.terminal = element; globalThis.bridge = bridge;
      await bridge.initialize(element, 'glyphs', JSON.stringify({ raw: {}, colors: {
        background: { r: 0, g: 0, b: 0 }, foreground: { r: 255, g: 255, b: 255 }, palette: []
      } }));
    });
    for (const points of [10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]) {
      await page.evaluate(async points => {
        globalThis.bridge.fontSize(globalThis.terminal, points);
        await new Promise(resolve => setTimeout(resolve, 150));
        globalThis.bridge.output(globalThis.terminal, new TextEncoder().encode('\x1b[2J\x1b[H\r\nEEEEEEEE\r\n\r\nDDDDDDDD\r\n\r\nBBBBBBBB\r\n'));
        await new Promise(resolve => setTimeout(resolve, 250));
      }, points);
      const rows = await page.evaluate(async buffer => {
        const bitmap = await createImageBitmap(await (await fetch('data:image/png;base64,' + buffer)).blob());
        const context = new OffscreenCanvas(bitmap.width, bitmap.height).getContext('2d');
        context.drawImage(bitmap, 0, 0);
        const { data, width, height } = context.getImageData(0, 0, bitmap.width, bitmap.height);
        const ink = [];
        for (let y = 0; y < height; y++) {
          let count = 0;
          for (let x = 0; x < width; x++) if (data[(y * width + x) * 4] > 60) count++;
          ink.push(count);
        }
        return ink;
      }, (await page.locator('canvas').screenshot()).toString('base64'));
      const runs = [];
      let run = null;
      rows.forEach((count, y) => { if (count > 0) { if (!run) runs.push(run = []); run.push(count); } else run = null; });
      const pixels = Math.round(points * scale);
      for (const [index, letter] of ['E', 'D', 'B'].entries()) {
        const glyph = runs[index];
        // Whole rows of ink cover the cap height; the bar on the last row carries most of that row's width.
        const expected = Math.floor(capHeight * pixels);
        const bar = glyph && glyph.at(-1) >= 0.6 * Math.max(...glyph);
        if (!glyph || glyph.length < expected || !bar)
          failures.push(`${letter} at ${points} pt, scale ${scale}: ${glyph?.length ?? 0} ink rows (need ${expected}), last row ${glyph?.at(-1)} of at most ${glyph && Math.max(...glyph)}`);
      }
    }
    await page.close();
    console.log('measured scale', scale);
  }
  assert.deepEqual(failures, [], `Clipped glyphs:\n${failures.join('\n')}`);
  console.log('PASS terminal glyphs complete at scales 1, 1.25, 1.5, 1.75 and 2, 10-20 pt');
}
finally { await browser?.close(); server.close(); }
