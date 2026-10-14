import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Exercise the production bridge and pinned runtime without a workbench/host startup.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const assetsRoot = process.env.SHARPRAIL_BROWSER_ASSETS_ROOT ?? root;
process.env.PLAYWRIGHT_BROWSERS_PATH ??= path.join(assetsRoot, '.tools/playwright');
const { chromium } = await import(path.join(assetsRoot, 'src/SharpRail.Browser/node_modules/playwright/index.mjs'));
const vendor = path.join(assetsRoot, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle/vendor');
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost').pathname;
    const file = url.startsWith('/vendor/') ? path.join(vendor, url.slice(8)) : path.join(root, 'src/SharpRail.Browser/wwwroot', url.slice(1));
    if (url === '/') { response.end('<!doctype html><body></body>'); return; }
    response.setHeader('Content-Type', file.endsWith('.wasm') ? 'application/wasm' : file.endsWith('.js') ? 'text/javascript' : 'application/octet-stream');
    response.end(await fs.readFile(file));
  } catch { response.statusCode = 404; response.end(); }
});
let browser;
try {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  browser = await chromium.launch({ channel: 'chromium', headless: !process.argv.includes('--headed') });
  const page = await browser.newPage();
  const trackGpu = () => {
    // Keep detached contexts and resource handles reachable: GC must not mask a leak.
    globalThis.glRecords = [];
    const get = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (kind, ...args) {
      const gl = get.call(this, kind, ...args);
      if (kind !== 'webgl2' || !gl || globalThis.glRecords.some(record => record.gl === gl)) return gl;
      const record = { gl, resources: [], created: {}, draws: 0 };
      const debug = gl.getExtension('WEBGL_debug_renderer_info');
      record.renderer = debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : null;
      globalThis.glRecords.push(record);
      for (const type of ['Buffer', 'Texture', 'Program', 'Shader', 'Framebuffer', 'Renderbuffer', 'VertexArray', 'Query', 'Sampler', 'TransformFeedback']) {
        const original = gl['create' + type].bind(gl);
        gl['create' + type] = (...values) => {
          const resource = original(...values);
          if (resource) { record.resources.push({ type, resource }); record.created[type] = (record.created[type] ?? 0) + 1; }
          return resource;
        };
      }
      const fence = gl.fenceSync.bind(gl);
      gl.fenceSync = (...values) => {
        const resource = fence(...values);
        if (resource) { record.resources.push({ type: 'Sync', resource }); record.created.Sync = (record.created.Sync ?? 0) + 1; }
        return resource;
      };
      const draw = gl.drawArraysInstanced.bind(gl);
      gl.drawArraysInstanced = (...values) => { record.draws++; return draw(...values); };
      return gl;
    };
    globalThis.resourceSnapshot = () => globalThis.glRecords.map(record => ({
      lost: record.gl.isContextLost(), renderer: record.renderer, draws: record.draws, created: record.created,
      live: record.resources.filter(({ type, resource }) => record.gl['is' + type](resource)).reduce((result, { type }) => {
        result[type] = (result[type] ?? 0) + 1; return result;
      }, {})
    }));
  };
  await page.addInitScript(trackGpu);
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  const cdp = await browser.newBrowserCDPSession();
  const gpu = await cdp.send('SystemInfo.getInfo');
  console.log('GPU:', JSON.stringify(gpu.gpu.devices), gpu.gpu.auxAttributes.glImplementationParts);
  if (process.argv.includes('--require-hardware'))
    assert.ok(!/swiftshader|software|llvmpipe|softpipe/i.test(JSON.stringify(gpu.gpu.devices)), 'Hardware GPU was required');
  const trace = [];
  cdp.on('Tracing.dataCollected', ({ value }) => trace.push(...value));
  await cdp.send('Tracing.start', { categories: 'disabled-by-default-memory-infra', options: 'record-as-much-as-possible' });
  const dump = async label => {
    await cdp.send('Tracing.recordClockSyncMarker', { syncId: `restty:${label}:begin` });
    const result = await cdp.send('Tracing.requestMemoryDump', { deterministic: true, levelOfDetail: 'detailed' });
    await cdp.send('Tracing.recordClockSyncMarker', { syncId: `restty:${label}:end` });
    assert.ok(result.success, `${label} GPU memory dump failed`);
    console.log('Memory dump:', label, result);
    return { label, ...result };
  };
  const dumps = [await dump('baseline')];
  for (let cycle = 0; cycle < 48; cycle++) {
    await page.evaluate(async () => {
      const bridge = await import('/restty-bridge.js');
      const element = bridge.create();
      element.style.width = '900px'; element.style.height = '500px';
      document.body.append(element);
      globalThis.currentTerminal = element;
      await bridge.initialize(element, 'lifecycle-test', JSON.stringify({ raw: {}, colors: {
        background: { r: 0, g: 0, b: 0 }, foreground: { r: 255, g: 255, b: 255 }, palette: []
      } }));
      bridge.output(element, new TextEncoder().encode(('GPU lifecycle Ω 中文 🦀\r\n').repeat(100)));
    });
    await page.waitForFunction(() => globalThis.glRecords.at(-1).draws > 0);
    const opened = await page.evaluate(() => globalThis.resourceSnapshot().at(-1));
    if (cycle === 0) console.log('Terminal WebGL renderer:', opened.renderer);
    if (process.argv.includes('--require-hardware'))
      assert.ok(opened.renderer && !/swiftshader|software|llvmpipe|softpipe/i.test(opened.renderer), 'Terminal WebGL hardware renderer was required');
    assert.ok(Object.keys(opened.created).length > 0, 'No real GPU allocations were observed');
    if (cycle === 0 || cycle === 47) dumps.push(await dump('open-' + (cycle + 1)));
    await page.evaluate(async () => {
      const bridge = await import('/restty-bridge.js');
      bridge.destroy(globalThis.currentTerminal); bridge.destroy(globalThis.currentTerminal);
      globalThis.currentTerminal.remove();
    });
    const closed = await page.evaluate(() => globalThis.resourceSnapshot());
    assert.ok(closed.every(record => record.lost), `Cycle ${cycle}: live detached contexts: ${JSON.stringify(closed)}`);
    assert.ok(closed.every(record => Object.keys(record.live).length === 0), `Cycle ${cycle}: live GPU objects: ${JSON.stringify(closed)}`);
    const draws = closed.map(record => record.draws);
    await page.waitForTimeout(100);
    assert.deepEqual(await page.evaluate(() => globalThis.resourceSnapshot().map(record => record.draws)), draws, 'Disposed renderer continued drawing');
    console.log('PASS cycle', cycle + 1, 'created', JSON.stringify(opened.created), 'live contexts/resources 0');
    if ([0, 11, 23, 47].includes(cycle)) {
      await page.waitForTimeout(2000);
      dumps.push(await dump('closed-' + (cycle + 1)));
    }
  }
  await page.evaluate(async () => {
    const bridge = await import('/restty-bridge.js');
    const element = bridge.create(); document.body.append(element);
    const pending = bridge.initialize(element, 'cancelled-init', '{"colors":{}}');
    bridge.destroy(element); await pending; element.remove();
  });
  assert.ok((await page.evaluate(() => globalThis.resourceSnapshot())).every(record => record.lost && Object.keys(record.live).length === 0));
  // Dispose while runtime.init is awaiting a font, not only before its import resolves.
  const cancelling = await browser.newPage();
  await cancelling.addInitScript(trackGpu);
  let fontRoute;
  const heldFont = new Promise(resolve => { fontRoute = resolve; });
  await cancelling.route('**/JetBrainsMono-Regular.ttf', route => fontRoute(route));
  await cancelling.goto(`http://127.0.0.1:${server.address().port}`);
  await cancelling.evaluate(async () => {
    const bridge = await import('/restty-bridge.js');
    const element = bridge.create(); document.body.append(element);
    globalThis.pendingElement = element;
    globalThis.pendingInitialization = bridge.initialize(element, 'font-init', '{"raw":{},"colors":{"palette":[]}}');
  });
  const route = await Promise.race([heldFont, new Promise((_, reject) => setTimeout(() => reject(new Error('Font initialization was not held')), 10000))]);
  await cancelling.evaluate(async () => (await import('/restty-bridge.js')).destroy(globalThis.pendingElement));
  await route.continue();
  await cancelling.evaluate(async () => { await globalThis.pendingInitialization; globalThis.pendingElement.remove(); });
  assert.deepEqual(await cancelling.evaluate(() => globalThis.resourceSnapshot()), [], 'Cancelled initialization allocated a GPU context');
  const failed = await page.evaluate(async () => {
    const bridge = await import('/restty-bridge.js');
    const element = bridge.create(); document.body.append(element);
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (...args) {
      const gl = original.apply(this, args);
      if (args[0] === 'webgl2' && gl) gl.createBuffer = () => { throw new Error('Injected GPU allocation failure'); };
      return gl;
    };
    let message;
    try { await bridge.initialize(element, 'failed-init', '{"raw":{},"colors":{"palette":[]}}'); }
    catch (error) { message = error.message; }
    finally { HTMLCanvasElement.prototype.getContext = original; bridge.destroy(element); element.remove(); }
    return { message, record: globalThis.resourceSnapshot().at(-1) };
  });
  assert.match(failed.message, /Injected GPU allocation failure/);
  assert.ok(failed.record.created.Program > 0, 'Failure did not happen after graphics allocation');
  assert.ok(failed.record.lost && Object.keys(failed.record.live).length === 0, 'Failed initialization retained GPU resources');
  const finished = new Promise(resolve => cdp.once('Tracing.tracingComplete', resolve));
  await cdp.send('Tracing.end'); await finished;
  const memory = trace.filter(event => event.ph === 'v' && event.args?.dumps);
  const gpuPids = trace.filter(event => event.name === 'process_name' && /gpu/i.test(event.args.name)).map(event => event.pid);
  // Chromium's legacy JSON export remaps dump IDs. Correlate explicit clock markers,
  // not array positions or the CDP GUID with the unrelated exported ID.
  const gpuAllocators = dumps.map(({ label, dumpGuid }) => {
    const begin = trace.find(event => event.name === 'clock_sync' && event.args?.sync_id === `restty:${label}:begin`);
    const end = trace.find(event => event.name === 'clock_sync' && event.args?.sync_id === `restty:${label}:end`);
    assert.ok(begin && end, `${label} memory dump markers were missing`);
    const matches = memory.filter(event => gpuPids.includes(event.pid) && event.args.dumps.allocators?.gpu && event.ts >= begin.ts && event.ts <= end.ts);
    assert.equal(matches.length, 1, `${label} GPU memory dump correlation was ambiguous`);
    const event = matches[0];
    return { label, dumpGuid, pid: event.pid, traceId: event.id, values: Object.fromEntries(Object.entries(event.args.dumps.allocators)
      .filter(([name]) => ['gpu', 'gpu/angle/metal', 'gpu/gl', 'gpu/shared_images', 'gpu/shared_image'].includes(name))
      .map(([name, values]) => [name, parseInt(values.attrs?.size?.value ?? '0', 16)])) };
  });
  console.log('GPU-process allocator bytes:', JSON.stringify(gpuAllocators));
  const valuesFor = label => gpuAllocators.find(sample => sample.label === label).values;
  console.log('Observed warmed cycle allocation change:', JSON.stringify({
    warmBaseline: valuesFor('closed-24'), open: valuesFor('open-48'), after: valuesFor('closed-48')
  }));
  const memoryReturned = valuesFor('open-48').gpu > valuesFor('closed-48').gpu && valuesFor('closed-48').gpu <= valuesFor('closed-24').gpu;
  console.log(`MEASURED GPU-process allocated-byte return to warmed baseline: ${memoryReturned ? 'observed' : 'not established'}. Physical VRAM residency was not measured.`);
  if (process.argv.includes('--require-memory-return')) {
    assert.ok(memoryReturned, 'GPU-process allocated bytes did not return to the warmed closed baseline after a positive open allocation');
    console.log('PASS required GPU-process allocated-byte return (open > closed and closed <= warmed baseline)');
  }
  console.log('PASS graphics-resource disposal, idempotent close, cancelled and failed initialization.');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
