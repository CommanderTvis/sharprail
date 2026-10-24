// The browser's own Tailscale node, loaded only after the user asks for it. The module is fetched from this origin
// (tailscale/manifest.json names it), kept in Cache Storage per build, and run with Go's wasm_exec.js. The node's identity
// lives in IndexedDB; sign-out wipes it. Nothing here touches the DOM, so the checks run it under node with fakes.

const lockName = 'sharprail-tailscale-node';

// A synchronous map for Go's state store, written through to an asynchronous backend in order.
export function createStateStore(backend) {
  const values = new Map();
  let pending = Promise.resolve();
  const queue = work => (pending = pending.then(work, work));
  return {
    async load() { for (const [key, value] of await backend.getAll()) values.set(key, value); },
    getState: key => values.get(key) ?? '',
    setState(key, value) { values.set(key, value); queue(() => backend.put(key, value)); },
    async wipe() { values.clear(); await queue(() => backend.clear()); },
    flushed: () => pending,
  };
}

export function indexedDbBackend(indexedDB, name = 'sharprail-tailscale') {
  let database;
  const open = () => database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open(name, 1);
    request.onupgradeneeded = () => request.result.createObjectStore('state');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  const run = async (mode, action) => {
    const db = await open();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction('state', mode);
      const result = action(transaction.objectStore('state'));
      transaction.oncomplete = () => resolve(result?.result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new Error('The state write was aborted.'));
    });
  };
  return {
    async getAll() {
      const keys = await run('readonly', store => store.getAllKeys());
      const values = await run('readonly', store => store.getAll());
      return keys.map((key, index) => [key, values[index]]);
    },
    put: (key, value) => run('readwrite', store => store.put(value, key)),
    clear: () => run('readwrite', store => store.clear()),
  };
}

// Without Cache Storage (an insecure context) the download is kept for this page only.
const memory = new Map();

export async function isCachedIn(caches, manifest) {
  if (!caches) return memory.has(manifest.build);
  if (!(await caches.has(manifest.cacheName))) return false;
  const cache = await caches.open(manifest.cacheName);
  return Boolean(await cache.match(manifest.module)) && Boolean(await cache.match(manifest.exec));
}

// Fetches both files, reporting decoded module bytes; only a complete download is stored, so a cancel leaves nothing.
export async function downloadInto({ fetch, caches, manifest, signal, onProgress }) {
  const exec = await fetch(manifest.exec, { signal });
  if (!exec.ok) throw new Error(`The server answered ${exec.status} for wasm_exec.js.`);
  const execText = await exec.text();
  const response = await fetch(manifest.module, { signal });
  if (!response.ok) throw new Error(`The server answered ${response.status} for the Tailscale module.`);
  const chunks = [];
  let received = 0;
  const reader = response.body.getReader();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    received += value.length;
    onProgress(received);
  }
  signal?.throwIfAborted();
  if (received !== manifest.size) throw new Error(`The Tailscale module is incomplete (${received} of ${manifest.size} bytes).`);
  const module = new Blob(chunks, { type: 'application/wasm' });
  if (!caches) { memory.set(manifest.build, { module, execText }); return; }
  const cache = await caches.open(manifest.cacheName);
  await cache.put(manifest.exec, new Response(execText, { headers: { 'content-type': 'text/javascript' } }));
  await cache.put(manifest.module, new Response(module, { headers: { 'content-type': 'application/wasm' } }));
}

async function readCached(manifest) {
  if (!globalThis.caches) {
    const entry = memory.get(manifest.build);
    if (!entry) throw new Error('Download Tailscale first.');
    return { bytes: await entry.module.arrayBuffer(), execText: entry.execText };
  }
  const cache = await caches.open(manifest.cacheName);
  const [module, exec] = await Promise.all([cache.match(manifest.module), cache.match(manifest.exec)]);
  if (!module || !exec) throw new Error('The cached Tailscale download is missing. Download it again.');
  return { bytes: await module.arrayBuffer(), execText: await exec.text() };
}

const parse = json => {
  const manifest = JSON.parse(json);
  return { ...manifest, cacheName: `sharprail-tailscale-${manifest.build}` };
};

export async function manifest() {
  const response = await fetch('tailscale/manifest.json', { cache: 'no-store' });
  if (response.status === 404) throw new Error('This server does not offer Tailscale for the browser.');
  if (!response.ok) throw new Error(`The server answered ${response.status}.`);
  return await response.text();
}
export async function cacheNames() { return JSON.stringify(globalThis.caches ? await caches.keys() : []); }
export async function deleteCache(name) { if (globalThis.caches) await caches.delete(name); }
export function isCached(json) { return isCachedIn(globalThis.caches, parse(json)); }

let download;
let received = 0;
export function downloaded() { return received; }
export function cancelDownload() { download?.abort(); }
export async function startDownload(json) {
  const current = download = new AbortController();
  received = 0;
  try {
    await downloadInto({ fetch: globalThis.fetch.bind(globalThis), caches: globalThis.caches, manifest: parse(json), signal: current.signal, onProgress: bytes => { received = bytes; } });
  } catch (error) {
    if (current.signal.aborted) throw new Error('cancelled');
    throw error;
  } finally { if (download === current) download = undefined; }
}

let node;
let store;
let starting;
export function start(json, hostname) { return starting ??= startNode(parse(json), hostname).catch(error => { starting = undefined; throw error; }); }
async function startNode(manifest, hostname) {
  // One tab owns the identity: two nodes with one key would replace each other on the tailnet.
  if (navigator.locks) {
    const held = await new Promise(resolve => navigator.locks.request(lockName, { ifAvailable: true }, lock => {
      resolve(Boolean(lock));
      return lock ? new Promise(() => {}) : undefined;
    }));
    if (!held) throw new Error('Tailscale is already running in another SharpRail tab. Close that tab, or use it.');
  }
  const { bytes, execText } = await readCached(manifest);
  if (!globalThis.Go) {
    const url = URL.createObjectURL(new Blob([execText], { type: 'text/javascript' }));
    try { await import(url); } finally { URL.revokeObjectURL(url); }
  }
  store = createStateStore(indexedDbBackend(indexedDB));
  await store.load();
  const go = new globalThis.Go();
  const { instance } = await WebAssembly.instantiate(bytes, go.importObject);
  go.run(instance).catch(error => console.error('Tailscale stopped:', error));
  const created = globalThis.sharprailTailscale({ stateStorage: store, hostname });
  if (created instanceof Error) throw created;
  node = created;
  node.start();
}
const running = () => node ?? (() => { throw new Error('Tailscale is not running.'); })();
export function status() { return node ? node.status() : JSON.stringify({ state: 'NoState', authUrl: '', online: false, peers: [] }); }
export function login() { return running().login(); }
export async function logout() {
  await running().logout();
  await store.wipe();
}
export function dial(address, timeout) { return running().dial(address, timeout); }
export async function read(id) {
  const bytes = await running().read(id);
  return bytes === null ? null : toBase64(bytes);
}
export function write(id, base64) { return running().write(id, fromBase64(base64)); }
export function close(id) { node?.close(id); }

export function toBase64(bytes) {
  let text = '';
  for (let index = 0; index < bytes.length; index += 0x8000) text += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  return btoa(text);
}
export function fromBase64(base64) {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let index = 0; index < text.length; index++) bytes[index] = text.charCodeAt(index);
  return bytes;
}
