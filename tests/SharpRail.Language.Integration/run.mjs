import { validateCorpus } from './manifest.mjs';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const args = process.argv.slice(2);
if (!args.includes('--all') && !args.includes('--server')) throw Error('Explicitly select --all or --server <id>; this expensive suite is opt-in.');
const selected = args.includes('--all') ? null : args[args.indexOf('--server') + 1];
const corpus = JSON.parse(await readFile(new URL('./corpus.json', import.meta.url), 'utf8'));
validateCorpus(corpus);
const cases = corpus.cases.filter(item => selected === null || selected.split(',').includes(item.server));
if (!cases.length) throw Error('No matching scenario');
const cache = path.join(root, '.tools/lsp-validation');
const reports = path.join(root, 'artifacts/lsp-validation');
await mkdir(cache, { recursive: true });
await mkdir(reports, { recursive: true });
const dotnet = process.env.DOTNET ?? 'dotnet';
// The JVM cases run with the repository's pinned JDK (`scripts/tool jdk`), or JAVA_HOME when
// set. The host's project-JDK lookup prefers a machine SDKMAN install, so the child environment points it at an empty
// candidates directory: the selection never depends on what the machine has installed.
const jdk = process.env.JAVA_HOME || execFileSync('sh', [path.join(root, 'scripts/tool'), 'jdk'], { encoding: 'utf8' }).trim();
const java = path.join(jdk, 'bin', process.platform === 'win32' ? 'java.exe' : 'java');
const jvmEnvironment = { JAVA_HOME: jdk, PATH: path.join(jdk, 'bin') + path.delimiter + (process.env.PATH ?? ''), SDKMAN_CANDIDATES_DIR: path.join(cache, 'no-sdkman') };
const host = path.join(root, 'src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll');
await access(host);
const outcomes = [];
let active;
let interrupted = false;
process.on('SIGINT', () => { interrupted = true; active?.kill('SIGTERM'); process.exitCode = 130; });
function command(executable, argv, cwd, env = process.env) {
  return execFileSync(executable, argv, { cwd, env, encoding: 'utf8', timeout: 300_000, maxBuffer: 8 * 1024 * 1024 });
}
for (const scenario of cases) {
  if (interrupted) break;
  const started = Date.now();
  const report = { server: scenario.server, repository: scenario.repository, revision: scenario.revision, state: 'failed', coverage: scenario.coverage, calls: [] };
  let log = '';
  let captureLogs;
  let restoreProbe;
  try {
    const runtime = scenario.runtime === 'dotnet' ? dotnet : scenario.runtime === 'java' ? java : scenario.runtime;
    try { report.runtime = command(runtime, scenario.runtime === 'java' ? ['-version'] : scenario.runtime === 'go' ? ['version'] : scenario.runtime === 'cs' ? ['version'] : ['--version'], root).trim(); }
    catch (error) { report.state = 'blocked'; throw Error(`Runtime ${scenario.runtime} unavailable: ${error.message}`); }
    if (scenario.blocked) { report.state = 'blocked'; throw Error(scenario.blocked); }
    const project = path.join(cache, 'projects', scenario.repository.replace('/', '-'), scenario.revision);
    await mkdir(project, { recursive: true });
    try { await access(path.join(project, '.git')); }
    catch {
      command('git', ['init', '-q'], project);
      command('git', ['remote', 'add', 'origin', `https://github.com/${scenario.repository}.git`], project);
    }
    let revision = '';
    try { revision = command('git', ['rev-parse', '--verify', '--quiet', 'HEAD'], project).trim(); } catch { }
    if (revision !== scenario.revision) {
      command('git', ['fetch', '--depth=1', 'origin', scenario.revision], project);
      command('git', ['checkout', '--detach', scenario.revision], project);
    }
    if (command('git', ['rev-parse', 'HEAD'], project).trim() !== scenario.revision) throw Error('Project revision mismatch');
    const projectEnvironment = { ...process.env, ...jvmEnvironment, BUNDLE_PATH: path.join(project, '.bundle/packages'), BUNDLE_USER_HOME: path.join(cache, 'bundle'), GEM_SPEC_CACHE: path.join(cache, 'gem-specs') };
    for (const preparation of scenario.prepare ?? []) {
      const argv = Array.isArray(preparation) ? preparation : preparation.command;
      const cwd = Array.isArray(preparation) ? project : path.resolve(project, preparation.cwd);
      if (cwd !== project && !cwd.startsWith(project + path.sep)) throw Error('Preparation directory escapes project');
      command(argv[0], argv.slice(1), cwd, projectEnvironment);
    }
    report.workspace = project;
    const sourceFiles = command('git', ['ls-files'], project).split('\n').filter(file => scenario.extensions.includes(path.extname(file)));
    report.sourceFiles = sourceFiles.length;
    if (sourceFiles.length < scenario.minimumSourceFiles) throw Error(`Corpus too small: ${sourceFiles.length} source files`);
    await access(path.join(project, scenario.path));
    const state = path.join(cache, 'state', scenario.server);
    await mkdir(path.join(state, 'language'), { recursive: true });
    await writeFile(path.join(state, 'language/servers.json'), JSON.stringify({ [scenario.server]: { managed: !['swift', 'dart'].includes(scenario.server) } }));
    const token = randomBytes(32).toString('hex');
    const environment = { ...projectEnvironment, SHARPRAIL_ROOT: project, SHARPRAIL_STATE_DIR: state, SHARPRAIL_BIND: '127.0.0.1', SHARPRAIL_PORT: '0', SHARPRAIL_TOKEN: token, SHARPRAIL_HEADLESS_MCP: '1', SHARPRAIL_LSP_DISABLE: '0', DOTNET_ROOT: path.dirname(dotnet) };
    active = spawn(dotnet, [host], { cwd: root, env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
    active.stdout.on('data', chunk => { log = (log + chunk).slice(-200_000); });
    active.stderr.on('data', chunk => { log = (log + chunk).slice(-200_000); });
    const startup = Date.now() + 30_000;
    while (!/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.test(log)) {
      if (active.exitCode !== null || Date.now() > startup) throw Error('Headless host failed to start');
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    const endpoint = log.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)[1] + '/mcp';
    async function rpc(method, params) {
      const response = await fetch(endpoint, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }), signal: AbortSignal.timeout(120_000) });
      if (!response.ok) throw Error(`MCP HTTP ${response.status}`);
      const message = await response.json();
      if (message.error) throw Error(JSON.stringify(message.error));
      return message.result;
    }
    async function call(tool, arguments_, retry = 0) {
      const result = await rpc('tools/call', { name: tool, arguments: { workspace: project, ...arguments_ } });
      const value = JSON.parse(result.content[0].text);
      report.calls.push({ tool, arguments: arguments_, result: value });
      if (scenario.server === 'scala' && tool === 'lsp_navigate' && value.state === 'error' && /request .*has been cancelled/i.test(value.detail ?? '') && retry < 3 && !interrupted) {
        await new Promise(resolve => setTimeout(resolve, 1000));
        return call(tool, arguments_, retry + 1);
      }
      return value;
    }
    captureLogs = () => call('lsp_status', { operation: 'logs', server: scenario.server });
    await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'sharprail-functional-tests', version: '1' } });
    const tools = await rpc('tools/list');
    if (!tools.tools.some(tool => tool.name === 'lsp_navigate')) throw Error('Production language MCP tools missing');
    const catalog = await call('lsp_status', { operation: 'catalog' });
    report.provider = catalog.servers?.find(item => item.id === scenario.server);
    if (scenario.configuration) await call('lsp_status', { operation: 'configure', server: scenario.server, configuration: scenario.configuration });
    for (const step of scenario.calls) {
      if (step.assert === 'checked' && scenario.diagnosticProbe && !restoreProbe) {
        const file = path.join(project, scenario.path);
        const original = await readFile(file);
        restoreProbe = () => writeFile(file, original);
        await writeFile(file, Buffer.concat([original, Buffer.from(scenario.diagnosticProbe)]));
      }
      if (step.anchor) {
        const text = await readFile(path.join(project, step.arguments.path), 'utf8');
        const start = text.indexOf(step.anchor.text);
        if (start < 0 || text.indexOf(step.anchor.text, start + 1) >= 0) throw Error('Anchor must occur exactly once');
        const index = start + step.anchor.text.indexOf(step.anchor.identifier);
        const prefix = text.slice(0, index);
        step.arguments.line = prefix.split('\n').length - 1;
        step.arguments.column = prefix.length - prefix.lastIndexOf('\n') - 1;
      }
      let result = await call(step.tool, step.arguments);
      const deadline = Date.now() + 900_000;
      while (['installing', 'initializing'].includes(result.state)) {
        if (interrupted) throw Error('Interrupted');
        if (Date.now() > deadline) throw Error('Import/readiness deadline exceeded');
        await new Promise(resolve => setTimeout(resolve, 1000));
        result = await call(step.tool, step.arguments.operation === 'retry' ? { operation: 'status', server: scenario.server } : step.arguments);
      }
      if (step.assert === 'crossFile' && ['swift', 'scala'].includes(scenario.server)) {
        const indexDeadline = Date.now() + 900_000;
        while (['ready', 'initializing'].includes(result.state) && !result.locations?.some(item => item.path !== step.arguments.path) && Date.now() < indexDeadline && !interrupted) {
          await new Promise(resolve => setTimeout(resolve, 1000));
          result = await call(step.tool, step.arguments);
        }
      }
      if (step.assert === 'checked' && result.state === 'ready') {
        const diagnosticDeadline = Date.now() + 30_000;
        while (!result.files?.some(file => ['current', 'unversioned'].includes(file.freshness)) && Date.now() < diagnosticDeadline && !interrupted) {
          await new Promise(resolve => setTimeout(resolve, 1000));
          result = await call(step.tool, step.arguments);
          if (result.state !== 'ready') break;
        }
      }
      if (['requiresAcceptance', 'disabled', 'expired'].includes(result.state)) { report.state = 'blocked'; throw Error(result.detail); }
      if (result.state !== 'ready') throw Error(`${step.tool}: ${result.state}: ${result.detail}`);
      if (step.assert === 'locations' && !result.locations?.length) throw Error('Expected semantic locations, received none');
      if (step.assert === 'checked' && !result.files?.some(file => ['current', 'unversioned'].includes(file.freshness))) throw Error('No current or explicitly unversioned diagnostic publication was received');
      if (step.assert === 'crossFile' && !result.locations?.some(item => item.path !== step.arguments.path)) throw Error('No references outside the queried file');
    }
    report.state = 'passed';
    await call('lsp_status', { operation: 'stop', server: scenario.server });
  } catch (error) { report.detail = error.message; }
  finally {
    if (restoreProbe) await restoreProbe();
    if (captureLogs && !interrupted) { try { await captureLogs(); } catch { } }
    if (active && active.exitCode === null) {
      const process_ = active;
      process_.kill('SIGTERM');
      await Promise.race([new Promise(resolve => process_.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 10_000))]);
      if (process_.exitCode === null) process_.kill('SIGKILL');
    }
    active = null;
    report.durationMs = Date.now() - started;
    await writeFile(path.join(reports, `${scenario.server}.json`), JSON.stringify(report, null, 2) + '\n');
    await writeFile(path.join(reports, `${scenario.server}.log`), log);
    outcomes.push({ server: report.server, state: report.state, detail: report.detail, sourceFiles: report.sourceFiles, coverage: report.coverage });
    console.log(report.state.toUpperCase(), report.server, report.detail ?? '');
  }
}
await writeFile(path.join(reports, 'summary.json'), JSON.stringify(outcomes, null, 2) + '\n');
if (!interrupted && outcomes.some(item => item.state !== 'passed')) process.exitCode = 1;
