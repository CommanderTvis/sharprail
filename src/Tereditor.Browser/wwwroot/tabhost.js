// The host that runs in a browser tab: its disk is the .NET runtime's own virtual file system (so System.IO and the
// agent see the same files), kept in IndexedDB between visits, and its Git is isomorphic-git behind a git command line.
import { createDisk } from './tabhost/disk.js';
import { createGit } from './tabhost/git.js';

// Git remotes send no CORS headers, so a tab clones through isomorphic-git's standard public proxy.
const CORS_PROXY = 'https://cors.isomorphic-git.org';

let disk;
let git;
let FS;
let pi;
let iso;
let web;
const sessions = new Map();

async function load() {
  if (git) return;
  const runtime = globalThis.sharprailRuntime;
  if (!runtime?.Module?.FS) throw new Error('The runtime does not expose its file system.');
  FS = runtime.Module.FS;
  disk = createDisk(FS);
  await import('./vendor/buffer.js');
  const source = await (await fetch(new URL('./vendor/isomorphic-git.js', import.meta.url))).text();
  const module = { exports: {} };
  new Function('module', 'exports', 'self', source)(module, module.exports, globalThis);
  const http = (await import('./vendor/isomorphic-git-http.js')).default;
  iso = module.exports;
  web = http;
  git = createGit(module.exports, disk.fs, runtime.Module.FS, http, CORS_PROXY);
}

/** Restores the saved files into the runtime's file system and starts saving it. */
export async function start(roots) {
  await load();
  await disk.restore(JSON.parse(roots));
  disk.startSaving();
}

/** Runs one git command line; the answer is JSON with the exit code, base64 output and the error text. */
export async function gitRun(directory, argsJson, inputBase64) {
  await load();
  const input = inputBase64 ? Uint8Array.from(atob(inputBase64), c => c.charCodeAt(0)) : null;
  const result = await git.run(directory, JSON.parse(argsJson), input);
  let binary = '';
  for (let i = 0; i < result.out.length; i += 0x8000) binary += String.fromCharCode(...result.out.subarray(i, i + 0x8000));
  return JSON.stringify({ exit: result.exit, out: btoa(binary), err: result.err });
}

/** Makes the directory a repository with one commit of whatever it holds. */
export async function initRepository(directory) {
  await load();
  return git.initialize(directory);
}

/** Writes pending changes to IndexedDB now. */
export async function save() {
  if (disk) await disk.save();
}

/** The version of the Pi bundled into the page. */
export async function piVersion() {
  return (await import('./vendor/tabpi-version.js')).version;
}

/** Starts Pi in this tab for a workspace; returns the conversation's id. */
export async function piOpen(cwd, authPath, model, thinking) {
  await load();
  pi ??= (await import('./vendor/tabpi.js')).createPi({
    FS, authPath,
    gitRun: (directory, args, input) => git.run(directory, args, input),
    ...isoGit()
  });
  const id = crypto.randomUUID();
  sessions.set(id, pi({ cwd, model: model || undefined, thinking: thinking || undefined }));
  return id;
}

const servers = new Map();

/** Starts a language server in a Web Worker; returns its id, or null for a server this page does not have. */
export function lspOpen(server) {
  if (server !== 'css') return null;
  const worker = new Worker(new URL('./vendor/csslsp.js', import.meta.url));
  const queue = [];
  const waiters = [];
  const entry = { worker, queue, waiters, closed: false };
  const deliver = value => { if (waiters.length) waiters.shift()(value); else queue.push(value); };
  worker.onmessage = event => {
    if (event.data?.sharprail === 'usage') { entry.usage?.(event.data); entry.usage = null; return; }
    deliver(JSON.stringify(event.data));
  };
  worker.onerror = () => { entry.closed = true; for (const waiter of waiters.splice(0)) waiter(null); };
  const id = crypto.randomUUID();
  servers.set(id, entry);
  return id;
}

/** Posts one JSON-RPC message to the server. */
export function lspSend(id, message) {
  servers.get(id)?.worker.postMessage(JSON.parse(message));
}

/** The next JSON-RPC message the server wrote, or null once it has stopped. */
export function lspNext(id) {
  const entry = servers.get(id);
  if (!entry) return Promise.resolve(null);
  if (entry.queue.length) return Promise.resolve(entry.queue.shift());
  if (entry.closed) return Promise.resolve(null);
  return new Promise(resolve => entry.waiters.push(resolve));
}

/** The worker's busy time and JS heap as JSON, or null when it is gone or does not answer. */
export function lspUsage(id) {
  const entry = servers.get(id);
  if (!entry || entry.closed) return Promise.resolve(null);
  return new Promise(resolve => {
    const timer = setTimeout(() => { entry.usage = null; resolve(null); }, 2000);
    entry.usage = data => { clearTimeout(timer); resolve(JSON.stringify({ busyMs: data.busyMs, heapBytes: data.heapBytes })); };
    entry.worker.postMessage({ sharprail: 'usage' });
  });
}

export function lspClose(id) {
  const entry = servers.get(id);
  if (!entry) return;
  entry.closed = true;
  entry.worker.terminate();
  for (const waiter of entry.waiters.splice(0)) waiter(null);
  servers.delete(id);
}

let shells;

/** isomorphic-git and what it needs, for the shell's own `git`. */
const isoGit = () => ({ git: iso, fs: disk.fs, http: web, corsProxy: CORS_PROXY });

/** Opens a terminal over just-bash at the directory; returns its id. */
export async function shellOpen(root) {
  await load();
  shells ??= (await import('./vendor/tabpi.js')).createTerminals({ FS, gitRun: (directory, args, input) => git.run(directory, args, input), ...isoGit() });
  return shells.open(root);
}

export function shellWrite(id, data) {
  shells?.write(id, data);
}

/** The next output the terminal wrote, or null once it has been closed. */
export async function shellNext(id) {
  return shells ? shells.next(id) : null;
}

/** What the terminal has shown so far, for a view attaching to it again. */
export function shellReplay(id) {
  return shells ? shells.replay(id) : '';
}

export function shellClose(id) {
  shells?.close(id);
}

export function piSend(id, line) {
  sessions.get(id)?.send(line);
}

/** The next line Pi wrote, or null once the conversation has ended. */
export async function piNext(id) {
  const session = sessions.get(id);
  return session ? session.next() : null;
}

export function piClose(id) {
  sessions.get(id)?.close();
  sessions.delete(id);
}
