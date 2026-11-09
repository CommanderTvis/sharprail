export const meta = {
  name: 'factory',
  description: 'Coding factory: run a task list with parallel agents in their own workspaces, conflict-aware, then verify and (optionally) land each result one at a time',
  whenToUse: 'Several independent coding tasks for this repo. args: {tasks:[{id,title,brief,areas:[paths],dependsOn?:[ids],docsOnly?:bool}], maxAgents?:4, land?:false, dryRun?:false}',
  phases: [
    { title: 'Implement', detail: 'one agent per task in its own SharpRail workspace; builds wait in queue-heavy' },
    { title: 'Verify', detail: 'signed commits, files inside the declared areas, report complete' },
    { title: 'Land', detail: 'one at a time: rebase, fast-forward push, sync, cleanup, Roslyn diagnostics' },
  ],
}

// Input. Everything but `tasks` is optional.
//   tasks[].areas    path prefixes the task may touch; two tasks whose areas overlap never run together
//                    (no areas = conflicts with everything)
//   tasks[].docsOnly the task builds nothing and starts no language server (still conflicts on shared paths)
//   land             false (default): stop after verification and report; true: also rebase, push to main, clean up
//   dryRun           only print the schedule, spawn nothing
const ROOT = args?.root ?? '/Users/commandertvis/thinkrail/sharprail'
const QUEUE = args?.queueHeavy ?? '/Users/commandertvis/.sharprail/bin/queue-heavy'
const CAP = args?.maxAgents ?? 2
const LAND = args?.land === true
const DRY = args?.dryRun === true
const BASE = args?.base ?? 'origin/main'
const ATTRIBUTION = args?.attribution ?? 'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>'
// Paths every non-docs task may also touch: the checks belong to the change they cover. Override with args.alwaysAllow.
const ALWAYS = args?.alwaysAllow ?? ['tests/SharpRail.Checks']
const TASKS = (args?.tasks ?? []).map(t => {
  const task = { areas: [], dependsOn: [], docsOnly: false, ...t }
  // Scheduling uses the declared areas only (an empty list still conflicts with everything); verification also accepts ALWAYS.
  return { ...task, allowed: task.docsOnly || !task.areas.length ? task.areas : [...task.areas, ...ALWAYS] }
})

const RULES = `
Rules for this task (apply all of them):
- Work only in your own SharpRail workspace: create it first with the workspace_create MCP tool (load its schema with ToolSearch "select:mcp__plugin_sharprail_sharprail__workspace_create" if deferred; pass description and baseBranch "${BASE}"), then use the returned absolute path for every command and edit. Never run git worktree add.
- MEMORY RULE (this Mac swaps): the ONLY way to run anything heavy is ${QUEUE} <command>: every restore, build, publish, format, check mode, test, `dotnet run` of a script that builds, npm/pnpm install, native or Zig build. Never run such a command directly, never run two at once, never start a build server or leave one running (the wrapper shuts them down; if you started any other process, stop it), never launch the app. The wrapper also waits while free memory is low. Cap yourself to one heavy command at a time.
- Other agents run in parallel. Run EVERY build, restore, format, check mode and test through ${QUEUE} <command> from your workspace directory (it waits while another agent runs a heavy command). Start with scripts/mise run restore in the fresh worktree; use scripts/mise x -- dotnet build and scripts/mise run format.
- Never run checks that open real windows (they steal focus; CI covers them) and list what you skipped. Start no language server. Never run machine-wide process listings or touch processes you did not start.
- Navigate with the context_search MCP tool first (load it with ToolSearch "select:mcp__plugin_sharprail_sharprail__context_search"; pass your workspace path) and grep/Read for literals. If its index is not ready, say so.
- Touch only the declared areas, plus the checks folder (${ALWAYS.join(', ')}) for the checks that belong to your change. If the task needs other files, finish what is inside the areas, commit it and list the rest as a blocker.
- Commit signed; never bypass signing (on a 1Password or ssh-agent failure stop and report). End each commit message with:
${ATTRIBUTION}
- Do NOT push. Keep SPEC.md text short, no code references in specs.
- Your final answer is the structured report: workspace path and branch, commit shas, changed files, checks run and skipped, number of context_search calls, suspected bugs you did not fix, blockers.`

const REPORT = {
  type: 'object',
  properties: {
    workspace: { type: 'string' },
    branch: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    files: { type: 'array', items: { type: 'string' } },
    checksRun: { type: 'array', items: { type: 'string' } },
    checksSkipped: { type: 'array', items: { type: 'string' } },
    contextSearchCalls: { type: 'integer' },
    suspectedBugs: { type: 'array', items: { type: 'string' } },
    blockers: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
  required: ['workspace', 'commits', 'files', 'checksRun', 'checksSkipped', 'contextSearchCalls', 'summary'],
}
const VERDICT = {
  type: 'object',
  properties: {
    ok: { type: 'boolean' },
    unsigned: { type: 'array', items: { type: 'string' } },
    outsideAreas: { type: 'array', items: { type: 'string' } },
    problems: { type: 'array', items: { type: 'string' } },
  },
  required: ['ok', 'unsigned', 'outsideAreas', 'problems'],
}
const LANDED = {
  type: 'object',
  properties: {
    landed: { type: 'boolean' },
    sha: { type: 'string' },
    touchedCsharp: { type: 'array', items: { type: 'string' } },
    diagnosticsErrors: { type: 'integer' },
    diagnosticsNotes: { type: 'string' },
    problems: { type: 'array', items: { type: 'string' } },
  },
  required: ['landed', 'problems'],
}

const norm = p => p.replace(/\\/g, '/').replace(/^\.\//, '').replace(/\/+$/, '')
const overlaps = (a, b) => {
  if (!a.areas.length || !b.areas.length) return true
  return a.areas.some(x => b.areas.some(y => {
    const p = norm(x), q = norm(y)
    return p === q || p.startsWith(q + '/') || q.startsWith(p + '/')
  }))
}

// Which pending tasks may start now: dependencies done and ok, no overlap with a running task, under the cap.
function ready(pending, running, results) {
  const out = []
  for (const t of pending) {
    if (running.length + out.length >= CAP) break
    const depsOk = t.dependsOn.every(id => results.get(id)?.state === 'done')
    const depFailed = t.dependsOn.some(id => results.has(id) && results.get(id).state !== 'done')
    if (depFailed || !depsOk) continue
    if ([...running, ...out].some(r => overlaps(t, r))) continue
    out.push(t)
  }
  return out
}

// Landing and Roslyn diagnostics share one lane: one at a time.
let lane = Promise.resolve()
const inLane = fn => { const run = lane.then(fn, fn); lane = run.catch(() => {}); return run }

async function work(t) {
  phase('Implement')
  const report = await agent(
    `${RULES}\n\nTask ${t.id}: ${t.title}\nTouch only: ${t.areas.length ? t.areas.join(', ') : '(unspecified: ask in blockers)'}\n${t.docsOnly ? 'Docs only: do not build and do not run checks.\n' : ''}\n${t.brief}`,
    { label: `implement:${t.id}`, phase: 'Implement', schema: REPORT })
  if (!report) return { state: 'failed', note: 'agent returned nothing' }
  const warnings = []
  if (!report.commits.length) return { state: report.blockers?.length ? 'blocked' : 'failed', report, note: report.blockers?.join('; ') ?? 'no commit' }
  // Blockers with commits are not fatal: the finished part is still verified and reported, the rest is listed.
  for (const blocker of report.blockers ?? []) warnings.push('blocker: ' + blocker)
  if (!t.docsOnly && report.contextSearchCalls === 0) warnings.push('0 context_search calls on code work')

  const verdict = await agent(
    `Verify without changing anything. Workspace ${report.workspace}, commits ${report.commits.join(' ')}, declared areas: ${t.allowed.join(', ') || '(none)'}.
For each commit run: git -C ${report.workspace} cat-file commit <sha> | rg -c "BEGIN SSH SIGNATURE" (1 means signed, otherwise unsigned) and git -C ${report.workspace} show --name-only --format= <sha>. Report every unsigned sha, every changed file outside the declared areas (path-prefix match), and any problem (commit missing, uncommitted changes in git -C ${report.workspace} status --short, branch not based on ${BASE}). ok is true only when all three lists are empty.`,
    { label: `verify:${t.id}`, phase: 'Verify', schema: VERDICT, effort: 'low' })
  if (!verdict?.ok) return { state: 'rejected', report, warnings, note: (verdict?.problems ?? ['verification failed']).concat(verdict?.unsigned ?? [], verdict?.outsideAreas ?? []).join('; ') }
  if (!LAND) return { state: 'done', report, warnings, landed: false }

  const landed = await inLane(() => agent(
    `Land one verified task on main. Repo root (Default checkout, stays on main): ${ROOT}. Workspace ${report.workspace}, branch ${report.branch}, commits ${report.commits.join(' ')}.
1. In the workspace: git fetch origin, git rebase ${BASE}. Stop and report problems on conflicts.
2. Check that every commit is still signed (git cat-file commit <sha> | rg -c "BEGIN SSH SIGNATURE"). Never bypass signing.
3. git push origin HEAD:main (fast-forward only, never force).
4. In ${ROOT}: git pull --ff-only origin main, then git push backup main.
5. Delete the workspace with the workspace_delete MCP tool (pass its path; leave the directory first) and then git branch -D ${report.branch} in ${ROOT}.
6. Run the lsp_diagnostics MCP tool (workspace ${ROOT}, paths = every C# file the commits changed, from git show --name-only) one call at a time; call again for pending files; read the results for the touched lines. Report the number of errors and a one-line note on real findings (style noise on untouched lines is not a finding). If it cannot run, say so loudly in diagnosticsNotes starting with LSP DIAGNOSTICS DID NOT RUN.
Return landed true only after the push succeeded.`,
    { label: `land:${t.id}`, phase: 'Land', schema: LANDED }))
  if (!landed?.landed) return { state: 'landing-failed', report, warnings, note: (landed?.problems ?? ['no result']).join('; ') }
  return { state: 'done', report, warnings, landed: true, landing: landed }
}

// Dry run: print the waves the scheduler would start.
if (DRY) {
  const results = new Map(), pending = [...TASKS]
  let wave = 0
  while (pending.length) {
    const batch = ready(pending, [], results)
    if (!batch.length) { log(`stuck: ${pending.map(t => t.id).join(', ')} (dependency failed, cycle or unknown id)`); break }
    log(`wave ${++wave}: ${batch.map(t => t.id).join(', ')}`)
    for (const t of batch) { pending.splice(pending.indexOf(t), 1); results.set(t.id, { state: 'done' }) }
  }
  return { dryRun: true, waves: wave }
}

const results = new Map()
const pending = [...TASKS]
const running = []
const inflight = new Map()
log(`${TASKS.length} tasks, up to ${CAP} at once, ${LAND ? 'landing each result' : 'stopping before landing'}`)
while (pending.length || running.length) {
  for (const t of ready(pending, running, results)) {
    pending.splice(pending.indexOf(t), 1)
    running.push(t)
    log(`start ${t.id}: ${t.title}`)
    inflight.set(t.id, work(t).catch(e => ({ state: 'failed', note: String(e) })).then(r => [t.id, r]))
  }
  if (!running.length) {
    for (const t of pending) results.set(t.id, { state: 'skipped', note: 'waiting on a task that did not finish, or never ready' })
    break
  }
  const [id, result] = await Promise.race(inflight.values())
  inflight.delete(id)
  running.splice(running.findIndex(r => r.id === id), 1)
  results.set(id, result)
  log(`${id}: ${result.state}${result.note ? ' (' + result.note + ')' : ''}`)
}

const lines = TASKS.map(t => {
  const r = results.get(t.id) ?? { state: 'skipped' }
  const ws = r.report?.workspace ? `${r.report.workspace}` : ''
  const warn = r.warnings?.length ? `; ${r.warnings.join(', ')}` : ''
  return `- ${t.title} (${ws || t.id}): ${r.state}${r.note ? ', ' + r.note : ''}${r.landed ? ', landed ' + (r.landing?.sha ?? '') : ''}${warn}`
})
return { board: lines.join('\n'), results: Object.fromEntries(results) }
