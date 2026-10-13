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
const scratch = path.join(root, '.bench/browser-checks');
const fixture = path.join(scratch, 'fixture');
const token = randomUUID();
let host, browser;

async function port() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const value = server.address().port; await new Promise(resolve => server.close(resolve));
  return value;
}
async function until(read, accept, message) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    const value = await read();
    if (accept(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(message);
}
function pdfFixture(rotation = 0, crop = '', unicode = false) {
  const content = `BT /F1 18 Tf 20 60 Td (${unicode ? 'AB' : 'Browser PDF fixture'}) Tj ET`;
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] /Rotate ${rotation} ${crop && `/CropBox [${crop}]`} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica ${unicode ? '/ToUnicode 6 0 R' : ''} >>`];
  if (unicode) {
    const map = '/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Fixture def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n2 beginbfchar\n<41> <D83DDE80>\n<42> <00E9>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend';
    objects.push(`<< /Length ${map.length} >>\nstream\n${map}\nendstream`);
  }
  let pdf = '%PDF-1.4\n'; const offsets = [];
  objects.forEach((object, index) => { offsets.push(pdf.length); pdf += `${index + 1} 0 obj\n${object}\nendobj\n`; });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

try {
  await fs.mkdir(fixture, { recursive: true });
  // Borrow existing Git objects and materialize one file: no fixture commit or signing bypass.
  execFileSync('git', ['init', '-q', '-b', 'main', fixture]);
  const common = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: root, encoding: 'utf8' }).trim();
  await fs.mkdir(path.join(fixture, '.git/objects/info'), { recursive: true });
  await fs.writeFile(path.join(fixture, '.git/objects/info/alternates'), path.join(common, 'objects') + '\n');
  execFileSync('git', ['update-ref', 'refs/heads/main', execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim()], { cwd: fixture });
  execFileSync('git', ['sparse-checkout', 'set', '--no-cone', '/README.md'], { cwd: fixture });
  execFileSync('git', ['checkout', '-q', 'main'], { cwd: fixture });
  await fs.writeFile(path.join(fixture, 'hello.txt'), 'Browser editor fixture\n');
  await fs.writeFile(path.join(fixture, 'diagram.md'), '# Browser\n\n```mermaid\nflowchart LR\nA[Browser] --> B[Host]\n```\n');
  await fs.writeFile(path.join(fixture, 'sample.pdf'), pdfFixture());
  await fs.cp(path.join(root, 'tests/SharpRail.Checks/bin/Release/net10.0/plugin-fixture/fixture'), path.join(scratch, 'state/plugins/fixture'), { recursive: true });
  const browserPort = await port();
  const address = `http://127.0.0.1:${browserPort}`;
  host = spawn(path.join(root, '.tools/dotnet/dotnet'), ['src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll'], {
    cwd: root, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, SHARPRAIL_ROOT: fixture, SHARPRAIL_STATE_DIR: path.join(scratch, 'state'),
      SHARPRAIL_TOKEN: token, SHARPRAIL_PORT: String(browserPort),
      SHARPRAIL_BROWSER_ROOT: process.env.SHARPRAIL_BROWSER_BUNDLE ?? path.join(root, 'src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle'), Logging__LogLevel__Default: 'Warning' }
  });
  let hostError = ''; host.stderr.on('data', data => { hostError += data; });
  await until(async () => { try { return await fetch(address); } catch { if (host.exitCode !== null) throw new Error(hostError); } }, response => response?.ok, 'Browser host did not start.');
  browser = await chromium.launch({ channel: 'chromium', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  // The shared Skia pixel/input suite exercises the actual loading fallback.
  // Default restty and its failure paths are covered by restty.mjs.
  if (!process.env.SHARPRAIL_BROWSER_BUNDLE) await page.route('**/vendor/restty/**', route => route.abort());
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') console.error(message.text().slice(0, 2000)); });
  const sockets = new Set(); let disconnected = false;
  await page.routeWebSocket('**/rpc', route => {
    if (disconnected) { route.close({ code: 1013, reason: 'Acceptance network interruption' }); return; }
    const server = route.connectToServer(); const connection = { route, server }; sockets.add(connection);
    route.onClose((code, reason) => { sockets.delete(connection); server.close({ code, reason }); });
    server.onClose((code, reason) => { sockets.delete(connection); route.close({ code, reason }); });
  });
  await page.addInitScript(({ address, token, fixture, deployment }) => {
    if (deployment) sessionStorage.setItem('sharprail:endpoint', JSON.stringify({ Address: address, Token: token }));
    if (!localStorage.getItem('sharprail:profile')) localStorage.setItem('sharprail:profile', JSON.stringify({ LastProject: fixture, Windows: [{ LastProject: fixture }] }));
  }, { address, token, fixture, deployment: !!process.env.SHARPRAIL_BROWSER_BUNDLE });
  await page.goto(address);
  await page.waitForFunction(() => globalThis.getDotnetRuntime?.(0)?.getAssemblyExports);
  await page.waitForFunction(() => document.querySelector('.sharprail-window canvas')?.width > 100);
  console.log('Browser runtime loaded');
  if (process.env.SHARPRAIL_BROWSER_BUNDLE) {
    assert.equal(await page.evaluate(async () => {
      const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll');
      return !!exports.SharpRail?.Browser?.Checks;
    }), false, 'The deployment bundle must exclude acceptance hooks.');
    await page.waitForFunction(() => document.querySelector('.sharprail-workbench canvas'));
    assert.equal(await page.locator('#window-bar').count(), 0);
    assert.equal(await page.locator('.sharprail-workbench').evaluate(element => element.getBoundingClientRect().top), 0);
    assert.match(await page.evaluate(async () => (await import('./sharprail.js')).renderMermaid('flowchart LR\nA[Published] --> B[Host]', '{}')), /<svg.*Published/s);
    assert.deepEqual(errors, []);
    console.log('PASS published browser bundle, full-viewport workbench, worker rendering and excluded test hooks');
  } else {
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('SharpRail.UI.dll'); globalThis.check = exports.SharpRail.Browser.Checks.BrowserChecks; });
  const command = process.platform === 'darwin' ? 'Meta' : 'Control';
  const fields = async () => page.evaluate(async () => JSON.parse(await check.ConnectFields()));
  await page.waitForFunction(() => document.querySelector('.sharprail-window canvas')?.width > 100);
  await until(fields, fields => fields.length === 2 && fields[0].Position?.Y > 0, 'The connect form did not appear.');
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write'], { origin: address });
  const clipboard = await page.evaluate(() => navigator.clipboard.readText());
  try {
    for (const [name, value] of [['ConnectAddress', address], ['ConnectToken', token]]) {
      await until(async () => {
        const field = (await fields()).find(field => field.Name === name);
        await page.mouse.click(field.Position.X + 20, field.Position.Y + 15);
        return (await fields()).find(field => field.Name === name);
      }, field => field.IsFocused, 'The connection field did not receive browser focus.');
      await page.keyboard.type('replace this');
      await page.keyboard.press(`${command}+a`);
      await until(fields, fields => { const field = fields.find(field => field.Name === name); return Math.abs(field.SelectionEnd - field.SelectionStart) === field.Text.length; }, 'Command+A did not select the field.');
      await page.evaluate(value => navigator.clipboard.writeText(value), value);
      await page.keyboard.press(`${command}+v`);
      await until(fields, fields => fields.find(field => field.Name === name)?.Text === value, 'Command+V did not paste into the field.');
    }
  } finally { await page.evaluate(value => navigator.clipboard.writeText(value), clipboard); }
  await page.keyboard.press('Enter');
  console.log('PASS native platform Select All and Paste shortcuts in browser connection fields');
  const snapshot = async () => {
    let timer;
    try { return await Promise.race([page.evaluate(async () => JSON.parse(await check.Snapshot())), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Browser dispatcher stopped responding: ' + errors.join('; '))), 5000); })]); }
    finally { clearTimeout(timer); }
  };
  let windows = await until(snapshot, windows => windows[0]?.ProjectRoot === fixture && windows[0].Terminals[0]?.Text.includes('fixture'), 'The workbench did not restore its host terminal.');
  await page.evaluate(() => check.SetTabMode(true, true));
  windows = await until(snapshot, windows => windows[0]?.VerticalCenterTabs && windows[0]?.VerticalTabsInProjects &&
    windows[0]?.TerminalRegions.length > 0 && windows[0].TerminalRegions.every(region => region === 'center') &&
    windows[0].Terminals[0]?.Text.includes('fixture'), 'Host Projects mode did not repair the browser terminal placement.');
  console.log('PASS shared host tab mode restores legacy browser bottom terminals into Projects/center');
  await page.evaluate(() => check.SetTabMode(false, false));
  await until(snapshot, windows => !windows[0]?.VerticalCenterTabs && !windows[0]?.VerticalTabsInProjects,
    'A live host tab mode change did not reach the browser.');
  await page.evaluate(() => check.CheckTerminalDrawing());
  console.log('PASS batched terminal drawing matches the existing renderer pixel for pixel');
  const terminal = windows[0].Terminals[0];
  await page.mouse.click(terminal.Position.X + 40, terminal.Position.Y + 48);
  await page.keyboard.type("printf 'browser-%s\\n' vt"); await page.keyboard.press('Enter');
  await until(snapshot, windows => windows[0].Terminals[0].Text.includes('browser-vt'), 'Host PTY output did not reach the WASM terminal.');
  await page.keyboard.type("printf '\\033[31mRED\\033[32mGREEN\\033[0m\\n'; stty size"); await page.keyboard.press('Enter');
  windows = await until(snapshot, windows => {
    const terminal = windows[0].Terminals[0];
    return terminal.Text.includes('REDGREEN') && terminal.Frame.Colors.filter(color => color !== 0).length >= 2 &&
      terminal.Text.split('\n').some(line => line.trim() === `${terminal.Grid.Rows} ${terminal.Grid.Columns}`);
  }, 'ANSI colors or the host PTY dimensions do not match the WASM terminal.');
  assert.equal(windows[0].Terminals[0].Frame.Columns, windows[0].Terminals[0].Grid.Columns);
  assert.equal(windows[0].Terminals[0].Frame.Rows, windows[0].Terminals[0].Grid.Rows);
  console.log('PASS ANSI colors and matching browser, VT and host PTY dimensions');
  disconnected = true;
  for (const { route, server } of sockets) { route.close({ code: 1012, reason: 'Acceptance network interruption' }); server.close({ code: 1012, reason: 'Acceptance network interruption' }); }
  await new Promise(resolve => setTimeout(resolve, 500)); disconnected = false;
  await new Promise(resolve => setTimeout(resolve, 1500));
  await page.keyboard.type("printf 'browser-%s\\n' reconnected"); await page.keyboard.press('Enter');
  await until(snapshot, windows => windows[0].Terminals[0].Text.includes('browser-reconnected'), 'The browser terminal did not reconnect to its host shell.');
  await page.evaluate(() => check.OpenDocument('hello.txt'));
  windows = await until(snapshot, windows => windows[0].Editors[0]?.Recordings > 0, 'Scintilla did not record the host file.');
  const editor = windows[0].Editors[0];
  await page.mouse.click(editor.Position.X + 100, editor.Position.Y + 40);
  await page.keyboard.press('End'); await page.keyboard.type(' edited in WASM');
  await until(snapshot, windows => windows[0].Editors[0].Text.includes('edited in WASM'), 'Browser typing did not edit the document.');
  await page.keyboard.press(`${command}+s`);
  await until(() => fs.readFile(path.join(fixture, 'hello.txt'), 'utf8'), text => text.includes('edited in WASM'), 'Saving did not update the host file.');
  await page.keyboard.type(' local conflict');
  await until(snapshot, windows => windows[0].Editors[0].IsModified, 'The browser document did not become dirty.');
  await fs.writeFile(path.join(fixture, 'hello.txt'), 'External host edit\n');
  await page.keyboard.press(`${command}+s`);
  await until(snapshot, windows => windows[0].Messages.some(message => message?.includes('changed on disk')), 'The browser did not report the save conflict.');
  assert.equal(await fs.readFile(path.join(fixture, 'hello.txt'), 'utf8'), 'External host edit\n');
  assert.ok((await snapshot())[0].Editors[0].Text.includes('local conflict'));
  console.log('PASS browser Scintilla input/save/conflict and Ghostty host PTY reconnect');

  for (const [encoded, width, height, text] of [[pdfFixture(), 200, 120, 'Browser PDF fixture'], [pdfFixture(90), 120, 200, 'Browser PDF fixture'], [pdfFixture(0, '10 20 180 110'), 170, 90, 'Browser PDF fixture'], [pdfFixture(0, '', true), 200, 120, '🚀é']]) {
    const preview = await page.evaluate(async bytes => {
      const renderer = await import('./sharprail.js');
      const pages = JSON.parse(await renderer.readPdf(bytes));
      const pixels = JSON.parse(await renderer.renderPdf(bytes, 2));
      return { pages, pixels: pixels.map(page => ({ Width: page.Width, Height: page.Height, Bytes: atob(page.Pixels).length, Dark: [...atob(page.Pixels)].some(value => value.charCodeAt(0) !== 255) })) };
    }, encoded.toString('base64'));
    assert.equal(preview.pages[0].Width, width); assert.equal(preview.pages[0].Height, height);
    assert.ok(preview.pages[0].Text.includes(text)); assert.equal(preview.pages[0].Boxes.length, preview.pages[0].Text.length);
    assert.equal(preview.pixels[0].Width, width * 2); assert.equal(preview.pixels[0].Height, height * 2); assert.ok(preview.pixels[0].Dark);
  }
  assert.match(await page.evaluate(async () => (await import('./sharprail.js')).renderMermaid('flowchart LR\nA[Browser] --> B[Host]', '{}')), /<svg.*Browser/s);
  await page.evaluate(() => check.OpenDocument('sample.pdf')); await page.evaluate(() => check.OpenDocument('diagram.md'));
  console.log('PASS browser PDFium text, Unicode, rotation/crop, pixels and Mermaid worker');
  await page.evaluate(() => check.OpenSettings('Plugins'));
  await page.waitForFunction(() => document.querySelector('.sharprail-workbench').inert);
  assert.equal(await page.locator('.sharprail-dialog').evaluateAll(dialogs => dialogs.filter(dialog => dialog.style.visibility === 'visible').length), 1);
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !document.querySelector('.sharprail-workbench').inert);
  console.log('PASS browser modal plugin settings and keyboard dismissal');
  await page.evaluate(() => check.EnablePlugin('fixture'));
  await until(snapshot, windows => windows[0].Plugins.includes('fixture'), 'The browser did not load the external managed plugin UI.');
  await page.evaluate(() => check.OpenSettings('plugin:fixture:fixture'));
  await until(() => page.evaluate(async () => JSON.parse(await check.SettingsText())), texts => texts.includes('hello'), 'The external plugin settings did not mount.');
  await page.evaluate(() => check.ClickSettingsButton('FixtureGreet'));
  await until(() => page.evaluate(async () => JSON.parse(await check.SettingsText())), texts => texts.includes('hey'), 'The external plugin could not update host settings.');
  assert.match(await fs.readFile(path.join(scratch, 'state/state.json'), 'utf8'), /hey/);
  await page.keyboard.press('Escape');
  console.log('PASS browser external assembly loading, plugin UI activation and host settings persistence');

  assert.ok(await page.evaluate(() => JSON.parse(localStorage.getItem('sharprail:profile')).Windows.length > 0));
  assert.deepEqual(errors, []);
  console.log('PASS browser profile persistence');
  }
} finally {
  await browser?.close();
  if (host && host.exitCode === null && host.signalCode === null) { const stopped = once(host, 'exit'); host.kill('SIGINT'); await stopped; }
  await fs.rm(scratch, { recursive: true, force: true });
}
