import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { access, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { root, delay, require, startHost, snapshot, interrupted } from './shared-cache-support.mjs';

const args = process.argv.slice(2);
const option = name => args.includes(name) ? args[args.indexOf(name) + 1] : undefined;
const inventory = JSON.parse(await readFile(new URL('./shared-cache-inventory.json', import.meta.url)));
const catalog = ['typescript', 'python', 'php', 'css', 'shell', 'csharp', 'clangd', 'java', 'kotlin', 'intellij', 'ruby', 'go', 'rust', 'scala', 'r', 'powershell', 'swift', 'dart'];
const native = new Set(['go', 'dart']);
require(inventory.servers?.length === catalog.length && new Set(inventory.servers.map(item => item.id)).size === catalog.length, 'The shared-cache inventory must classify every catalog server exactly once.');
require(catalog.every(id => inventory.servers.some(item => item.id === id)), 'The shared-cache inventory is missing a catalog server.');
require(inventory.servers.filter(item => item.probe === 'native').every(item => native.has(item.id)), 'Every native shared-cache claim needs a runnable probe.');
if (args.includes('--inventory')) {
  for (const item of inventory.servers) console.log(`${item.id}: ${item.kind}${item.probe ? ` (${item.probe})` : ''}`);
  process.exit(0);
}
if (!option('--server')) throw Error('Explicitly select --server go,dart or use --inventory. Real integration is opt-in.');
const selected = option('--server').split(',');
const base = path.resolve(option('--scratch') ?? path.join(root, '.tools/lsp-shared-cache/run'));
const reportDirectory = path.join(root, 'artifacts/lsp-shared-cache');
const dotnet = path.join(root, '.tools/dotnet/dotnet');
const hostAssembly = path.join(root, 'src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll');
const outcomes = [];
await access(hostAssembly);
await mkdir(reportDirectory, { recursive: true });

async function nativeCase(directory, server) {
  const scenario = {
    go: {
      file: 'fixture.go', manifest: ['go.mod', 'module example.test/sharedcache\n\ngo 1.25\n'],
      valid: 'package fixture\nimport "fmt"\nfunc Shared() string { return fmt.Sprint("shared") }\nvar Branch int = 1\n',
      invalid: text => text.replace('var Branch int = 1', 'var Branch int = "branch-b"'),
      fixed: text => text.replace('var Branch int = 1', 'var Branch int = 2'),
      environment: { GOCACHE: path.join(directory, 'go-build'), GOMODCACHE: path.join(directory, 'go-modules'), GOPROXY: 'off', GOTOOLCHAIN: 'local', GOMAXPROCS: '2' },
      cache: state => path.join(state, 'language/cache/go/v0.23.0'),
      semantic: name => /-(export|xrefs|typerefs|methodsets|analysis)$/.test(name)
    },
    dart: {
      file: 'fixture.dart', manifest: ['pubspec.yaml', 'name: shared_cache_fixture\nenvironment:\n  sdk: ">=3.13.0 <4.0.0"\n'],
      valid: 'import "dart:collection";\nString Shared() => Queue<int>().toString();\nint branch = 1;\n',
      invalid: text => text.replace('int branch = 1', 'int branch = "branch-b"'),
      fixed: text => text.replace('int branch = 1', 'int branch = 2'),
      environment: { DART_SUPPRESS_ANALYTICS: 'true' },
      cache: state => path.join(state, 'native-dart-cache'),
      arguments: state => ['language-server', '--protocol=lsp', '--cache', path.join(state, 'native-dart-cache')],
      semantic: name => !name.includes('-temp-')
    }
  }[server];
  require(scenario, `No native cache fixture for ${server}`);
  const workspaces = [path.join(directory, 'branch-a'), path.join(directory, 'branch-b')];
  const valid = scenario.valid;
  const invalid = scenario.invalid(valid);
  await mkdir(workspaces[0], { recursive: true });
  await writeFile(path.join(workspaces[0], scenario.manifest[0]), scenario.manifest[1]);
  await writeFile(path.join(workspaces[0], scenario.file), valid);
  const configuration = path.join(directory, 'fixture.gitconfig');
  await writeFile(configuration, '[user]\nname = SharpRail fixture\nemail = fixture@example.test\n[commit]\ngpgsign = false\n');
  const gitEnvironment = { ...process.env, GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: configuration };
  const git = args => execFileSync('git', args, { cwd: workspaces[0], env: gitEnvironment, encoding: 'utf8' });
  git(['init', '-q', '-b', 'branch-a']); git(['add', '.']); git(['commit', '-qm', 'Fixture']);
  git(['worktree', 'add', '-qb', 'branch-b', workspaces[1]]);
  await writeFile(path.join(workspaces[1], scenario.file), invalid);
  if (server === 'dart') for (const workspace of workspaces) execFileSync(process.env.SHARPRAIL_LSP_COMMAND_DART, ['pub', 'get', '--offline'], { cwd: workspace, env: { ...process.env, DART_SUPPRESS_ANALYTICS: 'true', PUB_CACHE: path.join(directory, 'pub-cache') }, stdio: 'pipe' });
  const environment = scenario.environment;
  const warmState = path.join(directory, 'warm-state');
  const coldState = path.join(directory, 'cold-state');
  const cachePath = scenario.cache;
  const primeClean = async directory => {
    if (server === 'dart') {
      await writeFile(path.join(directory, scenario.file), invalid);
      await host.call(directory, 'lsp_navigate', { operation: 'symbols', path: scenario.file });
      await host.diagnostics(directory, scenario.file, true);
      await writeFile(path.join(directory, scenario.file), valid);
    }
    return host.diagnostics(directory, scenario.file, false);
  };
  let host;
  try {
    host = await startHost(warmState, workspaces[0], server, environment, scenario.arguments);
    await host.ready(workspaces[0]);
    const clean = await primeClean(workspaces[0]);
    const symbols = await host.call(workspaces[0], 'lsp_navigate', { operation: 'symbols', path: scenario.file });
    require(symbols.locations?.some(item => item.text?.includes('Shared')), `Real ${server} returned no fixture symbol: ${JSON.stringify(symbols).slice(0, 1500)}`);
    await host.stop(); host = null;
    const afterA = await snapshot(cachePath(warmState));
    require(afterA.size > 0, `Workspace A produced no native ${server} cache`);
    host = await startHost(warmState, workspaces[1], server, environment, scenario.arguments);
    await host.ready(workspaces[1]);
    const branchError = await host.diagnostics(workspaces[1], scenario.file, true);
    await host.stop(); host = null;
    const afterWarmB = await snapshot(cachePath(warmState));
    await scenario.beforeCold?.();
    host = await startHost(coldState, workspaces[1], server, environment, scenario.arguments);
    await host.ready(workspaces[1]);
    await host.diagnostics(workspaces[1], scenario.file, true);
    await host.stop(); host = null;
    const afterColdB = await snapshot(cachePath(coldState));
    const reused = [...afterA].filter(([name, before]) => scenario.semantic(name) && afterColdB.get(name)?.hash === before.hash && afterWarmB.get(name)?.hash === before.hash && afterWarmB.get(name)?.modified === before.modified);
    require(reused.length > 0, 'No A-produced semantic cache records stayed unmodified during warm B while independently regenerated by cold B');
    host = await startHost(warmState, workspaces[0], server, environment, scenario.arguments);
    await host.ready(workspaces[0]); await primeClean(workspaces[0]); await host.ready(workspaces[1]);
    await host.diagnostics(workspaces[0], scenario.file, false);
    await host.diagnostics(workspaces[1], scenario.file, true);
    await writeFile(path.join(workspaces[1], scenario.file), scenario.fixed(valid));
    await host.diagnostics(workspaces[1], scenario.file, false);
    await host.diagnostics(workspaces[0], scenario.file, false);
    return { state: 'passed', evidence: { workspaces: workspaces.map(item => path.basename(item)), nativeRecordsAfterA: afterA.size, nativeRecordsAfterWarmB: afterWarmB.size, nativeRecordsAfterColdB: afterColdB.size, reusedRecords: reused.map(([name, value]) => ({ name, sha256: value.hash })), diagnostics: { clean, branchError }, concurrentBranchIsolation: true, invalidationAfterEdit: true } };
  } finally { await host?.stop(); }
}

for (const server of selected) {
  const directory = path.join(base, server + '-' + randomBytes(5).toString('hex'));
  let outcome = { server, state: 'blocked', detail: 'No native cache reuse scenario is implemented for this provider' };
  try {
    await mkdir(directory, { recursive: true });
    if (native.has(server)) outcome = { server, ...await nativeCase(directory, server) };
  } catch (error) { outcome = { server, state: 'failed', detail: error.message }; }
  finally { await rm(directory, { recursive: true, force: true }); }
  outcomes.push(outcome);
  console.log(`${outcome.state.toUpperCase()} ${server}${outcome.detail ? ': ' + outcome.detail : ''}`);
}
for (const outcome of outcomes) await writeFile(path.join(reportDirectory, outcome.server + '.json'), JSON.stringify({ measuredAt: new Date().toISOString(), ...outcome }, null, 2) + '\n');
await writeFile(path.join(reportDirectory, 'latest.json'), JSON.stringify({ measuredAt: new Date().toISOString(), outcomes }, null, 2) + '\n');
if (outcomes.some(item => item.state !== 'passed') || interrupted) process.exitCode = 1;
