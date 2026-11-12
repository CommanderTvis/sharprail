// Headless checks of tailscale.js: the IndexedDB-backed node state through a fake backend, and the lazy download into a
// fake Cache Storage (progress, cancel, incomplete bodies, versioned cache names). No browser and no network.
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const tailscale = await import(pathToFileURL(path.join(root, 'src/SharpRail.Browser/wwwroot/tailscale.js')).href);

// State: synchronous reads for Go, ordered write-through, reload restores identity, sign-out wipes it.
const persisted = new Map();
const backend = {
  async getAll() { return [...persisted]; },
  async put(key, value) { await new Promise(resolve => setTimeout(resolve, 1)); persisted.set(key, value); },
  async clear() { persisted.clear(); },
};
const first = tailscale.createStateStore(backend);
await first.load();
assert.equal(first.getState('_machinekey'), '', 'A new browser has no identity.');
first.setState('_machinekey', 'aa');
first.setState('profile', 'bb');
first.setState('_machinekey', 'cc');
assert.equal(first.getState('_machinekey'), 'cc', 'Writes are visible synchronously.');
await first.flushed();
assert.deepEqual([...persisted], [['_machinekey', 'cc'], ['profile', 'bb']], 'Writes persist in order; the last one wins.');
const reloaded = tailscale.createStateStore(backend);
await reloaded.load();
assert.equal(reloaded.getState('_machinekey'), 'cc', 'A later visit rejoins as the same device.');
await reloaded.wipe();
assert.equal(persisted.size, 0, 'Sign-out wipes the persisted identity.');
assert.equal(reloaded.getState('profile'), '', 'Sign-out wipes the in-memory identity.');

// Download: a fake same-origin fetch and Cache Storage.
const manifest = { build: '1.104.1-abc', cacheName: 'sharprail-tailscale-1.104.1-abc', module: 'tailscale/m.wasm', exec: 'tailscale/wasm_exec.js', size: 6 };
function fakeCaches() {
  const stores = new Map();
  return {
    stores,
    async has(name) { return stores.has(name); },
    async open(name) {
      if (!stores.has(name)) stores.set(name, new Map());
      const store = stores.get(name);
      return { async match(url) { return store.get(url); }, async put(url, response) { store.set(url, response); } };
    },
  };
}
function fakeFetch(chunks, { status = 200 } = {}) {
  return async (url, { signal }) => {
    if (url.endsWith('wasm_exec.js')) return new Response('globalThis.Go = class {};');
    let index = 0;
    const body = new ReadableStream({
      async pull(controller) {
        await new Promise(resolve => setTimeout(resolve, 1));
        if (signal?.aborted) { controller.error(signal.reason); return; }
        if (index < chunks.length) controller.enqueue(new Uint8Array(chunks[index++])); else controller.close();
      },
    });
    return new Response(body, { status });
  };
}
let caches = fakeCaches();
assert.equal(await tailscale.isCachedIn(caches, manifest), false, 'Nothing is cached before the download.');
const progress = [];
await tailscale.downloadInto({ fetch: fakeFetch([[1, 2], [3, 4, 5, 6]]), caches, manifest, onProgress: bytes => progress.push(bytes) });
assert.deepEqual(progress, [2, 6], 'Progress counts decoded module bytes.');
assert.equal(await tailscale.isCachedIn(caches, manifest), true, 'A complete download is cached under its build.');
assert.equal(await tailscale.isCachedIn(caches, { ...manifest, build: 'next', cacheName: 'sharprail-tailscale-next' }), false, 'Another build is not served from this cache.');

caches = fakeCaches();
const controller = new AbortController();
await assert.rejects(tailscale.downloadInto({ fetch: fakeFetch([[1], [2], [3], [4], [5], [6]]), caches, manifest, signal: controller.signal, onProgress: () => controller.abort() }));
assert.equal(await tailscale.isCachedIn(caches, manifest), false, 'A cancelled download caches nothing.');

await assert.rejects(tailscale.downloadInto({ fetch: fakeFetch([[1, 2]]), caches, manifest, onProgress: () => {} }), /incomplete/);
assert.equal(await tailscale.isCachedIn(caches, manifest), false, 'A truncated download caches nothing.');
await assert.rejects(tailscale.downloadInto({ fetch: fakeFetch([], { status: 404 }), caches, manifest, onProgress: () => {} }), /404/);

assert.deepEqual(tailscale.fromBase64(tailscale.toBase64(new Uint8Array([0, 255, 128, 7]))), new Uint8Array([0, 255, 128, 7]), 'Stream bytes survive the base64 bridge.');
console.log('PASS tailscale.js: IndexedDB state persistence and wipe, lazy download, progress, cancel, truncation and cache versioning');
