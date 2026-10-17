import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const scratch = path.join(root, '.bench/wasm-compression');
const bytes = Buffer.from('Browser assets λ\n'.repeat(4096));
const cache = path.join(scratch, '.cache');
const compress = () => execFileSync(process.execPath, [path.join(root, 'scripts/browser-compress.mjs'), scratch, cache], { stdio: 'inherit' });
try {
  await fs.mkdir(path.join(scratch, '_framework'), { recursive: true });
  const file = path.join(scratch, '_framework/runtime.wasm');
  await fs.writeFile(file, bytes);
  await fs.writeFile(path.join(scratch, 'small.js'), 'small');
  await fs.writeFile(path.join(scratch, 'image.png'), bytes);
  compress();
  for (const [suffix, decode] of [['br', brotliDecompressSync], ['gz', gunzipSync]]) {
    const packed = await fs.readFile(`${file}.${suffix}`);
    assert(packed.length < bytes.length);
    assert.deepEqual(decode(packed), bytes);
  }
  const before = (await fs.stat(`${file}.br`)).mtimeMs;
  compress();
  assert.equal((await fs.stat(`${file}.br`)).mtimeMs, before, 'Unchanged assets must reuse compression.');
  const cachedFile = path.join(cache, (await fs.readdir(cache)).find(name => name.endsWith('.br11')));
  const cachedTime = (await fs.stat(cachedFile)).mtimeMs;
  await fs.rm(`${file}.br`);
  compress();
  assert.deepEqual(brotliDecompressSync(await fs.readFile(`${file}.br`)), bytes);
  assert.equal((await fs.stat(cachedFile)).mtimeMs, cachedTime, 'Regenerated bundles must preserve the content cache.');
  for (const name of ['small.js.br', 'small.js.gz', 'image.png.br', 'image.png.gz'])
    await assert.rejects(fs.access(path.join(scratch, name)), { code: 'ENOENT' });
  const changed = Buffer.from('Changed browser bundle\n'.repeat(4096));
  await fs.writeFile(file, changed);
  await fs.utimes(file, new Date(), new Date(Date.now() + 1000));
  compress();
  assert.deepEqual(brotliDecompressSync(await fs.readFile(`${file}.br`)), changed);
  await fs.writeFile(file, 'tiny');
  await fs.utimes(file, new Date(), new Date(Date.now() + 2000));
  compress();
  await assert.rejects(fs.access(`${file}.br`), { code: 'ENOENT' });
  await assert.rejects(fs.access(`${file}.gz`), { code: 'ENOENT' });
  console.log('PASS bundle compression, exact decode, incremental reuse, changed inputs and identity fallbacks');
} finally {
  await fs.rm(scratch, { recursive: true, force: true });
}
