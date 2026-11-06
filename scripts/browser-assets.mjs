import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = path.join(root, 'src/SharpRail.Browser');
const vendor = path.join(project, 'wwwroot/vendor');
await fs.mkdir(vendor, { recursive: true });
await fs.cp(path.join(root, 'licenses'), path.join(vendor, 'licenses'), { recursive: true });
for (const file of ['LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md'])
  await fs.copyFile(path.join(root, file), path.join(vendor, file));
for (const dependency of ['Ghostty.Avalonia', 'SharpRail.Scintilla'])
  await fs.cp(path.join(root, 'src', dependency, 'licenses'), path.join(vendor, 'licenses', dependency), { recursive: true });
for (const font of ['Geist-OFL.txt', 'JetBrainsMono-OFL.txt'])
  await fs.copyFile(path.join(root, 'src/SharpRail.Plugins.UI.Kit/Assets/Fonts', font), path.join(vendor, 'licenses', font));
for (const entry of await fs.readdir(path.join(root, 'src'), { withFileTypes: true })) {
  if (!entry.isDirectory() || !entry.name.startsWith('SharpRail.Plugins.')) continue;
  const notice = path.join(root, 'src', entry.name, 'NOTICE.md');
  try { await fs.access(notice); } catch { continue; }
  await fs.copyFile(notice, path.join(vendor, 'licenses', `${entry.name}-NOTICE.md`));
}
try {
  await fs.access(path.join(project, 'node_modules/@mermanjs/web/pkg/merman_wasm.js'));
  await fs.access(path.join(project, 'node_modules/restty/dist/internal/runtime.js'));
}
catch { execFileSync('pnpm', ['install', '--frozen-lockfile', '--ignore-scripts', '--dir', project], { stdio: 'inherit' }); }
for (const name of ['merman_wasm.js', 'merman_wasm_bg.wasm'])
  await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/pkg', name), path.join(vendor, name));

await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/LICENSE'), path.join(vendor, 'Merman-LICENSE'));
// The tab host's Git: isomorphic-git's self-contained UMD bundle and jsdiff's ES modules.
await fs.copyFile(path.join(project, 'node_modules/isomorphic-git/index.umd.min.js'), path.join(vendor, 'isomorphic-git.js'));
// isomorphic-git expects a Buffer global in the page; the `buffer` package is bundled into one module that sets it.
execFileSync(path.join(project, 'node_modules/.bin/esbuild'), ['--bundle', '--format=esm', '--minify', `--outfile=${path.join(vendor, 'buffer.js')}`, '--log-level=warning'],
  { input: "import { Buffer } from 'buffer';\nglobalThis.Buffer ??= Buffer;\n", cwd: project });
await fs.copyFile(path.join(project, 'node_modules/buffer/LICENSE'), path.join(vendor, 'Buffer-LICENSE'));
// The tab host's Pi agent: pi-agent-core and pi-ai bundled with the tab's file and Git tools (pi-ai carries every provider
// SDK; the tab only speaks HTTP to them, so the Mistral SDK is stubbed) and just-bash, its shell (gzip is stubbed: no node:zlib).
execFileSync(path.join(project, 'node_modules/.bin/esbuild'), ['tabpi/agent.mjs', '--bundle', '--format=esm', '--platform=browser', '--minify',
  '--alias:@mistralai/mistralai=./tabpi/mistral-stub.mjs', '--alias:node:zlib=./tabpi/zlib-stub.mjs', '--define:process.env.NODE_ENV="production"', `--define:PI_VERSION=${JSON.stringify(JSON.stringify(JSON.parse(await fs.readFile(path.join(project, 'node_modules/@mariozechner/pi-agent-core/package.json'), 'utf8')).version))}`, `--outfile=${path.join(vendor, 'tabpi.js')}`, '--log-level=warning'], { cwd: project });
// The version alone, so asking for it (Pi's status, on every start) does not fetch the agent bundle above.
await fs.writeFile(path.join(vendor, 'tabpi-version.js'), `export const version = ${JSON.stringify(JSON.parse(await fs.readFile(path.join(project, 'node_modules/@mariozechner/pi-agent-core/package.json'), 'utf8')).version)};\n`);
await fs.copyFile(path.join(project, 'node_modules/just-bash/LICENSE'), path.join(vendor, 'JustBash-LICENSE')).catch(() => {});
// The tab host's CSS language server runs in a Web Worker: vscode-css-languageservice behind the LSP, as one classic script.
execFileSync(path.join(project, 'node_modules/.bin/esbuild'), ['tabpi/csslsp.mjs', '--bundle', '--format=iife', '--platform=browser', '--minify', `--outfile=${path.join(vendor, 'csslsp.js')}`, '--log-level=warning'], { cwd: project });
for (const [name, license] of [['VscodeCssLanguageservice', 'vscode-css-languageservice'], ['VscodeLanguageserver', 'vscode-languageserver'], ['VscodeLanguageserverTextdocument', 'vscode-languageserver-textdocument']])
  await fs.copyFile(path.join(project, 'node_modules', license, 'LICENSE.md'), path.join(vendor, `${name}-LICENSE`)).catch(() => {});
await fs.copyFile(path.join(project, 'node_modules/@mariozechner/pi-agent-core/LICENSE'), path.join(vendor, 'PiAgentCore-LICENSE')).catch(() => {});
await fs.copyFile(path.join(project, 'node_modules/isomorphic-git/http/web/index.js'), path.join(vendor, 'isomorphic-git-http.js'));
await fs.copyFile(path.join(project, 'node_modules/isomorphic-git/LICENSE.md'), path.join(vendor, 'IsomorphicGit-LICENSE'));
await fs.cp(path.join(project, 'node_modules/diff/libesm'), path.join(vendor, 'diff'), { recursive: true });
await fs.copyFile(path.join(project, 'node_modules/diff/LICENSE'), path.join(vendor, 'Diff-LICENSE'));
await fs.cp(path.join(project, 'node_modules/restty/dist'), path.join(vendor, 'restty'), { recursive: true });
// Restty 0.3.0 shrinks a glyph (both ways, so the bottom bar of "E" falls off) whenever the font's advance exceeds the cell
// it rounded to a whole pixel, e.g. 8.4 px in an 8 px cell at 14 px. Rounding is below half a pixel, so only shrink for more.
const chunk = path.join(vendor, 'restty/chunk-c0rcpsc5.js');
const shrink = 'advancePx > maxWidth && advancePx > 0';
const source = await fs.readFile(chunk, 'utf8');
if (source.split(shrink).length !== 3) throw new Error('Restty changed its glyph fit: update the patch in scripts/browser-assets.mjs');
await fs.writeFile(chunk, source.replaceAll(shrink, 'advancePx > maxWidth + 0.5 && advancePx > 0'));
await fs.copyFile(path.join(project, 'node_modules/restty/LICENSE'), path.join(vendor, 'Restty-LICENSE'));
await fs.copyFile(path.join(await fs.realpath(path.join(project, 'node_modules/restty')), '../text-shaper/LICENSE'), path.join(vendor, 'TextShaper-LICENSE'));
await fs.copyFile(path.join(root, 'src/SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf'), path.join(vendor, 'JetBrainsMono-Regular.ttf'));
// PDFium's WASM build is pinned and checksummed in mise.browser.toml; `scripts/tool pdfium-wasm` prints where mise keeps it.
const cache = execFileSync('sh', [path.join(root, 'scripts/tool'), 'pdfium-wasm'], { encoding: 'utf8' }).trim();
await fs.copyFile(path.join(cache, 'lib/pdfium.wasm'), path.join(vendor, 'pdfium.wasm'));
const glue = await fs.readFile(path.join(cache, 'lib/pdfium.js'), 'utf8');
await fs.writeFile(path.join(vendor, 'pdfium.js'), `export default function loadPdfium(options) {
return new Promise((resolve, reject) => {
var Module = { ...options, onRuntimeInitialized: () => resolve(Module), onAbort: reject };
${glue}
});
}
`);
await fs.copyFile(path.join(cache, 'LICENSE'), path.join(vendor, 'PDFium-LICENSE'));
