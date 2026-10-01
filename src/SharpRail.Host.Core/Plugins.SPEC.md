---
id: submodule-host-plugins
type: submodule-design
status: active
title: Plugins — the host-side plugin runtime
parent: module-host-core
depends-on: [module-plugin-api, module-host-abstractions, submodule-host-state, submodule-host-terminals]
---

# Plugins — the host-side plugin runtime

Upstream: packages/server/src/plugins/SPEC.md @ 4737df6d (CommanderTvis fork)

## Responsibility

Turns plugin host halves (builtin, from `BuiltinPlugins.All`, which is empty in this commit; or external, from
`<stateDir>/plugins/<id>/sharprail-plugin.json` plus every root in `HostState.PluginPaths`) into one running
`PluginRuntime`: a roster the host publishes on its shared state, a method/channel/route dispatcher, the MCP
tools a terminal's agent sees, the plugin file read the app loads UI halves and assets through, and the
terminal, workspace and settings fan-out every active plugin's host context subscribed to. `PluginRuntime` is
the composition root and implements `IPluginService`; every other file in `Plugins/` is a leaf it wires
together. The contract it implements is [the plugin API](../SharpRail.Plugins.Api/SPEC.md).

## The boundary

Every core capability arrives as one injected object, `PluginHostSeams`: the state directory, the host
state store (read `PluginSettings`/`PluginPaths`, publish the roster and the agent records, register the
namespace validator), the loopback server's base URL and route table, the terminal service's plugin seams
(token, resolve, agent record, host-side write, environment contributors, lifecycle observer, revive hook,
process table), the workspace reads and watcher, the bounded Git runner, and a logger. No file in `Plugins/`
reaches a sibling service's internals directly; the composition (`App.cs` for the app's own host,
`RemoteServer.Create` for a remote one) builds the seams from the real services. Everything that reaches a
plugin author (`IPluginHostContext`, `PluginManifest`, `PluginContract`) is the API's public surface,
implemented here and never redeclared.

| File | Holds |
| --- | --- |
| `PluginRuntime.cs` | the composition root: discovery and rescan, the reconciler, activation and teardown, dispatch, subscriptions, routes, the file read, settings validation, tools and terminal fan-out |
| `PluginWorkspaces.cs` | H12 project and workspace reads, and lifecycle events derived from host-state snapshots |
| `PluginHostSeams.cs` | `PluginHostSeams` and `IPluginTerminalSeams` |
| `PluginHostContext.cs` | `IPluginHostContext` for one activation, with the live activation compare |
| `PluginRegistry.cs` | entries, activation tables with the drain counter, topological order, contract intake |
| `PluginDiscovery.cs` | manifest intake and the scan of plugin roots |
| `PluginLoadContext.cs` | the external host half's load context, keyed by directory and content hash |
| `WorkspaceFileWatcher.cs` | one activation's coalescing watch of a workspace (`OnFilesChanged`) |
| `BuiltinPlugins.cs` | the builtin array, empty in this commit |

The runtime runs where the host runs: in the app's process for its own host, inside `SharpRail.Host.Remote` for a
remote host. A remote client never runs a host half.

## Manifest-only builtins (no host half)

A builtin plugin whose manifest declares no host half is registered from its manifest alone: the entry carries a
manifest and no module. Activation finds no host half and goes straight to `Active` with no activation tables;
deactivation has nothing to tear down. Every module-scoped lookup (roster channels, method dispatch, tools)
already guards on the module being present, so a host-less builtin contributes an empty wire surface. It is
visible in the roster (enable and disable in Settings, its `Contributes`) and otherwise inert on the host.

## The roster state machine

A registry entry is `Active`, `Disabled`, `Failed` or `Refused`. Builtin entries always have a real id. External
entries start out either upserted (a valid manifest, real id) or refused (an unreadable or invalid manifest,
keyed `__refused:<directory>` since there is no id to trust). `Refused` is terminal until a rescan sees the
directory's manifest turn valid, at which point the stale `__refused:*` entry is dropped and a fresh id-keyed
entry takes its place; the reverse (valid to invalid) tears down any live activation and replaces the entry with
a refused placeholder the same way. `Failed` is terminal until `RetryAsync(id)` moves it back to `Disabled`, or until its settings turn it off: the
reconciler never retries a failed plugin on its own, so one plugin's crash cannot become a retry storm.

Upserting an unchanged manifest (a rescan re-discovering an active plugin whose directory is untouched) is a
no-op beyond refreshing its directory: module, state and activation stay. Only a genuinely different manifest, or
a different content hash of an entry assembly, drops the stale module and resets the state to `Disabled` so the
reconciler loads and activates the new one on its next pass. Dropping the module unconditionally would leave an
active entry with no module that the reconciler never touches again, answering `Unknown` from then on.

The reconciler computes, per plugin, `desired = explicit PluginSettings[id].enabled ?? (builtin ?
manifest.EnabledByDefault : false)`, ANDed with every dependency being known, not failed or refused, at the
declared wire version, and itself desired, computed dependency-first over the registry's topological order so a
dependency's desiredness is resolved before its dependent reads it. External plugins never auto-enable. A
dependency cycle refuses every plugin the topological order could not place (those in the cycle and anything
transitively depending on them). Activations walk the order forward; deactivations walk it in reverse. Desired
state is a snapshot taken at the start of the pass, so the activation loop re-checks each candidate's
dependencies against live registry state right before activating: a dependency earlier in the same order can
fail its own activation mid-pass, and a dependent that was desired before must then be marked `Failed` in the
same pass rather than activate on top of it. The roster is republished after every individual transition.

Runs are serialised. `Schedule()` coalesces overlapping callers into one more pass rather than queuing one run per
caller: a caller mid-pass gets the in-flight task, and a pending flag guarantees one more full pass before that
task completes, so a caller's own settings change is reflected by the time its await returns. A settings change
from any client schedules a run; it never acts inline.

## Activation ids, drain, and the bounded disposer

Only one activation exists per plugin at a time. Activation ids come from one monotonic counter, and every
mutating member of the host context (`Method`, `Publish`, `Route`, `Tool`, `TerminalEnvironment`,
`OnTerminal`, `RevivePrefill`, `OnWorkspace`, `OnFilesChanged`, `OnSettings`, `ExternalFiles`, `WriteState`)
compares the registry's current activation id with the one captured when the context was built before writing
into that activation's tables or reaching a client. A plugin that keeps a context past its dispose (a stray
timer, a leaked closure) can neither write into a future activation nor resurrect a dead one: the check is a
live compare, not a flag flipped once. The checks pin both cases: a publish after deactivation reaches no
subscriber, and a method registered from a callback that fires after deactivation never appears in the next
activation's table.

Deactivation is three bounded steps. The state flips to `Disabled` immediately, so dispatch and the route
answer disabled before anything async happens. The activation's in-flight counter drains up to a drain timeout.
The disposer runs up to a dispose timeout. A timeout logs a warning and moves on, rather than hanging shutdown or
a later rescan or retry on one wedged plugin. The drain is waiter-based, not polling: entering and leaving a
call increments and decrements one counter per activation, and reaching zero completes every queued waiter.
Both timeouts default to 5 seconds and are parameters so a check can pin them to milliseconds.

Host shutdown deactivates every active plugin in reverse topological order without awaiting each in turn; this
is the one sanctioned asymmetry between the two teardown paths. Because each state flip is synchronous and the
deactivations are started without an intervening await, every plugin is already disabled before any disposer
runs, so a dependent's disposer calling into its dependency gets the disabled error even though it runs first.
Reverse order fixes the order of firing, not this.

## Discovery is a read

An external directory whose manifest claims a builtin plugin's id is refused rather than replacing the builtin.

Discovery lists only the roots it is handed (`<stateDir>/plugins` and `HostState.PluginPaths`); it never walks a
project tree, and a root that does not exist is skipped, not thrown. It keeps no memory: every directory under
every root is read fresh, and an unreadable or invalid `sharprail-plugin.json` becomes a refused entry rather
than being dropped, so a mistyped manifest is visible in the roster. Each entry is checked with its link target
unresolved: a symbolic link is refused outright, naming the entry, rather than followed, because the one
scanned directory would otherwise be one `ln -s` away from placing an arbitrary directory (a project checkout
included) under it. `RescanAsync` is the only thing that re-runs discovery after the host starts; host start
runs it once before the first reconcile.

Manifest intake refuses, each with a reason naming the plugin: JSON that does not deserialize strictly into
`PluginManifest` (the reason names the path), an id that is not a plugin id or differs from the directory name,
and an `ApiGeneration` other than `PluginApi.Generation` (naming the declared and the host's generation).

## The external load seam

An external host half loads into a `PluginLoadContext`, one per plugin directory and content hash of the entry
assembly. The context resolves the shared assemblies (the framework, the API assemblies, and anything already
loaded in the default context by the same name) from the default context, so the plugin's `PluginHostModule`
is the host's type, and resolves everything else from the plugin's own directory. A changed entry assembly
hashes differently and loads into a new context; the previous context stays resident, since contexts here are
not collectible. The entry assembly must expose exactly one public concrete `PluginHostModule` subclass with a
public parameterless constructor; zero, several, or a throwing constructor marks the plugin failed with that
reason. The module's `Contract.Id` must equal the manifest id.

The plugin file read serves a requested path under the plugin's own directory and refuses anything that would
escape it, including through a symbolic link. It is how the app reads an external UI assembly, its sibling
assemblies, and the assets of either origin. A builtin plugin's assets are staged at `plugins/<id>/<assets>`
under the host's output directory and served the same way, so a manifest-only builtin needs no host half to
serve its assets.

## Settings namespaces

`HostStateStore` accepts a `plugin-settings` change (key: plugin id; value: a JSON object merged member by member
into that namespace, or empty to reset it) and a `plugin-paths` change (value: a JSON array of absolute
directories). Before it persists and broadcasts, it calls the validator the runtime registers: for each touched
namespace whose plugin's contract is loaded, the merged object minus `enabled` is deserialized strictly into the
contract's settings type, and the result (defaults filled) is written back with `enabled` preserved. A refusal
throws and the whole batch is rejected. A namespace whose contract is not loaded yet merges unvalidated, and the
schema is enforced from its next write. A namespace found invalid when the store loads falls back to defaults
with one warning.

Contract intake catches what `PluginContract.Create` cannot: duplicate method or channel names, a state channel
naming a snapshot method the contract does not declare, and a settings type with a member serialized as
`enabled`. It runs for builtin and external plugins alike, when the contract loads.

Disabling a plugin cascades, as the fork's settings update does: the change batch that writes `enabled: false`
into a namespace writes it into every transitive dependent's namespace too (`HostStateStore.PluginDependents`,
which the runtime registers), touching no unrelated namespace, so turning the dependency back on does not bring
the dependents back. Independently, each pass keeps a plugin that is enabled while a dependency is absent,
off, failed, refused or at another wire version out of the activation, and its row is `Refused` with a reason
naming that dependency (`depends on <id>, which is off`). That refusal is recomputed every pass rather than
terminal, so the row returns to `Disabled` or activates once the dependency is usable.

## Dispatch

`CallAsync` resolves the entry, the method in the loaded contract, and the active activation's handler, in that
order, answering `Unknown` (no such plugin or method), `Disabled` (known but not active), `InvalidParams` (the
params do not convert into the method's params type; the message names the JSON path) or `Failed` (the handler
threw; its message) as a `PluginCallException`. A known plugin whose host half never loaded has no contract to
look the method up in; it answers `Disabled`. The handler call is counted toward the activation's drain. A
dependent's in-process call (`Dependency(...).RequestAsync`) takes the same path without waiting for the
runtime's startup, since it may run inside the first reconcile.

`SubscribeAsync` registers at once, before the first item is read. It accepts any channel a known plugin's loaded
contract declares, and any channel of a known plugin whose contract has not loaded yet, regardless of enablement, and
yields every later publish on it that is broadcast or addressed to the subscriber's client key, and, for a keyed
state channel with a key given, whose key fields equal the subscription's. It never replays. A publish also
reaches dependent plugins' in-process subscriptions.

A route request under `/plugin/<id>/` on the loopback server reaches the active activation's route handler and
counts toward its drain; a disabled or unknown plugin answers 404.

## Tools

An active plugin's tools join the per-terminal MCP table. `tools/list` includes them
with an input schema generated from the parameters record (`JsonSchemaExporter` with `PluginJson.Options`);
`tools/call` deserializes the arguments strictly, runs the tool with the token's terminal and workspace as its
`PluginToolContext`, and counts the run toward the activation's drain. A tool name clashing with core's or
another active plugin's is refused at registration with a logged reason. Disabling a plugin removes its tools
from future listings and calls; a run already executing finishes.

## Terminals

The terminal service learns three things for plugins. An attach names its tab key, so the host knows each
session's `TerminalRef`; the session id stays the hash of workspace root and tab key the app already uses.
Environment contributors run when a shell starts, after core's own variables. A shell that starts for a tab whose
agent record is set offers that record to every active revive hook, first non-null wins, and the prefill rides
the attachment to the client, which types it once the shell's first output has arrived. Agent records persist in
the host state file, travel on `HostState.TerminalAgents`, and are dropped when the tab closes. Lifecycle events
fire on spawn (with the shell's pid), exit and close, and on every agent record change.

## Checks

Each rule above is pinned in `tests/SharpRail.Checks/PluginHostChecks.cs`, run against both the in-process
runtime and a remote host: manifest refusals (strict shape, id against directory, generation naming both
numbers), discovery (valid, unreadable and missing manifests, a missing root, a symlinked plugin directory
refused), registry (dependency order, cycle refusal, contract-intake refusals, an unchanged active plugin left
alone by a rescan versus a changed one reset), activation (lifecycle, a throwing activation failing the plugin,
the two staleness cases, drain before dispose, both bounded timeouts), reconciler (start enable, external
non-auto-enable, settings disable, cascade, a dependent not activating in the pass its dependency fails,
failed-not-retried-until-retry, coalesced scheduling), dispatch (unknown plugin, unknown method, disabled, a
validation error naming the path, a subscription surviving disable and enable), settings (two namespaces
surviving separate writes, an invalid namespace refusing the batch, defaults filled alongside `enabled`),
tools (arguments validated before the run, a running call counted toward the drain), the file read (round trip,
containment, missing file), and the whole composition (discovery then disabled, settings enable then a method
answers, a route serving, rescan promoting a fixed manifest, shutdown running disposers dependents-first). The
fixture plugin under `tests/` is the external plugin these load from disk.

## External configuration files

Activation-scoped `ExternalFiles` providers supply exact absolute paths per workspace. The runtime's
`AllowsExternalFile(workspaceRoot, path)` consults active registrations only, so disabling a plugin removes
access. `ProjectServices` routes a permitted absolute path through its ordinary text read and compare-and-swap
save without depending on any plugin id.

## Not yet ported

- Agent extensions and skills from a plugin's `pi` block, and the agent tool surface: SharpRail has no
  in-process agent.
- Workspace auto-naming hints.
- `WorkspaceUpdated` for a branch switch: the host learns of workspace changes only from the published
  workspace lists and labels, so it fires on creation, removal and relabelling.
- Serving a plugin's static assets on its HTTP route, as the fork did for its web bundle; the app reads UI
  assemblies and assets through `ReadFileAsync` instead.
