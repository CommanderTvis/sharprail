import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dotnet = path.join(root, '.tools/dotnet/dotnet');
const hostAssembly = path.join(root, 'src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll');
export const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
export const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export const require = (value, message) => { if (!value) throw Error(message); };
const children = new Set();
export let interrupted = false;
process.on('SIGINT', () => { interrupted = true; for (const child of children) child.kill('SIGTERM'); });

export async function snapshot(directory) {
  const result = new Map();
  async function visit(folder) {
    for (const item of await readdir(folder, { withFileTypes: true }).catch(error => error.code === 'ENOENT' ? [] : Promise.reject(error))) {
      const file = path.join(folder, item.name);
      if (item.isDirectory()) await visit(file);
      else if (item.isFile()) {
        const info = await stat(file, { bigint: true });
        result.set(path.relative(directory, file), { hash: digest(await readFile(file)), modified: info.mtimeNs.toString(), bytes: Number(info.size) });
      }
    }
  }
  await visit(directory);
  return result;
}

export async function startHost(state, workspace, server, environment, launchArguments) {
  await mkdir(path.join(state, 'language'), { recursive: true });
  const command = process.env[`SHARPRAIL_LSP_COMMAND_${server.toUpperCase()}`];
  require(!launchArguments || command, `Set SHARPRAIL_LSP_COMMAND_${server.toUpperCase()} to the actual provider executable for the isolated cache probe`);
  await writeFile(path.join(state, 'language/servers.json'), JSON.stringify({ [server]: { ...(command ? { command } : { managed: server !== 'dart' }), ...(launchArguments ? { arguments: launchArguments(state) } : {}) } }));
  const token = randomBytes(32).toString('hex');
  const child = spawn(dotnet, [hostAssembly], { cwd: root, env: { ...process.env, ...environment, SHARPRAIL_ROOT: workspace, SHARPRAIL_STATE_DIR: state, SHARPRAIL_BIND: '127.0.0.1', SHARPRAIL_PORT: '0', SHARPRAIL_TOKEN: token, SHARPRAIL_HEADLESS_MCP: '1', SHARPRAIL_LSP_DISABLE: '0' }, stdio: ['ignore', 'pipe', 'pipe'] });
  children.add(child);
  let logs = '';
  child.stdout.on('data', data => { logs = (logs + data).slice(-100_000); });
  child.stderr.on('data', data => { logs = (logs + data).slice(-100_000); });
  let nextId = 0;
  const stop = async () => {
    child.kill('SIGTERM');
    const deadline = Date.now() + 10_000;
    while (child.exitCode === null && child.signalCode === null && Date.now() < deadline) await delay(50);
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await new Promise(resolve => child.once('exit', resolve)); }
    children.delete(child);
  };
  try {
    const deadline = Date.now() + 30_000;
    while (!/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.test(logs)) {
      if (child.exitCode !== null || Date.now() > deadline || interrupted) throw Error('Headless host startup failed');
      await delay(50);
    }
    const endpoint = logs.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)[1] + '/mcp';
    const rpc = async (method, params) => {
      const response = await fetch(endpoint, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: ++nextId, method, params }), signal: AbortSignal.timeout(120_000) });
      require(response.ok, `MCP HTTP ${response.status}`);
      const message = await response.json();
      require(!message.error, `MCP error: ${JSON.stringify(message.error)}`);
      return message.result;
    };
    await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'sharprail-shared-cache-checks', version: '1' } });
    const call = async (directory, name, arguments_) => {
      const result = await rpc('tools/call', { name, arguments: { workspace: directory, ...arguments_ } });
      const text = result.content?.filter(item => item.type === 'text').map(item => item.text).join('\n');
      require(text, `Missing MCP result for ${name}`);
      try { return JSON.parse(text); }
      catch { throw Error(`${name}: ${text.slice(0, 1800)}`); }
    };
    const ready = async directory => {
      let result = await call(directory, 'lsp_status', { operation: 'retry', server });
      const deadline = Date.now() + 180_000;
      while (['installing', 'initializing'].includes(result.state) && Date.now() < deadline && !interrupted) {
        await delay(200);
        result = await call(directory, 'lsp_status', { operation: 'status', server });
      }
      require(result.state === 'ready', `${server} did not become ready: ${result.state}: ${result.detail}`);
      return result;
    };
    const diagnostics = async (directory, file, expectError) => {
      const deadline = Date.now() + 90_000;
      let result;
      do {
        result = await call(directory, 'lsp_diagnostics', { paths: [file] });
        const publication = result.files?.find(item => item.path === file || item.path.endsWith('/' + file));
        const diagnostics = result.diagnostics ?? result.files?.flatMap(item => item.diagnostics ?? []) ?? [];
        const errors = diagnostics.filter(item => item.kind === '1');
        if (result.state === 'ready' && publication && ['current', 'unversioned'].includes(publication.freshness) && (errors.length > 0) === expectError) return { state: result.state, freshness: publication.freshness, errors: errors.map(item => item.text) };
        await delay(200);
      } while (Date.now() < deadline && !interrupted);
      throw Error(`Expected ${expectError ? 'errors' : 'clean diagnostics'} in ${directory}: ${JSON.stringify(result).slice(0, 1800)}`);
    };
    return { call, ready, diagnostics, stop, logs: () => logs };
  } catch (error) { await stop(); throw error; }
}

