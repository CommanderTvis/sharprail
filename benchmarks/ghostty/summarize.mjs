import {readFileSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';

const batch = resolve(process.argv[2]);
const metadata = JSON.parse(readFileSync(resolve(batch, 'metadata.json')));
const stats = values => ({n:values.length,mean:values.reduce((a,b)=>a+b,0)/values.length,min:Math.min(...values),max:Math.max(...values)});
const phases = [...new Set(metadata.trials.flatMap(trial=>trial.phases.map(phase=>phase.name)))];
const summary = {kind:metadata.kind,mode:metadata.mode,phases:{}};
for (const name of phases) {
  const rows = metadata.trials.map(trial=>trial.phases.find(phase=>phase.name===name));
  summary.phases[name] = {
    footprintMiB:stats(rows.map(row=>row.footprintMiB)),
    peakFootprintMiB:stats(rows.map(row=>row.sampledPeakFootprintMiB)),
    ...(name === 'idle' || name === 'paced' ? {cpuPercent:stats(rows.map(row=>100*row.cpuSeconds/row.seconds))} : {})
  };
}
const latency = metadata.trials.flatMap(trial=>(trial.latency??[]).map(sample=>sample.displayMs)).sort((a,b)=>a-b);
if (latency.length) {
  const percentile = q => {const i=(latency.length-1)*q;return latency[Math.floor(i)]+(latency[Math.ceil(i)]-latency[Math.floor(i)])*(i%1);};
  summary.latencyMs = {n:latency.length,median:percentile(.5),p95:percentile(.95),max:Math.max(...latency)};
}
writeFileSync(resolve(batch,'summary.json'),JSON.stringify(summary,null,2));
console.log(JSON.stringify(summary,null,2));
