import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = path.join(root, 'src/SharpRail.Browser');
const vendor = path.join(project, 'wwwroot/vendor');
await fs.mkdir(vendor, { recursive: true });
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
catch { execFileSync('pnpm', ['install', '--frozen-lockfile', '--ignore-scripts', '--dir', project], { stdio: 'inherit' }); }
for (const name of ['merman_wasm.js', 'merman_wasm_bg.wasm'])
  await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/pkg', name), path.join(vendor, name));

await fs.copyFile(path.join(project, 'node_modules/@mermanjs/web/LICENSE'), path.join(vendor, 'Merman-LICENSE'));
await fs.cp(path.join(project, 'node_modules/restty/dist'), path.join(vendor, 'restty'), { recursive: true });
await fs.copyFile(path.join(project, 'node_modules/restty/LICENSE'), path.join(vendor, 'Restty-LICENSE'));
await fs.copyFile(path.join(await fs.realpath(path.join(project, 'node_modules/restty')), '../text-shaper/LICENSE'), path.join(vendor, 'TextShaper-LICENSE'));
await fs.copyFile(path.join(root, 'src/SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf'), path.join(vendor, 'JetBrainsMono-Regular.ttf'));
// PDFium's WASM build is pinned and checksummed in mise.browser.toml and linked by `scripts/mise run bootstrap:browser`.
const cache = path.join(root, '.tools/pdfium-wasm');
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
