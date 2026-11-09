---
id: module-plugin-processes
type: module-design
status: active
title: plugin-processes — the process tree, CPU and memory, and agent-owned process management as a builtin plugin
parent: group-plugins-workbench
depends-on: [module-plugin-api]
tags: [v1, plugins]
---

## Responsibility

Owns the Processes side tool and the agent-facing management of the processes agents start. Builtin, enabled by default, no
dependencies, wire version 1, category Agents, icon `node-tree`, right rail. Refs #52 (the owned-process registry); the registry itself stays in the host core.
The host and UI assemblies reference only their plugin API entry and the shared contract; the UI also uses the kit.
It has no settings type: BranchGraph, Visualize and the other tool plugins have none, and Settings › Plugins carries the on/off switch every plugin has.

## Where the line is

Core keeps `OwnedProcesses` (the registry, its persistence, SIGTERM/SIGKILL stopping, the stale-file sweep), `HostState.OwnedProcesses`
and the language service. Reason: the stopping rules guard against pid reuse and process groups and are called from `PtyTerminalService` on tab close and
quit, which must not depend on a plugin, and `HostState` is a protocol type every client reads. The registry is inert until it is switched on:
`OwnedProcesses.Enabled` is false at construction and only `IProcessHost.ManageOwned` (H27) changes it.

The plugin owns everything an agent or the user can reach:

- The `owned_process_report` and `owned_process_release` MCP tools (`context.Tool`; they moved out of `WorkspaceMcpTools` and `McpRoute`, names and wording unchanged).
- The prompt section, now `plugin.processes.owned-processes` (it was `core.owned-processes`). A project that listed the old id in
  `.sharprail/instructions.toml` should list the new one; the old id is then an unknown id, which is kept and ignored. The section follows the host's own text
  instead of preceding the `parallel-tasks` section.
- The registry switch: activation calls `ManageOwned(true)`, which also sweeps the records of dead hosts (the sweep moved here from the terminal service's constructor);
  disposal calls `ManageOwned(false)`.
- The Processes pane, the snapshot and stop methods.

## When the plugin is disabled

Nothing is sampled (the pane does not exist, so no request arrives); no pane or rail entry exists; the report and release tools are absent from the MCP list;
the prompt section is absent; `Report` refuses; tab close, workspace removal and quit stop no agent-owned process; the stale sweep does not run;
`HostState.OwnedProcesses` stays empty, so the quit dialog counts nothing and no toast appears. Records that exist when it is switched off are forgotten and
their file deleted, but their processes are not stopped: the user opted out of managing them. Language servers and terminals are untouched; core still ends
its own children. The plugin model applies a switch without a restart, so enabling and disabling take effect at once; agents that already started keep the prompt text they were given.

## Tree model

`ProcessModel.Build` is a pure function of `ProcessInputs` (terminals with shell pid and agent kind, the process table, running language servers, reported processes), a usage
function and a `ProcessHistory`. Three top groups, in this order, always present:

- Language servers: one row per running server of the host language service (workspace root, server id, pid), ordered by workspace then id, labelled with the server's display name (the one the Code Intelligence pane shows, the id when it has none).
- Settings › Processes has "Stop what an agent started along with it" (`StopAgentChildren`, default on). On, stopping an agent row (or a group, or a workspace node containing one) also stops every process forked from it. Off, an agent row stops only its own process; a child row named directly still stops itself and what it forked.
- Tooltips (`Detail`) show the command on one line, cut at 160 characters with an ellipsis; stopping still uses the full recorded command.
- AI agents and daemons: one row per terminal that carries an agent record. The row is the shallowest descendant of the terminal's shell whose command names the agent
  (`claude`, `codex`, `omp`, looking through an interpreter in the first two tokens), else the shell. Under it hangs every process forked from the agent (agent or not, any depth),
  each nested under the process that forked it, and every process the terminal reported with `owned_process_report`, named by its description. Each pid appears once.
- Memory guard: a terminal's row shows the memory of its process tree against the terminal's limit ("memory 2.1 of 8 GB", with the guard's state when it is not Ok). When the guard has said or done anything, a Memory guard group lists its recent events, newest first, with their time; it is absent otherwise. See the Terminals spec ("Memory guard").
- Spawned processes: for a terminal without a recognised agent, a parent row "of Terminal · tab 2" below which hang, nested under the process that forked them, the processes below the shell that have been running at least 10 seconds (`ProcessModel.LongRunning`; a dev server, a watcher, a build; the tooltip adds "up 2m"), any agent process below it whatever its age, and the processes the terminal reported. Commands that come and go in under 10 seconds are not listed, and neither is anything when the system gives no elapsed time (Windows). A terminal with an agent has no row here: its processes are under the agent row.

A group, workspace or parent row shows the sum of everything under it; a process row shows itself plus what is nested below it. The total is the sum of the three groups.
When the rows of a group come from more than one workspace they sit under collapsible workspace rows labelled with the workspace folder name. Tab labels count the host's terminals
per workspace. Language servers' own helper processes (for example tsserver) are not listed; the server row is the process the language service started.

Row tooltip: pid, workspace and tab, command line. CPU is percent of one core (several busy cores exceed 100). Memory is the macOS physical footprint (what Activity Monitor shows), the working set elsewhere.

## Project scope

Every node below a group carries the id (root path) of the workspace it belongs to (`ProcessNode.Workspace`, additive; null on the three groups). The host always samples and sends the whole tree once; the pane
limits what it shows. By default a pane shows only the rows of its own project (every workspace of the project its workspace belongs to, from the host projection); the "All projects" check box above the tree The box keeps the choice: it is saved in the plugin settings (`ShowAllProjects`, default off) when changed and read when the window opens, so a pane starts as the user left it.
shows everything. Groups, their sums and the Total are recomputed from the visible rows, and the Total line says "(this project)" or "(all projects)". Toggling re-filters the last snapshot in place (no request, same row
controls, scroll, selection and collapse state). Stop selected and Stop group act on visible rows only: a scoped group is stopped through the visible rows below it, never by the group id, which would reach other projects.
A project with nothing running shows "Nothing running for this project" and the check box stays. The choice is shared by the Processes panes of one run and is not persisted (the plugin has no settings type).
Subtrees start expanded from the top in order while the rows shown stay within what fits the pane height, and the rest start collapsed. A user's expand or collapse wins, is kept per node across refreshes, and a resize re-decides only nodes the user never toggled.

## In a browser tab

The tab has no process table and no terminal processes; its language servers are Web Workers. There `IProcessHost.Table` is empty and the tree has one group, "Language servers · Web Workers",
whose rows are called workers (`worker 3`) and stop through the language service like any server (the worker is terminated). `Usage` comes from the page: before each sample the plugin calls
`IProcessHost.RefreshAsync`, which asks every live worker for its usage (`ProcessLinks`, set by the tab host). CPU is the time the worker spent handling messages over wall time, an upper bound
(each message counts a whole turn of its event loop); memory is its JS heap where the browser reports one (Chromium's `performance.memory`) and zero elsewhere. The owned-process tools and their
prompt section are not offered, since a tab starts no process an agent could own. The check `--processes` covers the group with fake inputs; the worker read itself needs a browser and is not checked.

## Sampling

The pane asks; the host never samples on its own. `snapshot` samples at most once per second (a closer call, including a second window's, returns the last sample), so the cost is one
process listing plus one `Process.TotalProcessorTime`/footprint read per listed row. The UI polls every 2 s while the panel is attached and effectively visible and not at all otherwise: a hidden
pane, a closed pane or a disabled plugin therefore costs nothing. The handler runs on a worker (`Task.Run`) because the embedded adapter runs a handler on the caller's thread. CPU is the
processor-time delta over wall time between two samples of the same pid; the first sample of a pid reads 0. The trend of each row is the last 30 CPU samples (`ProcessesPlugin.TrendLength`), held in host
memory only and dropped when the row leaves the tree. No 30 s status chip exists; that is out of scope.

## Stopping

`stop({ id })` takes a node id from the last snapshot and stops every stoppable row at or below it (a row, a workspace or parent row, or a group); an id the last snapshot does not hold stops nothing,
so no request can name an arbitrary pid. Processes (agent, spawned and owned rows) take the same path a terminal close uses: SIGTERM to the process group when it leads one, SIGKILL after the registry's grace (3 s),
`taskkill` on Windows; the command line recorded in the snapshot guards against a reused pid. Language servers are stopped only through the language service's own `stop` operation, never by signal, and
the service keeps its usual rules (the server starts again on next use or Retry).

## Wire

Plugin `processes`, wire 1, additive: no HostProtocol change and no bump. Methods `snapshot` and `stop`; no channels; MCP tools `owned_process_report` and `owned_process_release`.
A method not listed in the contract makes the plugin refuse to activate, so the two methods are the whole surface. The host capability behind it is H27, `IPluginHostContext.Processes`
(`IProcessHost`), a default-null member in the Api.Host assembly.

## Checks

`--processes` (also in `--plugins` and the full run) uses fake process sources: grouping and nesting of language servers, an agent with two spawned children and a reported process, workspace rows, group sums
and the total, the 30-sample trend cap, no sampling before a request and one sample per interval, the 50 percent CPU arithmetic, stop through the language service versus the process path, and ids the snapshot
does not hold, and the project scope (filter, sums, toggle in place, scoped stop targets, empty project). A real runtime with a real terminal service covers the switch: default on, tools and prompt present, a reported process kept running after disabling, no registration while off, no quit sweep, and
re-enabling restoring management. `--owned-processes` keeps covering the registry itself.

Not covered: the pane rendered (no screenshot taken), UI polling stopping when the pane is hidden (it follows from `IsEffectivelyVisible`), Linux and Windows usage reads, and the
Claude Code chat host's own process (it lives inside the ClaudeCode plugin and has no seam here), which therefore is not listed.
