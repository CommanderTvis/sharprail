import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { brotliCompress, gzip, constants } from 'node:zlib';

const compressBr = promisify(brotliCompress);
const compressGzip = promisify(gzip);
const bundle = path.resolve(process.argv[2]);
const cache = path.resolve(process.argv[3] ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '../.tools/browser-compression'));
await fs.mkdir(cache, { recursive: true });
const extensions = new Set(['.wasm', '.dll', '.js', '.mjs', '.json', '.html', '.css', '.svg', '.dat', '.blat', '.txt']);
async function writeAtomic(file, bytes) {
  const temporary = `${file}.${process.pid}.tmp`;
  try {
    await fs.writeFile(temporary, bytes);
    await fs.rename(temporary, file);
  } finally { await fs.rm(temporary, { force: true }); }
}
let rawTotal = 0, brTotal = 0, gzipTotal = 0;
for (const entry of await fs.readdir(bundle, { recursive: true, withFileTypes: true })) {
  if (!entry.isFile() || !extensions.has(path.extname(entry.name))) continue;
  const file = path.join(entry.parentPath, entry.name);
  const bytes = await fs.readFile(file);
  const digest = createHash('sha256').update(bytes).digest('hex');
  rawTotal += bytes.length;
  for (const [suffix, compress] of [['br', () => compressBr(bytes, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } })], ['gz', () => compressGzip(bytes, { level: 9 })]]) {
    const sidecar = `${file}.${suffix}`;
    const cachedFile = path.join(cache, `${digest}.${suffix === 'br' ? 'br11' : 'gz9'}`);
    let compressed;
    let cached = false;
    try {
      const [sourceStat, compressedStat] = await Promise.all([fs.stat(file), fs.stat(sidecar)]);
      if (compressedStat.mtimeMs >= sourceStat.mtimeMs) {
        compressed = await fs.readFile(sidecar);
        cached = true;
      }
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (!compressed && bytes.length >= 1024) {
      try { compressed = await fs.readFile(cachedFile); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
    compressed ??= bytes.length >= 1024 ? await compress() : bytes;
    if (compressed.length < bytes.length) {
      if (!cached) await writeAtomic(sidecar, compressed);
      try { await fs.access(cachedFile); }
      catch (error) {
        if (error.code !== 'ENOENT') throw error;
        await writeAtomic(cachedFile, compressed);
      }
    }
    else await fs.rm(sidecar, { force: true });
    if (suffix === 'br') brTotal += Math.min(bytes.length, compressed.length);
    else gzipTotal += Math.min(bytes.length, compressed.length);
  }
}
console.log(`Browser compressible assets: ${rawTotal} bytes raw, ${brTotal} Brotli, ${gzipTotal} gzip (including identity fallbacks).`);
