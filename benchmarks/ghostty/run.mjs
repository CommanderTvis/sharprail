import {spawn, execFileSync} from 'node:child_process';
import {mkdirSync, readFileSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';

const [kind, mode, destination] = process.argv.slice(2);
const executables = {presentation:'GhosttyRendererBench', memory:'GhosttyMemoryBench', attribution:'GhosttyAttribution'};
if (!executables[kind] || !['native','texture'].includes(mode) || !destination)
  throw Error('Usage: node benchmarks/ghostty/run.mjs presentation|memory|attribution native|texture .bench/NEW_DIRECTORY');
const batch = resolve(destination);
mkdirSync(batch, {recursive:false});
const power = () => execFileSync('pmset', ['-g','batt'], {encoding:'utf8'});
const metadata = {started:new Date().toISOString(), kind, mode, power:power(), trials:[]};
for (let i=1; i <= (kind === 'attribution' ? 1 : 3); i++) {
  if (!power().includes("'AC Power'")) throw Error('AC power required');
  const output = resolve(batch, String(i));
  const child = spawn(resolve(`benchmarks/ghostty/${kind}/bin/Release/net10.0/${executables[kind]}`), [mode,output],
    {env:{...process.env,DOTNET_ROOT:resolve('.tools/dotnet')},stdio:['ignore','pipe','pipe']});
  let log='', failure;
  child.stdout.on('data', x=>log+=x); child.stderr.on('data', x=>log+=x);
  const readings=[];
  const monitor=setInterval(()=>{
    try {const value=power(); readings.push({time:Date.now(),power:value}); if (!value.includes("'AC Power'")) throw Error('Power changed');}
    catch (error) {failure=error; child.kill('SIGTERM');}
  },1000);
  const timeout=setTimeout(()=>{failure=Error('Trial exceeded 120 seconds');child.kill('SIGTERM');},120000);
  let code;
  try {code=await new Promise((accept,reject)=>{child.once('error',reject);child.once('exit',accept);});}
  finally {clearInterval(monitor);clearTimeout(timeout);writeFileSync(output+'.log',log);writeFileSync(output+'.power.json',JSON.stringify(readings));}
  if (failure || code !== 0) throw failure ?? Error(`Trial failed: ${code}\n${log}`);
  const result=JSON.parse(readFileSync(output+'/result.json'));
  if (result.scale !== 2) throw Error('Expected 2x backing scale');
  metadata.trials.push(result);
  writeFileSync(batch+'/metadata.json',JSON.stringify(metadata,null,2));
  console.log(kind,mode,i,result.phases.map(x=>[x.name,x.footprintMiB]));
}
