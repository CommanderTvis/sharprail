import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = path.join(root, 'src/SharpRail.Browser');
const vendor = path.join(project, 'wwwroot/vendor');
const cache = path.join(root, '.tools/pdfium-web');
await fs.mkdir(vendor, { recursive: true });
await fs.mkdir(cache, { recursive: true });
await fs.cp(path.join(root, 'licenses'), path.join(vendor, 'licenses'), { recursive: true });
await fs.copyFile(path.join(root, 'THIRD-PARTY-NOTICES.md'), path.join(vendor, 'THIRD-PARTY-NOTICES.md'));
for (const dependency of ['Ghostty.Avalonia', 'SharpRail.Scintilla'])
  await fs.cp(path.join(root, 'src', dependency, 'Licenses'), path.join(vendor, 'licenses', dependency), { recursive: true });
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
catch { execFileSync('npm', ['ci', '--ignore-scripts', '--prefix', project], { stdio: 'inherit' }); }
for (const name of ['merman_wasm.js', 'merman_wasm_bg.wasm'])
  await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/pkg', name), path.join(vendor, name));

await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/LICENSE'), path.join(vendor, 'Merman-LICENSE'));
await fs.cp(path.join(project, 'node_modules/restty/dist'), path.join(vendor, 'restty'), { recursive: true });
await fs.copyFile(path.join(project, 'node_modules/restty/LICENSE'), path.join(vendor, 'Restty-LICENSE'));
await fs.copyFile(path.join(project, 'node_modules/text-shaper/LICENSE'), path.join(vendor, 'TextShaper-LICENSE'));
await fs.copyFile(path.join(root, 'src/SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf'), path.join(vendor, 'JetBrainsMono-Regular.ttf'));
const archive = path.join(cache, 'pdfium-wasm.tgz');
let bytes;
try { bytes = await fs.readFile(archive); }
catch {
  const response = await fetch('https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8076/pdfium-wasm.tgz');
  if (!response.ok) throw new Error(`PDFium download failed: ${response.status}`);
  bytes = Buffer.from(await response.arrayBuffer());
  await fs.writeFile(archive, bytes);
}
if (createHash('sha256').update(bytes).digest('hex') !== 'f0e18e471c0f40e769ff796a7a03955835f9782be4fd119c15349cc91b458f21')
  throw new Error('Unexpected PDFium archive checksum.');
execFileSync('tar', ['-xzf', archive, '-C', cache]);
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
