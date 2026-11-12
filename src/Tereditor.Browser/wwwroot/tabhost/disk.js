// The tab host's disk: the runtime's Emscripten file system, saved to IndexedDB and exposed to isomorphic-git.
const DATABASE = 'tereditor-tabhost';
const STORE = 'entries';
const SAVE_EVERY_MS = 1500;

const CODES = { 2: 'ENOENT', 13: 'EACCES', 17: 'EEXIST', 20: 'ENOTDIR', 21: 'EISDIR', 22: 'EINVAL', 39: 'ENOTEMPTY', 44: 'ENOENT', 54: 'ENOTDIR', 55: 'EEXIST', 31: 'EISDIR', 66: 'ENOTEMPTY' };

function open() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function done(transaction) {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = transaction.onabort = () => reject(transaction.error);
  });
}

export function createDisk(FS) {
  const isDirectory = mode => FS.isDir(mode);
  const manifest = new Map();
  let roots = [];
  let timer = 0;
  let saving = Promise.resolve();

  function* walk(path) {
    let names;
    try { names = FS.readdir(path); } catch { return; }
    for (const name of names) {
      if (name === '.' || name === '..') continue;
      const full = (path === '/' ? '' : path) + '/' + name;
      let info;
      try { info = FS.lstat(full); } catch { continue; }
      if (FS.isLink(info.mode)) continue;
      yield [full, info];
      if (isDirectory(info.mode)) yield* walk(full);
    }
  }

  function mkdirs(path) {
    let current = '';
    for (const part of path.split('/').filter(Boolean)) {
      current += '/' + part;
      try { FS.mkdir(current); } catch { /* exists */ }
    }
  }

  async function restore(directories) {
    roots = directories;
    for (const root of roots) mkdirs(root);
    const db = await open();
    const entries = await new Promise((resolve, reject) => {
      const rows = [];
      const request = db.transaction(STORE).objectStore(STORE).openCursor();
      request.onsuccess = () => {
        const cursor = request.result;
        if (!cursor) return resolve(rows);
        rows.push([cursor.key, cursor.value]);
        cursor.continue();
      };
      request.onerror = () => reject(request.error);
    });
    db.close();
    entries.sort((a, b) => a[0].length - b[0].length);
    for (const [path, entry] of entries) {
      if (entry.directory) mkdirs(path);
      else {
        mkdirs(path.slice(0, path.lastIndexOf('/')));
        FS.writeFile(path, entry.data);
        FS.chmod(path, entry.mode);
        FS.utime(path, entry.mtime, entry.mtime);
      }
      manifest.set(path, signature(entry.mtime, entry.directory ? -1 : entry.data.length));
    }
  }

  const signature = (mtime, size) => mtime + ':' + size;

  async function writeChanges() {
    const seen = new Set();
    const writes = [];
    for (const root of roots) {
      for (const [path, info] of walk(root)) {
        seen.add(path);
        const directory = isDirectory(info.mode);
        const mtime = info.mtime.getTime();
        const mark = signature(mtime, directory ? -1 : info.size);
        if (manifest.get(path) === mark) continue;
        if (directory) writes.push([path, { directory: true }, mark]);
        else {
          let data;
          try { data = FS.readFile(path); } catch { continue; }
          writes.push([path, { directory: false, mode: info.mode, mtime, data: data.slice() }, mark]);
        }
      }
    }
    const removed = [...manifest.keys()].filter(path => !seen.has(path) && roots.some(root => path.startsWith(root + '/')));
    if (writes.length === 0 && removed.length === 0) return;
    const db = await open();
    const transaction = db.transaction(STORE, 'readwrite');
    const store = transaction.objectStore(STORE);
    for (const [path, entry] of writes) store.put(entry, path);
    for (const path of removed) store.delete(path);
    await done(transaction);
    db.close();
    for (const [path, , mark] of writes) manifest.set(path, mark);
    for (const path of removed) manifest.delete(path);
  }

  const save = () => (saving = saving.then(writeChanges, writeChanges));

  function startSaving() {
    if (timer) return;
    timer = setInterval(() => { if (!document.hidden) save(); }, SAVE_EVERY_MS);
    document.addEventListener('visibilitychange', () => { if (document.hidden) save(); });
    addEventListener('pagehide', () => save());
  }

  const fail = error => {
    const code = CODES[error.errno] ?? error.code ?? 'EIO';
    const failure = new Error(`${code}: ${error.message ?? code}`);
    failure.code = code;
    throw failure;
  };
  const guard = action => { try { return action(); } catch (error) { return fail(error); } };
  const stats = info => ({
    type: FS.isDir(info.mode) ? 'dir' : FS.isLink(info.mode) ? 'symlink' : 'file',
    mode: info.mode, size: info.size, ino: info.ino, uid: info.uid, gid: info.gid, dev: info.dev,
    mtimeMs: info.mtime.getTime(), ctimeMs: info.ctime.getTime(),
    isFile: () => FS.isFile(info.mode), isDirectory: () => FS.isDir(info.mode), isSymbolicLink: () => FS.isLink(info.mode)
  });

  const fs = {
    promises: {
      readFile: async (path, options) => guard(() => {
        const encoding = typeof options === 'string' ? options : options?.encoding;
        return encoding ? FS.readFile(path, { encoding: 'utf8' }) : FS.readFile(path);
      }),
      writeFile: async (path, data, options) => guard(() => {
        FS.writeFile(path, typeof data === 'string' ? new TextEncoder().encode(data) : data);
        if (options?.mode) FS.chmod(path, options.mode);
      }),
      unlink: async path => guard(() => FS.unlink(path)),
      readdir: async path => guard(() => FS.readdir(path).filter(name => name !== '.' && name !== '..')),
      mkdir: async path => guard(() => FS.mkdir(path)),
      rmdir: async path => guard(() => FS.rmdir(path)),
      stat: async path => guard(() => stats(FS.stat(path))),
      lstat: async path => guard(() => stats(FS.lstat(path))),
      readlink: async path => guard(() => FS.readlink(path)),
      symlink: async (target, path) => guard(() => FS.symlink(target, path)),
      chmod: async (path, mode) => guard(() => FS.chmod(path, mode))
    }
  };

  return { fs, restore, startSaving, save };
}
