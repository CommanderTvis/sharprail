import {spawn, execFileSync} from 'node:child_process';
import {once} from 'node:events';
import {mkdirSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {randomBytes} from 'node:crypto';
import {createServer} from 'node:net';

const mode = process.argv[2] ?? 'embedded';
const rounds = Number(process.argv[3] ?? 10);
const root = resolve(process.argv[4] ?? '.');
if (!['embedded', 'remote'].includes(mode) || !Number.isInteger(rounds) || rounds < 1)
  throw new Error('Usage: node scripts/benchmark.mjs [embedded|remote] [rounds] [workspace]');
mkdirSync('.bench', {recursive: true});
execFileSync('clang', ['-O2', 'scripts/window-probe.m', '-framework', 'CoreGraphics', '-framework', 'CoreFoundation', '-o', '.bench/window-probe']);

function power() {
  const output = execFileSync('pmset', ['-g', 'batt'], {encoding: 'utf8'}).trim();
  if (!output.includes("'AC Power'")) throw new Error('Benchmark requires AC power: ' + output);
  return {at: Date.now(), output};
}

function rss(pids) {
  const rows = execFileSync('ps', ['-axo', 'pid=,ppid=,rss='], {encoding: 'utf8'})
    .trim().split('\n').map(line => line.trim().split(/\s+/).map(Number));
  const ids = new Set(pids);
  for (let changed = true; changed;) {
    changed = false;
    for (const [pid, parent] of rows) if (ids.has(parent) && !ids.has(pid)) {
      ids.add(pid); changed = true;
    }
  }
  return rows.filter(([pid]) => ids.has(pid)).reduce((sum, row) => sum + row[2], 0) / 1024;
}

function launch(binary, env, marker) {
  const child = spawn(resolve(binary), [], {env: {...process.env, ...env}, stdio: ['ignore', 'pipe', 'pipe']});
  let output = '';
  const ready = new Promise((resolveReady, reject) => {
    const timer = setTimeout(() => reject(new Error('Readiness timeout: ' + output)), 30000);
    const receive = chunk => {
      output += chunk;
      const match = output.match(new RegExp(marker + ' (\\d+)'));
      if (match) { clearTimeout(timer); resolveReady(Number(match[1])); }
    };
    child.stdout.on('data', receive);
    child.stderr.on('data', receive);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error('Process exited: ' + code + '\n' + output)); });
  });
  return {child, ready, output: () => output};
}

async function freePort() {
  const server = createServer();
  await new Promise(resolveListen => server.listen(0, '127.0.0.1', resolveListen));
  const port = server.address().port;
  await new Promise(resolveClose => server.close(resolveClose));
  return port;
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const exited = once(child, 'exit');
  child.kill('SIGTERM');
  const timer = setTimeout(() => child.kill('SIGKILL'), 3000);
  await exited;
  clearTimeout(timer);
}

async function measure() {
  const powerReadings = [power()];
  let powerFailure;
  const monitor = setInterval(() => {
    try { powerReadings.push(power()); } catch (error) { powerFailure = error; }
  }, 1000);
  let host, client, probe;
  try {
    const port = await freePort();
    const token = randomBytes(24).toString('hex');
    const env = {SHARPRAIL_ROOT: root, SHARPRAIL_REMOTE: '', SHARPRAIL_TOKEN: token};
    const launchedAt = Date.now();
    if (mode === 'remote') {
      host = launch('artifacts/host/SharpRail.Host.Remote',
        {...env, SHARPRAIL_PORT: String(port), SHARPRAIL_BIND: '127.0.0.1'}, 'SHARPRAIL_HOST_READY');
      env.SHARPRAIL_REMOTE = 'http://127.0.0.1:' + port;
      // Both runtimes are fresh. Wait for bind before connecting; host time stays in total startup.
      await host.ready;
    }
    client = launch('artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI', env, 'SHARPRAIL_WORKSPACE_LAYOUT');
    probe = spawn(resolve('.bench/window-probe'), [String(client.child.pid)]);
    const firstWindow = new Promise((resolveWindow, reject) => {
      let output = '';
      probe.stdout.on('data', chunk => output += chunk);
      probe.once('error', reject);
      probe.once('exit', code => code === 0 ? resolveWindow(Number(output.trim())) : reject(new Error('CoreGraphics probe failed')));
    });
    const [layoutAt, windowAt] = await Promise.all([client.ready, firstWindow]);
    const remaining = Math.max(0, launchedAt + 5000 - Date.now());
    await new Promise(resolveDelay => setTimeout(resolveDelay, remaining));
    if (Date.now() - launchedAt > 5500) throw new Error('Startup missed the five-second RSS sampling interval.');
    for (const process of [client, host].filter(Boolean))
      if (process.child.exitCode !== null || process.child.signalCode !== null)
        throw new Error('Process exited before RSS sampling: ' + process.output());
    const clientRss = rss([client.child.pid]);
    const hostRss = host ? rss([host.child.pid]) : 0;
    powerReadings.push(power());
    if (powerFailure) throw powerFailure;
    return {
      firstWindowMs: windowAt - launchedAt, workspaceLayoutMs: layoutAt - launchedAt,
      hostReadyMs: host ? await host.ready - launchedAt : null,
      clientRssMiB: clientRss, hostRssMiB: hostRss, totalRssMiB: clientRss + hostRss,
      powerReadings, clientOutput: client.output(), hostOutput: host?.output() ?? ''
    };
  } finally {
    clearInterval(monitor);
    await stop(probe);
    await stop(client?.child);
    await stop(host?.child);
  }
}

console.log('Filesystem warm-up (excluded)');
await measure();
const samples = [];
for (let i = 0; i < rounds; i++) {
  const sample = await measure();
  samples.push(sample);
  console.log(JSON.stringify({run: i + 1, ...sample, powerReadings: sample.powerReadings.length, clientOutput: undefined, hostOutput: undefined}));
}
const summary = {};
for (const key of ['firstWindowMs', 'workspaceLayoutMs', 'hostReadyMs', 'clientRssMiB', 'hostRssMiB', 'totalRssMiB']) {
  const values = samples.map(sample => sample[key]).filter(value => value !== null);
  if (!values.length) { summary[key] = null; continue; }
  const mean = values.reduce((a, b) => a + b) / values.length;
  summary[key] = {mean, sampleStandardDeviation: values.length < 2 ? null
    : Math.sqrt(values.reduce((sum, value) => sum + (value - mean) ** 2, 0) / (values.length - 1))};
}
const report = {mode, workload: 'Two-method C# host; static Thinkrail workspace maquette; no Pi', root,
  recordedAt: new Date().toISOString(), rounds, summary, samples};
const path = '.bench/results-' + mode + '-' + Date.now() + '.json';
writeFileSync(path, JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({path, summary}, null, 2));
