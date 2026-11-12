// The Playground's shell: just-bash, a simulated bash, over the tab's own file system and Git. One shell serves Pi's
// `bash` tool; each terminal tab gets its own behind a small line editor. Bundled into vendor/tabpi.js by browser-assets.cs.
import { Bash, defineCommand } from 'just-bash/browser';

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const MAX_OUTPUT_CHARS = 50_000;
const REPLAY_LIMIT = 64 * 1024;
const HISTORY_LIMIT = 200;

const normalize = path => {
  const out = [];
  for (const part of path.split('/')) {
    if (part === '' || part === '.') continue;
    if (part === '..') out.pop(); else out.push(part);
  }
  return '/' + out.join('/');
};
const join = (base, path) => normalize(path.startsWith('/') ? path : base + '/' + path);

const decode = (bytes, encoding) => {
  switch (encoding) {
    case 'base64': { let s = ''; for (const b of bytes) s += String.fromCharCode(b); return btoa(s); }
    case 'hex': return [...bytes].map(b => b.toString(16).padStart(2, '0')).join('');
    case 'binary': case 'latin1': case 'ascii': return [...bytes].map(b => String.fromCharCode(b)).join('');
    default: return decoder.decode(bytes);
  }
};

/** just-bash's file system contract over the tab's Emscripten volume, so the shell sees what Files, Git and Pi see. */
function volume(FS) {
  const stat = (path, link) => {
    const s = link ? FS.lstat(path) : FS.stat(path);
    return { isFile: FS.isFile(s.mode), isDirectory: FS.isDir(s.mode), isSymbolicLink: FS.isLink(s.mode), mode: s.mode & 0o7777, size: s.size, mtime: new Date(s.mtime), ino: s.ino };
  };
  const names = dir => FS.readdir(dir).filter(name => name !== '.' && name !== '..');
  const remove = (path, recursive) => {
    if (FS.isDir(FS.lstat(path).mode)) {
      if (recursive) for (const name of names(path)) remove(path + '/' + name, true);
      FS.rmdir(path);
    } else FS.unlink(path);
  };
  const copy = (from, to, recursive) => {
    if (FS.isDir(FS.stat(from).mode)) {
      if (!recursive) throw new Error(`cp: -r not specified; omitting directory '${from}'`);
      try { FS.mkdir(to); } catch { /* exists */ }
      for (const name of names(from)) copy(from + '/' + name, to + '/' + name, true);
    } else FS.writeFile(to, FS.readFile(from));
  };
  const walk = (dir, out) => {
    for (const name of names(dir)) {
      const child = (dir === '/' ? '' : dir) + '/' + name;
      out.push(child);
      if (FS.isDir(FS.lstat(child).mode)) walk(child, out);
    }
    return out;
  };
  const bytesOf = content => typeof content === 'string' ? encoder.encode(content) : content;
  return {
    readFile: async (path, options) => decode(FS.readFile(path), typeof options === 'string' ? options : options?.encoding),
    readFileBuffer: async path => new Uint8Array(FS.readFile(path)),
    writeFile: async (path, content) => { FS.writeFile(path, bytesOf(content)); },
    appendFile: async (path, content) => {
      let existing = new Uint8Array(0);
      try { existing = FS.readFile(path); } catch { /* new file */ }
      const added = bytesOf(content);
      const all = new Uint8Array(existing.length + added.length);
      all.set(existing);
      all.set(added, existing.length);
      FS.writeFile(path, all);
    },
    exists: async path => { try { FS.stat(path); return true; } catch { return false; } },
    stat: async path => stat(path, false),
    lstat: async path => stat(path, true),
    mkdir: async (path, options) => {
      if (!options?.recursive) { FS.mkdir(path); return; }
      let current = '';
      for (const part of path.split('/').filter(Boolean)) { current += '/' + part; try { FS.mkdir(current); } catch { /* exists */ } }
    },
    readdir: async path => names(path).sort(),
    rm: async (path, options) => {
      try { remove(path, options?.recursive === true); } catch (error) { if (!options?.force) throw error; }
    },
    cp: async (from, to, options) => { copy(from, to, options?.recursive === true); },
    mv: async (from, to) => { FS.rename(from, to); },
    resolvePath: (base, path) => join(base, path),
    getAllPaths: () => walk('/', []),
    chmod: async (path, mode) => { FS.chmod(path, mode); },
    symlink: async (target, link) => { FS.symlink(target, link); },
    link: async (from, to) => { FS.writeFile(to, FS.readFile(from)); },
    readlink: async path => FS.readlink(path),
    realpath: async path => FS.lookupPath(path, { follow: true }).path,
    utimes: async (path, atime, mtime) => { FS.utime(path, atime.getTime(), mtime.getTime()); }
  };
}

const AUTHOR = { name: 'SharpRail', email: 'tab@sharprail.local' };
const GIT_USAGE = [
  'git in the Playground is isomorphic-git; supported:',
  '  init, clone <https-url> [dir], status [-s], add <path…|.|-A>, rm <path…>, commit [-a] -m <message>,',
  '  log [-n <count>] [--oneline], diff, show, branch [<name>], checkout|switch [-b] <branch>,',
  '  fetch, pull, push [<remote>] [<branch>], remote [-v], config <key> [<value>], rev-parse.',
  'Network goes through the tab’s CORS proxy; push and private pulls read GIT_USERNAME and GIT_TOKEN from the',
  'environment (export GIT_TOKEN=… first).'
].join('\n');

const ok = stdout => ({ stdout, stderr: '', exitCode: 0 });
const fail = (stderr, exitCode = 1) => ({ stdout: '', stderr, exitCode });

/** One `status --short` code for a statusMatrix row, or null for an unchanged file. */
function shortStatus([, head, workdir, stage]) {
  if (head === 0) return workdir === 0 ? null : stage === 0 ? '??' : stage === 2 ? 'A ' : 'AM';
  if (workdir === 0) return stage === 0 ? 'D ' : ' D';
  if (workdir === 1) return stage === 1 ? null : stage === 0 ? 'D ' : 'M ';
  return stage === 1 ? ' M' : stage === 2 ? 'M ' : 'MM';
}

/**
 * `git` for the shell: the everyday commands answered here with isomorphic-git in the words a person expects, and the
 * rest (diff, show, rev-parse, ls-files, …) by the tab's Git, which speaks to the host in machine formats.
 */
function gitCommand({ FS, gitRun, git, fs, http, corsProxy }) {
  const exists = path => { try { FS.stat(path); return true; } catch { return false; } };
  const repoRoot = cwd => {
    for (let dir = normalize(cwd); ; dir = dir.slice(0, dir.lastIndexOf('/')) || '/') {
      if (exists(dir + '/.git')) return dir;
      if (dir === '/') return null;
    }
  };
  const flag = (args, ...names) => { for (const name of names) { const at = args.indexOf(name); if (at >= 0) return args[at + 1]; } return undefined; };
  const branchOf = async dir => (await git.currentBranch({ fs, dir })) ?? 'HEAD';
  const delegate = async (cwd, args) => {
    const result = await gitRun(cwd, args, null);
    return { stdout: decoder.decode(result.out), stderr: result.err ?? '', exitCode: result.exit };
  };

  return defineCommand('git', async (args, context) => {
    const [sub, ...rest] = args;
    if (!sub || sub === 'help' || sub === '--help' || sub === '-h') return ok(GIT_USAGE + '\n');
    if (sub === '--version' || sub === 'version') return ok('git version 2.47.0 (isomorphic-git)\n');
    if (sub === 'clone') return delegate(context.cwd, args);
    try {
      if (sub === 'init') {
        await git.init({ fs, dir: context.cwd, defaultBranch: 'main' });
        return ok(`Initialized empty Git repository in ${context.cwd}/.git/\n`);
      }
      const dir = repoRoot(context.cwd);
      if (!dir) return fail('fatal: not a git repository (or any of the parent directories): .git\n', 128);
      const relative = path => { const full = join(context.cwd, path); return full === dir ? '.' : full.slice(dir.length + 1); };
      const paths = rest.filter(arg => !arg.startsWith('-'));
      switch (sub) {
        case 'status': {
          const rows = (await git.statusMatrix({ fs, dir })).map(row => [row[0], shortStatus(row)]).filter(([, code]) => code);
          if (rest.includes('-s') || rest.includes('--short') || rest.includes('--porcelain')) return ok(rows.map(([path, code]) => `${code} ${path}\n`).join(''));
          const lines = [`On branch ${await branchOf(dir)}`];
          if (rows.length === 0) lines.push('nothing to commit, working tree clean');
          for (const [path, code] of rows) lines.push(`\t${{ '??': 'untracked', A: 'new file', M: 'modified', D: 'deleted' }[code.trim()[0]] ?? code.trim()}: ${path}`);
          return ok(lines.join('\n') + '\n');
        }
        case 'add': {
          if (rest.includes('-A') || rest.includes('--all') || paths.includes('.')) {
            for (const [path, , workdir] of await git.statusMatrix({ fs, dir })) {
              if (workdir === 0) await git.remove({ fs, dir, filepath: path }); else await git.add({ fs, dir, filepath: path });
            }
            return ok('');
          }
          for (const path of paths) {
            const spec = relative(path);
            if (exists(join(dir, spec))) await git.add({ fs, dir, filepath: spec }); else await git.remove({ fs, dir, filepath: spec });
          }
          return ok('');
        }
        case 'rm': {
          for (const path of paths) {
            await git.remove({ fs, dir, filepath: relative(path) });
            if (!rest.includes('--cached')) try { FS.unlink(join(dir, relative(path))); } catch { /* gone */ }
          }
          return ok('');
        }
        case 'commit': {
          const message = flag(rest, '-m', '-am', '--message') ?? rest.find(arg => arg.startsWith('--message='))?.slice(10);
          if (message === undefined) return fail('error: commit needs -m <message> in the Playground\n');
          if (rest.includes('-a') || rest.includes('--all') || rest.includes('-am')) {
            for (const [path, , workdir] of await git.statusMatrix({ fs, dir })) {
              if (workdir === 0) await git.remove({ fs, dir, filepath: path }); else await git.add({ fs, dir, filepath: path });
            }
          }
          const oid = await git.commit({ fs, dir, message, author: AUTHOR });
          return ok(`[${await branchOf(dir)} ${oid.slice(0, 7)}] ${message.split('\n')[0]}\n`);
        }
        case 'log': {
          const depth = Number(flag(rest, '-n', '--max-count') ?? 20);
          const commits = await git.log({ fs, dir, depth });
          if (rest.includes('--oneline')) return ok(commits.map(({ oid, commit }) => `${oid.slice(0, 7)} ${commit.message.split('\n')[0]}\n`).join(''));
          return ok(commits.map(({ oid, commit }) => `commit ${oid}\nAuthor: ${commit.author.name} <${commit.author.email}>\nDate:   ${new Date(commit.author.timestamp * 1000).toUTCString()}\n\n    ${commit.message.trimEnd().split('\n').join('\n    ')}\n`).join('\n'));
        }
        case 'branch': {
          if (rest.includes('--show-current')) return ok(await branchOf(dir) + '\n');
          const name = paths[0];
          if (rest.includes('-D') || rest.includes('-d')) { await git.deleteBranch({ fs, dir, ref: paths[0] }); return ok(`Deleted branch ${paths[0]}\n`); }
          if (name) { await git.branch({ fs, dir, ref: name }); return ok(''); }
          const current = await branchOf(dir);
          return ok((await git.listBranches({ fs, dir })).map(branch => `${branch === current ? '*' : ' '} ${branch}\n`).join(''));
        }
        case 'checkout':
        case 'switch': {
          const create = rest.includes('-b') || rest.includes('-c');
          const ref = paths[0];
          if (!ref) return fail(`error: ${sub} needs a branch\n`);
          if (create) await git.branch({ fs, dir, ref, checkout: true }); else await git.checkout({ fs, dir, ref });
          return ok(`Switched to ${create ? 'a new ' : ''}branch '${ref}'\n`);
        }
        case 'fetch':
        case 'pull':
        case 'push': {
          const remote = paths[0] ?? 'origin';
          const ref = paths[1] ?? await branchOf(dir);
          if (!(await git.listRemotes({ fs, dir })).some(entry => entry.remote === remote)) return fail(`fatal: '${remote}' does not appear to be a git repository (no such remote)\n`, 128);
          const token = context.env.get('GIT_TOKEN');
          const network = { fs, http, dir, remote, ...(corsProxy ? { corsProxy } : {}), ...(token ? { onAuth: () => ({ username: context.env.get('GIT_USERNAME') ?? token, password: token }) } : {}) };
          if (sub === 'fetch') {
            const result = await git.fetch({ ...network, ref, singleBranch: true });
            return ok(`From ${remote}\n * ${ref} -> ${result.fetchHead?.slice(0, 7) ?? 'nothing new'}\n`);
          }
          if (sub === 'pull') {
            const before = await git.resolveRef({ fs, dir, ref: 'HEAD' }).catch(() => null);
            await git.pull({ ...network, ref, singleBranch: true, author: AUTHOR });
            const after = await git.resolveRef({ fs, dir, ref: 'HEAD' });
            return ok(before === after ? 'Already up to date.\n' : `Updating ${before?.slice(0, 7) ?? '(none)'}..${after.slice(0, 7)}\n`);
          }
          const pushed = await git.push({ ...network, ref });
          return pushed.ok ? ok(`To ${remote}\n   ${ref} -> ${ref}\n`) : fail(`error: failed to push ${ref}: ${pushed.error ?? 'rejected'}\n`);
        }
        case 'remote': {
          const remotes = await git.listRemotes({ fs, dir });
          return ok(remotes.map(({ remote, url }) => rest.includes('-v') ? `${remote}\t${url} (fetch)\n${remote}\t${url} (push)\n` : `${remote}\n`).join(''));
        }
        case 'config': {
          const [key, value] = paths;
          if (!key) return fail('usage: git config <key> [<value>]\n');
          if (value !== undefined) { await git.setConfig({ fs, dir, path: key, value }); return ok(''); }
          const current = await git.getConfig({ fs, dir, path: key });
          return current === undefined ? fail('', 1) : ok(`${String(current)}\n`);
        }
        default:
          return delegate(context.cwd, args);
      }
    } catch (error) {
      return fail(`fatal: ${error?.message ?? error}\n`, 128);
    }
  });
}

/** A just-bash shell over the volume, starting in `root`. */
export function createShell(host, root) {
  const { FS } = host;
  for (const dir of ['/tmp', '/dev']) { try { FS.mkdir(dir); } catch { /* exists */ } }
  try { FS.stat('/dev/null'); } catch { FS.writeFile('/dev/null', new Uint8Array(0)); }
  return new Bash({
    fs: volume(FS),
    cwd: root,
    env: { HOME: root, PWD: root, USER: 'playground' },
    customCommands: [gitCommand(host)]
  });
}

const clip = value => value.length <= MAX_OUTPUT_CHARS ? value : `${value.slice(0, MAX_OUTPUT_CHARS)}\n[output truncated: ${value.length} characters]`;

/** Pi's `bash` tool: one command at a time through the workspace's shell. */
export function bashTool(host, root, Type) {
  const shell = createShell(host, root);
  return {
    name: 'bash', label: 'Bash',
    description: 'Run a bash command in the workspace (a simulated shell in the browser: coreutils, grep/rg, sed, awk, find, jq, pipes and redirections, and git). It has no other programs, no network tools and no node.',
    parameters: Type.Object({ command: Type.String({ description: 'The bash command to run' }) }),
    execute: async (_id, { command }) => {
      const result = await shell.exec(command, { cwd: root });
      const output = clip([result.stdout, result.stderr].filter(Boolean).join('\n').trimEnd());
      if (result.exitCode !== 0) throw new Error(`${output}${output ? '\n\n' : ''}Command exited with code ${result.exitCode}`);
      return { content: [{ type: 'text', text: output || '(no output)' }], details: {} };
    }
  };
}

const crlf = value => value.replace(/\r?\n/g, '\r\n');

/** Terminals for the tab: a line editor (echo, backspace, Enter, Ctrl+C, Ctrl+L, history) in front of one shell per terminal. */
export function createTerminals(host) {
  const sessions = new Map();

  function open(root, name = 'playground') {
    const bash = createShell(host, root);
    const chunks = [];
    const waiters = [];
    const session = { cwd: root, env: { HOME: root, PWD: root, USER: 'playground', TERM: 'xterm-256color' }, line: '', history: [], index: 0, running: false, closed: false, replay: '' };
    const output = data => {
      session.replay = (session.replay + data).slice(-REPLAY_LIMIT);
      if (waiters.length) waiters.shift()(data); else chunks.push(data);
    };
    const prompt = () => {
      const place = session.cwd === root ? '~' : session.cwd.startsWith(root + '/') ? '~/' + session.cwd.slice(root.length + 1) : session.cwd;
      output(`\x1b[32m${name}\x1b[0m:\x1b[34m${place}\x1b[0m$ `);
    };
    const replaceLine = next => { output('\b \b'.repeat(session.line.length) + next); session.line = next; };

    async function run(pending) {
      const command = session.line;
      session.line = '';
      if (command.trim()) session.history = [...session.history.filter(entry => entry !== command), command].slice(-HISTORY_LIMIT);
      session.index = session.history.length;
      if (command.trim() === 'clear') output('\x1b[2J\x1b[H');
      else if (command.trim()) {
        session.running = true;
        try {
          const result = await bash.exec(command, { cwd: session.cwd, env: session.env, replaceEnv: true });
          if (result.stdout) output(crlf(result.stdout));
          if (result.stderr) output(`\x1b[31m${crlf(result.stderr)}\x1b[0m`);
          if ((result.stdout || result.stderr) && !(result.stdout + result.stderr).endsWith('\n')) output('\r\n');
          session.env = result.env;
          session.cwd = result.env.PWD ?? session.cwd;
        } catch (error) {
          output(`\x1b[31m${crlf(String(error?.message ?? error))}\x1b[0m\r\n`);
        } finally { session.running = false; }
      }
      if (session.closed) return;
      prompt();
      if (pending) write(pending);
    }

    function write(data) {
      if (session.closed || session.running) return;
      for (let i = 0; i < data.length; i++) {
        const char = data[i];
        if (char === '\x1b') {
          const sequence = data.slice(i, i + 3);
          if (sequence === '\x1b[A' || sequence === '\x1b[B') {
            session.index = Math.max(0, Math.min(session.history.length, session.index + (sequence === '\x1b[A' ? -1 : 1)));
            replaceLine(session.history[session.index] ?? '');
          }
          i += 2;
        } else if (char === '\r' || char === '\n') {
          output('\r\n');
          void run(data.slice(i + 1));
          return;
        } else if (char === '\x7f' || char === '\b') {
          if (session.line.length > 0) { session.line = session.line.slice(0, -1); output('\b \b'); }
        } else if (char === '\x03') {
          session.line = '';
          output('^C\r\n');
          prompt();
        } else if (char === '\x0c') {
          output('\x1b[2J\x1b[H');
          prompt();
          output(session.line);
        } else if (char >= ' ') {
          session.line += char;
          output(char);
        }
      }
    }

    output('\x1b[2mPlayground shell (simulated): coreutils, rg, jq and git. Nothing here leaves this tab.\x1b[0m\r\n');
    prompt();
    const id = crypto.randomUUID();
    sessions.set(id, {
      write,
      next: () => chunks.length ? Promise.resolve(chunks.shift()) : session.closed ? Promise.resolve(null) : new Promise(resolve => waiters.push(resolve)),
      close: () => { session.closed = true; for (const waiter of waiters.splice(0)) waiter(null); sessions.delete(id); },
      cwd: () => session.cwd,
      replay: () => session.replay
    });
    return id;
  }

  return { open, write: (id, data) => sessions.get(id)?.write(data), next: id => sessions.get(id)?.next() ?? Promise.resolve(null), close: id => sessions.get(id)?.close(), replay: id => sessions.get(id)?.replay() ?? '' };
}
