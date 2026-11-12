// A git command line for the tab host, answered by isomorphic-git: the subset of commands, flags and output formats the
// host itself issues. Anything else exits 128 and says so; nothing here starts a process or reaches a network.
import { structuredPatch } from '../vendor/diff/index.js';

const AUTHOR = { name: 'SharpRail', email: 'tab@sharprail.local' };
const encoder = new TextEncoder();
const decoder = new TextDecoder();

class Exit extends Error {
  constructor(code, message = '') { super(message); this.code = code; }
}
const fatal = message => { throw new Exit(128, 'fatal: ' + message + '\n'); };

export function createGit(git, fs, FS, http, corsProxy) {
  const text = value => encoder.encode(value);
  const exists = path => { try { FS.stat(path); return true; } catch { return false; } };
  const join = (a, b) => (b.startsWith('/') ? b : (a === '/' ? '' : a) + '/' + b);

  function normalize(path) {
    const out = [];
    for (const part of path.split('/')) {
      if (part === '' || part === '.') continue;
      if (part === '..') out.pop(); else out.push(part);
    }
    return '/' + out.join('/');
  }

  function repositoryRoot(cwd) {
    let dir = normalize(cwd);
    for (;;) {
      if (exists(dir + '/.git')) return dir;
      if (dir === '/') return null;
      dir = dir.slice(0, dir.lastIndexOf('/')) || '/';
    }
  }

  const relative = (root, cwd, path) => {
    const full = normalize(join(cwd, path));
    return full === root ? '' : full.startsWith(root + '/') ? full.slice(root.length + 1) : fatal(`${path}: is outside repository at '${root}'`);
  };

  async function resolve(dir, spec) {
    let rev = spec.replace(/\^\{commit\}$/, '');
    let parents = 0;
    while (rev.endsWith('^')) { parents++; rev = rev.slice(0, -1); }
    let oid = null;
    if (/^[0-9a-f]{4,40}$/.test(rev)) { try { oid = await git.expandOid({ fs, dir, oid: rev }); } catch { /* not an id */ } }
    if (!oid) {
      for (const ref of [rev, 'refs/heads/' + rev, 'refs/tags/' + rev, 'refs/remotes/' + rev]) {
        try { oid = await git.resolveRef({ fs, dir, ref }); break; } catch { /* next */ }
      }
    }
    for (let i = 0; oid && i < parents; i++) {
      try { oid = (await git.readCommit({ fs, dir, oid })).commit.parent[0] ?? null; } catch { oid = null; }
    }
    return oid;
  }

  async function collect(dir, tree) {
    const entries = new Map();
    await git.walk({
      fs, dir, trees: [tree],
      map: async (path, [entry]) => {
        if (path === '.' || !entry) return undefined;
        if ((await entry.type()) === 'blob') entries.set(path, { oid: await entry.oid(), mode: await entry.mode() });
        return undefined;
      }
    });
    return entries;
  }

  const treeAt = async (dir, oid) => oid ? collect(dir, git.TREE({ ref: oid })) : new Map();
  const indexEntries = dir => collect(dir, git.STAGE());

  const blobSource = (dir, entries) => ({
    paths: () => new Set(entries.keys()),
    info: async path => {
      const entry = entries.get(path);
      if (!entry) return null;
      return { oid: entry.oid, mode: entry.mode, bytes: async () => (await git.readBlob({ fs, dir, oid: entry.oid })).blob };
    }
  });

  const workSource = (dir, candidates) => ({
    paths: () => candidates,
    info: async path => {
      const full = dir + '/' + path;
      let info;
      try { info = FS.stat(full); } catch { return null; }
      if (!FS.isFile(info.mode)) return null;
      const data = FS.readFile(full);
      const { oid } = await git.hashBlob({ object: data });
      return { oid, mode: info.mode & 0o111 ? 0o100755 : 0o100644, bytes: async () => data };
    }
  });

  const matches = (path, specs) => specs.length === 0 || specs.some(spec => spec === '' || path === spec || path.startsWith(spec.endsWith('/') ? spec : spec + '/'));
  const isBinary = bytes => bytes.subarray(0, 8000).includes(0);

  async function compare(from, to, specs) {
    const names = [...new Set([...from.paths(), ...to.paths()])].filter(path => matches(path, specs)).sort();
    const changes = [];
    for (const path of names) {
      const [before, after] = [await from.info(path), await to.info(path)];
      if (!before && !after) continue;
      if (before && after && before.oid === after.oid && before.mode === after.mode) continue;
      changes.push({ path, before, after, status: !before ? 'A' : !after ? 'D' : 'M' });
    }
    return changes;
  }

  const lines = value => value.length === 0 ? [] : value.split('\n');
  const range = (start, count) => count === 1 ? `${start}` : `${start},${count}`;

  async function patchOf(change, context) {
    const [before, after] = [change.before ? await change.before.bytes() : new Uint8Array(), change.after ? await change.after.bytes() : new Uint8Array()];
    const { path } = change;
    let out = `diff --git a/${path} b/${path}\n`;
    if (!change.before) out += `new file mode ${(change.after.mode).toString(8)}\n`;
    else if (!change.after) out += `deleted file mode ${(change.before.mode).toString(8)}\n`;
    else if (change.before.mode !== change.after.mode) out += `old mode ${change.before.mode.toString(8)}\nnew mode ${change.after.mode.toString(8)}\n`;
    const abbreviate = oid => oid.slice(0, 7);
    const left = change.before?.oid ?? '0'.repeat(40), right = change.after?.oid ?? '0'.repeat(40);
    if (left !== right) out += `index ${abbreviate(left)}..${abbreviate(right)}${change.before && change.after && change.before.mode === change.after.mode ? ' ' + change.after.mode.toString(8) : ''}\n`;
    if (isBinary(before) || isBinary(after))
      return out + `Binary files ${change.before ? 'a/' + path : '/dev/null'} and ${change.after ? 'b/' + path : '/dev/null'} differ\n`;
    if (left === right) return out;
    const patch = structuredPatch(path, path, decoder.decode(before), decoder.decode(after), '', '', { context });
    out += `--- ${change.before ? 'a/' + path : '/dev/null'}\n+++ ${change.after ? 'b/' + path : '/dev/null'}\n`;
    for (const hunk of patch.hunks) {
      out += `@@ -${range(hunk.oldStart, hunk.oldLines)} +${range(hunk.newStart, hunk.newLines)} @@\n`;
      for (const line of hunk.lines) out += line + '\n';
    }
    return out;
  }

  async function counts(change) {
    const [before, after] = [change.before ? await change.before.bytes() : new Uint8Array(), change.after ? await change.after.bytes() : new Uint8Array()];
    if (isBinary(before) || isBinary(after)) return null;
    const patch = structuredPatch('a', 'b', decoder.decode(before), decoder.decode(after), '', '', { context: 0 });
    let added = 0, removed = 0;
    for (const hunk of patch.hunks) for (const line of hunk.lines) { if (line[0] === '+') added++; else if (line[0] === '-') removed++; }
    return { added, removed };
  }

  async function status(dir) {
    const rows = await git.statusMatrix({ fs, dir });
    const out = [];
    for (const [path, head, work, stage] of rows) {
      let code;
      if (head === 0 && work === 2 && stage === 0) code = '??';
      else if (head === 0 && stage !== 0) code = work === 0 ? 'AD' : stage === 3 ? 'AM' : 'A ';
      else if (head === 1) {
        const index = stage === 0 ? 'D' : stage === 1 ? ' ' : 'M';
        const tree = work === 0 ? 'D' : stage === 1 ? (work === 2 ? 'M' : ' ') : stage === 3 ? 'M' : ' ';
        if (stage === 1 && work === 1) continue;
        code = index + tree;
      } else continue;
      out.push([code, path]);
    }
    return out.sort((a, b) => (a[1] < b[1] ? -1 : 1));
  }

  async function untracked(dir, specs) {
    return (await status(dir)).filter(([code, path]) => code === '??' && matches(path, specs)).map(([, path]) => path);
  }

  function isoDate({ timestamp, timezoneOffset }) {
    const offset = -timezoneOffset;
    const shifted = new Date((timestamp + offset * 60) * 1000).toISOString().slice(0, 19);
    const sign = offset < 0 ? '-' : '+';
    const abs = Math.abs(offset);
    return `${shifted}${sign}${String(Math.floor(abs / 60)).padStart(2, '0')}:${String(abs % 60).padStart(2, '0')}`;
  }

  function format(template, oid, commit) {
    const subject = commit.message.split('\n')[0];
    return template
      .replaceAll('%H', oid).replaceAll('%h', oid.slice(0, 7))
      .replaceAll('%cI', isoDate(commit.committer)).replaceAll('%an', commit.author.name)
      .replaceAll('%s', subject).replaceAll('%x00', '\0').replaceAll('%n', '\n');
  }

  async function reachable(dir, oid) {
    const seen = new Map();
    for (const entry of await git.log({ fs, dir, ref: oid, depth: 5000 })) seen.set(entry.oid, entry.commit);
    return seen;
  }

  // Splits an argument list at `--`, dropping the markers git itself ignores.
  function split(args) {
    const marker = args.indexOf('--');
    const before = (marker < 0 ? args : args.slice(0, marker)).filter(arg => arg !== '--end-of-options');
    return { before, paths: marker < 0 ? [] : args.slice(marker + 1) };
  }
  // Paths follow `--`, or are the plain arguments when the caller leaves the marker out.
  const pathsOf = args => args.includes('--') ? split(args).paths : args.filter(arg => !arg.startsWith('-'));
  const flagged = (list, ...names) => list.some(item => names.includes(item));

  async function head(dir) {
    try { return await git.resolveRef({ fs, dir, ref: 'HEAD' }); } catch { return null; }
  }

  async function diffSources(dir, revs, cached) {
    const staged = await indexEntries(dir);
    if (revs.length === 0) {
      return cached
        ? { from: blobSource(dir, await treeAt(dir, await head(dir))), to: blobSource(dir, staged) }
        : { from: blobSource(dir, staged), to: workSource(dir, new Set(staged.keys())) };
    }
    const first = await resolve(dir, revs[0]);
    if (!first) fatal(`bad revision '${revs[0]}'`);
    const from = blobSource(dir, await treeAt(dir, first));
    if (revs.length > 1) {
      const second = await resolve(dir, revs[1]);
      if (!second) fatal(`bad revision '${revs[1]}'`);
      return { from, to: blobSource(dir, await treeAt(dir, second)) };
    }
    if (cached) return { from, to: blobSource(dir, staged) };
    return { from, to: workSource(dir, new Set([...from.paths(), ...staged.keys()])) };
  }

  async function diff(dir, args) {
    const { before, paths } = split(args);
    const flags = before.filter(arg => arg.startsWith('-'));
    const revs = before.filter(arg => !arg.startsWith('-'));
    const context = Number((flags.find(flag => flag.startsWith('--unified=')) ?? '--unified=3').slice(10));
    const { from, to } = await diffSources(dir, revs, flags.includes('--cached'));
    const changes = await compare(from, to, paths);
    return renderChanges(changes, flags, context);
  }

  async function renderChanges(changes, flags, context) {
    if (flags.includes('--name-status')) return text(changes.map(change => `${change.status}\0${change.path}\0`).join(''));
    if (flags.includes('--numstat')) {
      let out = '';
      for (const change of changes) {
        const stat = await counts(change);
        out += `${stat ? stat.added : '-'}\t${stat ? stat.removed : '-'}\t${change.path}\0`;
      }
      return text(out);
    }
    if (flags.includes('--shortstat')) {
      let [added, removed] = [0, 0];
      for (const change of changes) { const stat = await counts(change); if (stat) { added += stat.added; removed += stat.removed; } }
      if (changes.length === 0) return text('');
      return text(` ${changes.length} file${changes.length === 1 ? '' : 's'} changed${added ? `, ${added} insertion${added === 1 ? '' : 's'}(+)` : ''}${removed ? `, ${removed} deletion${removed === 1 ? '' : 's'}(-)` : ''}\n`);
    }
    let out = '';
    for (const change of changes) out += await patchOf(change, context);
    return text(out);
  }

  async function revisionBlob(dir, cwd, root, spec) {
    const at = spec.indexOf(':');
    const revision = spec.slice(0, at);
    const path = relative(root, cwd, spec.slice(at + 1));
    const entries = revision === '' ? await indexEntries(dir) : await treeAt(dir, await resolve(dir, revision));
    const entry = entries.get(path);
    if (!entry) fatal(`path '${path}' does not exist in '${revision || 'the index'}'`);
    return (await git.readBlob({ fs, dir, oid: entry.oid })).blob;
  }

  const commands = {
    async 'rev-parse'(dir, root, cwd, args) {
      const { before } = split(args);
      if (before.includes('--show-toplevel')) return text(root + '\n');
      if (before.includes('--git-dir') || before.includes('--git-common-dir')) return text(root + '/.git\n');
      if (before.includes('--verify')) {
        const quiet = flagged(before, '--quiet', '-q');
        const spec = before.filter(arg => !arg.startsWith('-')).at(-1);
        const oid = spec ? await resolve(dir, spec) : null;
        if (!oid) throw new Exit(quiet ? 1 : 128, quiet ? '' : 'fatal: Needed a single revision\n');
        return text(oid + '\n');
      }
      const specs = before.filter(arg => !arg.startsWith('-'));
      if (specs.length === 0) fatal('unsupported rev-parse form');
      let out = '';
      for (const spec of specs) {
        const oid = await resolve(dir, spec);
        if (!oid) fatal(`ambiguous argument '${spec}': unknown revision or path not in the working tree.`);
        out += oid + '\n';
      }
      return text(out);
    },
    async 'symbolic-ref'(dir, root, cwd, args) {
      const { before } = split(args);
      const name = before.filter(arg => !arg.startsWith('-')).at(-1);
      if (name === 'HEAD') {
        const branch = await git.currentBranch({ fs, dir });
        if (!branch) throw new Exit(1, flagged(before, '-q', '--quiet') ? '' : 'fatal: ref HEAD is not a symbolic ref\n');
        return text((before.includes('--short') ? branch : 'refs/heads/' + branch) + '\n');
      }
      throw new Exit(1);
    },
    async 'for-each-ref'(dir, root, cwd, args) {
      const { before } = split(args);
      const patterns = before.filter(arg => arg.startsWith('refs/'));
      const names = [];
      if (patterns.includes('refs/heads')) names.push(...await git.listBranches({ fs, dir }));
      if (patterns.includes('refs/remotes')) {
        for (const remote of await git.listRemotes({ fs, dir }))
          for (const branch of await git.listBranches({ fs, dir, remote: remote.remote })) if (branch !== 'HEAD') names.push(`${remote.remote}/${branch}`);
      }
      return text(names.map(name => name + '\n').join(''));
    },
    async 'show-ref'(dir, root, cwd, args) {
      const ref = args.filter(arg => !arg.startsWith('-')).at(-1);
      try { return text(await git.resolveRef({ fs, dir, ref }) + ' ' + ref + '\n'); } catch { throw new Exit(1); }
    },
    async branch(dir, root, cwd, args) {
      if (args.includes('--show-current')) return text(((await git.currentBranch({ fs, dir })) ?? '') + '\n');
      if (args[0] === '-m') { await git.renameBranch({ fs, dir, ref: args[1], oldref: (await git.currentBranch({ fs, dir })) ?? args[1], checkout: true }); return text(''); }
      if (args[0] === '-D') { await git.deleteBranch({ fs, dir, ref: args[1] }); return text(''); }
      fatal('unsupported branch form');
    },
    async remote(dir) {
      return text((await git.listRemotes({ fs, dir })).map(remote => remote.remote + '\n').join(''));
    },
    async config() { throw new Exit(1); },
    async worktree(dir, root, cwd, args) {
      if (args[0] !== 'list') fatal('this host has one worktree and cannot add or remove others');
      const oid = await head(dir);
      const branch = await git.currentBranch({ fs, dir });
      return text(`worktree ${root}\0HEAD ${oid ?? '0'.repeat(40)}\0${branch ? 'branch refs/heads/' + branch : 'detached'}\0\0`);
    },
    async status(dir) {
      return text((await status(dir)).map(([code, path]) => `${code} ${path}\0`).join(''));
    },
    async 'ls-files'(dir, root, cwd, args) {
      const { before, paths } = split(args);
      const specs = paths.map(path => relative(root, cwd, path));
      const zero = before.includes('-z');
      const end = zero ? '\0' : '\n';
      if (before.includes('--stage')) {
        const entries = await indexEntries(dir);
        return text([...entries].filter(([path]) => matches(path, specs)).sort().map(([path, entry]) => `${entry.mode.toString(8)} ${entry.oid} 0\t${path}${end}`).join(''));
      }
      if (before.includes('--ignored')) return text((await ignoredEntries(dir)).map(path => path + end).join(''));
      const names = [];
      if (before.includes('--others') && !before.includes('--cached')) names.push(...await untracked(dir, specs));
      else {
        names.push(...(await git.listFiles({ fs, dir })).filter(path => matches(path, specs)));
        if (before.includes('--others')) names.push(...await untracked(dir, specs));
      }
      return text(names.map(path => path + end).join(''));
    },
    async 'check-ignore'(dir, root, cwd, args, input) {
      const { before, paths } = split(args);
      const zero = before.includes('-z');
      const candidates = before.includes('--stdin') ? decoder.decode(input ?? new Uint8Array()).split(zero ? '\0' : '\n').filter(Boolean) : paths;
      const ignored = [];
      for (const path of candidates) if (await git.isIgnored({ fs, dir, filepath: relative(root, cwd, path) })) ignored.push(path);
      if (ignored.length === 0) throw new Exit(1);
      return before.includes('-q') ? text('') : text(ignored.map(path => path + (zero ? '\0' : '\n')).join(''));
    },
    async diff(dir, root, cwd, args) { return diff(dir, args.map(arg => arg.startsWith('-') || arg === '--' ? arg : arg)); },
    async show(dir, root, cwd, args) {
      const { before } = split(args);
      const flags = before.filter(arg => arg.startsWith('-'));
      const spec = before.filter(arg => !arg.startsWith('-')).at(-1);
      const oid = await resolve(dir, spec ?? 'HEAD');
      if (!oid) fatal(`bad revision '${spec}'`);
      const { commit } = await git.readCommit({ fs, dir, oid });
      const template = flags.find(flag => flag.startsWith('--format='));
      let out = template ? format(template.slice(9), oid, commit) + '\n' : '';
      if (!flags.includes('-s')) {
        const parent = commit.parent[0];
        const changes = await compare(blobSource(dir, await treeAt(dir, parent ?? null)), blobSource(dir, await treeAt(dir, oid)), []);
        out += decoder.decode(await renderChanges(changes, [], 3));
      }
      return text(out);
    },
    async log(dir, root, cwd, args) {
      const { before } = split(args);
      const flags = before.filter(arg => arg.startsWith('-'));
      const spec = before.filter(arg => !arg.startsWith('-')).at(-1) ?? 'HEAD';
      const [bottom, top] = spec.includes('..') ? spec.split('..') : [null, spec];
      const tip = await resolve(dir, top || 'HEAD');
      if (!tip) fatal(`bad revision '${spec}'`);
      const excluded = bottom ? await reachable(dir, (await resolve(dir, bottom)) ?? tip) : new Map();
      const limit = Number((flags.find(flag => flag.startsWith('--max-count=')) ?? '--max-count=1000000').slice(12));
      const template = (flags.find(flag => flag.startsWith('--format=')) ?? '--format=%H').slice(9);
      const entries = [...await reachable(dir, tip)].filter(([oid]) => !excluded.has(oid)).slice(0, limit);
      return text(entries.map(([oid, commit]) => format(template, oid, commit) + '\n').join(''));
    },
    async 'cat-file'(dir, root, cwd, args) {
      const blob = await revisionBlob(dir, cwd, root, args.at(-1));
      return args[0] === '-s' ? text(blob.length + '\n') : blob;
    },
    async 'ls-tree'(dir, root, cwd, args) {
      const { before, paths } = split(args);
      const spec = before.filter(arg => !arg.startsWith('-')).at(-1);
      const entries = await treeAt(dir, await resolve(dir, spec));
      const specs = paths.map(path => relative(root, cwd, path));
      return text([...entries].filter(([path]) => specs.includes(path)).map(([path, entry]) => `${entry.mode.toString(8).padStart(6, '0')} blob ${entry.oid}\t${path}\0`).join(''));
    },
    async add(dir, root, cwd, args) {
      for (const path of pathsOf(args)) {
        const spec = relative(root, cwd, path);
        if (exists(join(root, spec))) await git.add({ fs, dir, filepath: spec });
        else await git.remove({ fs, dir, filepath: spec });
      }
      return text('');
    },
    async commit(dir, root, cwd, args) {
      const { before } = split(args);
      const at = before.findIndex(arg => arg === '-m' || arg === '--message');
      const message = at >= 0 ? before[at + 1] : before.find(arg => arg.startsWith('--message='))?.slice(10);
      if (message === undefined) fatal('a commit message is required (-m)');
      if (!flagged(before, '--allow-empty') && (await status(dir)).every(([code]) => code === '??' || code[0] === ' ')) {
        throw new Exit(1, 'nothing to commit\n');
      }
      const oid = await git.commit({ fs, dir, message, author: AUTHOR });
      return text(`[${(await git.currentBranch({ fs, dir })) ?? 'HEAD'} ${oid.slice(0, 7)}] ${message.split('\n')[0]}\n`);
    },
    async rm(dir, root, cwd, args) {
      for (const path of pathsOf(args)) await git.remove({ fs, dir, filepath: relative(root, cwd, path) });
      return text('');
    },
    async restore(dir, root, cwd, args) {
      const { before } = split(args);
      if (!before.includes('--staged')) fatal('restoring the working tree is not available here');
      for (const path of pathsOf(args)) await git.resetIndex({ fs, dir, filepath: relative(root, cwd, path) });
      return text('');
    }
  };

  async function ignoredEntries(dir) {
    const found = [];
    async function walkIgnored(path) {
      for (const name of FS.readdir(join(dir, path || '.')).filter(name => name !== '.' && name !== '..' && name !== '.git')) {
        const entry = path ? path + '/' + name : name;
        const directory = FS.isDir(FS.stat(join(dir, entry)).mode);
        if (await git.isIgnored({ fs, dir, filepath: entry })) found.push(directory ? entry + '/' : entry);
        else if (directory) await walkIgnored(entry);
      }
    }
    await walkIgnored('');
    return found;
  }

  async function run(cwd, argv, input) {
    // The host puts global options first: `--literal-pathspecs -c core.quotepath=false <command> ...`.
    const args = [...argv];
    while (args[0]?.startsWith('-')) args.splice(0, args[0] === '-c' ? 2 : 1);
    const name = args.shift();
    try {
      if (name === 'clone') return { exit: 0, out: await clone(normalize(cwd), args), err: '' };
      const root = repositoryRoot(cwd);
      if (!root) {
        if (name === 'rev-parse' || name === 'status' || name === 'worktree') fatal('not a git repository (or any of the parent directories): .git');
        fatal(`git ${name}: not a git repository`);
      }
      const command = commands[name];
      if (!command) fatal(`git ${name} is not available in the browser tab host`);
      const out = await command(root, root, normalize(cwd), args, input);
      return { exit: 0, out, err: '' };
    } catch (error) {
      if (error instanceof Exit) return { exit: error.code, out: new Uint8Array(), err: error.message };
      return { exit: 128, out: new Uint8Array(), err: `fatal: ${error?.message ?? error}\n` };
    }
  }

  async function clone(cwd, args) {
    const { before, paths } = split(args);
    const depthAt = before.indexOf('--depth');
    const depth = depthAt >= 0 ? Number(before[depthAt + 1]) : undefined;
    const [url, target] = paths;
    if (!url || !target) fatal('clone needs a repository and a directory');
    const dir = normalize(join(cwd, target));
    if (!/^https:\/\//.test(url)) fatal(`only https remotes can be cloned from a browser tab: ${url}`);
    if (exists(dir) && FS.readdir(dir).some(name => name !== '.' && name !== '..')) fatal(`destination path '${target}' already exists and is not an empty directory.`);
    try {
      await git.clone({ fs, http, dir, url, corsProxy, depth, singleBranch: true, noTags: true });
    } catch (error) {
      fatal(`could not clone ${url}: ${error?.message ?? error}`);
    }
    return text('');
  }

  async function initialize(directory) {
    if (!exists(directory + '/.git')) await git.init({ fs, dir: directory, defaultBranch: 'main' });
    if (await head(directory)) return 'existing';
    const walk = async path => {
      for (const name of FS.readdir(join(directory, path || '.')).filter(name => name !== '.' && name !== '..' && name !== '.git')) {
        const entry = path ? path + '/' + name : name;
        if (FS.isDir(FS.stat(join(directory, entry)).mode)) await walk(entry);
        else await git.add({ fs, dir: directory, filepath: entry });
      }
    };
    await walk('');
    await git.commit({ fs, dir: directory, message: 'Initial commit', author: AUTHOR });
    return 'created';
  }

  return { run, initialize };
}
