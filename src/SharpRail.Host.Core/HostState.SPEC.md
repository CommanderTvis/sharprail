---
id: submodule-host-state
type: submodule-design
status: active
title: Host state — projects, settings and persistence
parent: module-host-core
---

# Host state — projects, settings and persistence

Upstream: packages/server/src/projects/SPEC.md @ 4a65ed7f
Upstream: packages/server/src/settings/SPEC.md @ be804a56
Upstream: packages/server/src/persistence/SPEC.md @ c44534ea

## Responsibility

The state every client of one host shares: the open project list and recents, shared settings
(appearance mode, fixed theme and system light/dark pair, file and Markdown line widths with their bound
switches), the custom layout-preset catalog, workspace display labels, the published workspace lists,
plugin settings namespaces and roots, the plugin roster, terminal agent records and the host platform.
`HostStateStore` implements `IHostStateService`: it reads, validates, persists and broadcasts complete
snapshots.

The workbench frame, per-workspace document placement, current or default preset, group limits, bottom
alignment, selection, focus and last location are frontend-local and never reach this store. Built-in
presets belong to the UI.

## Boundary

- Owns: `state.json` in the store's directory (none means memory only), `HostStateChange` application,
  normalization of loaded state, and the watcher channels.
- Public surface: `GetStateAsync`, `ChangeAsync` (atomic batch), `WatchAsync`, plus `PublishWorkspaces`
  and `LastError` for Core and the composer.
- Forbidden: storing frame or view state, workspace resources or window identity; reading old layout
  snapshots.

## Persistence

- Local state lives in `~/.sharprail/state.json`; the first launch seeds it once from the profile's
  pre-host fields, which the UI clears only after the file is written. A remote host uses
  `SHARPRAIL_STATE_DIR`, default `~/.sharprail/host` on its machine. Tests use isolated directories.
- Writes go to a temporary file and are moved over the original. A failed load or save is reported
  through `LastError` and never discards the in-memory state; a corrupt file starts from defaults rather
  than blocking the app.
- The published workspace lists are runtime-only and never persisted.

## Projects

- A project is identified by its absolute root path. `project-open` puts an unknown project first and
  removes it from recents; `project-close` moves it to the front of recents (at most 10) without touching
  the repository, its worktrees or their terminals; `project-forget` drops it from the open list.
- A plain folder opens as a project directly, with no Git required: it is its own root and its Default
  workspace is the only one it has. A folder that later gains a `.git` is a repository on its next open.

## Settings and presets

- Converge on broadcast, no client optimism. `ChangeAsync` applies every change to the current snapshot
  under one lock, persists, increments `Revision` and publishes to every watcher, the initiator included.
  A control keeps showing the host's value until the broadcast arrives. A change that leaves state equal
  publishes nothing.
- An invalid change rejects the whole batch before persistence or broadcast. Line widths accept whole
  numbers 40–240; loaded values outside that range fall back to the client default (zero) without
  discarding valid siblings. Theme ids are opaque: availability and resolution belong to the UI.
  `themeMode` is `fixed` or `system`; anything else loads as `fixed`.
- Custom presets are resource-free, uniquely named, opaque layouts. Saving replaces a preset of the same
  name; renaming onto an existing name is refused; deleting changes only the shared definition, never any
  window's instantiated frame.
- Watchers receive the current snapshot on subscribe and only the latest one when they fall behind,
  because every snapshot is complete.

## Plugins

- `plugin-settings` (key: a plugin id; value: a JSON object merged member by member into that namespace,
  a `null` member removing one, or empty to reset it) and `plugin-paths` (value: a JSON array of absolute
  directories, replacing the list) persist with the rest of `state.json`. `enabled` must be a boolean.
- Before persisting, a touched namespace goes through the validator the plugin runtime registers
  (`PluginNamespaceValidator`); a refusal throws and the whole batch is rejected. Without one, or before
  the plugin's contract loads, a namespace merges unvalidated. Turning a plugin off also writes
  `enabled: false` into each transitive dependent's namespace (`PluginDependents`), touching no other.
- `TerminalAgents` persist (`SetTerminalAgent`, `RemoveTerminalAgents`); the roster (`PublishPlugins`) and
  `Platform` ride every snapshot and are never persisted. Publishing an unchanged roster publishes nothing.
- Namespaces are kept as compact JSON objects, so a reloaded file and a fresh change compare equal; a
  stored namespace that is not an object, or whose `enabled` is not a boolean, is dropped on load.

## Get right

- One host, one truth: no window writes a local copy of shared state; it sends a change and applies the
  broadcast (`HostSync.cs`).
- Invalid paths (relative, empty, containing NUL) and blank or NUL-bearing text are dropped on load and
  rejected on change.
- Separate processes that share a directory are not coordinated; the last writer wins.

## Not yet ported

- Resolving `~` and rejecting relative project paths against the host filesystem, and a path inspection
  that classifies a folder as repository, initializable, missing or not a directory before acting.
- Refusing to open as a project a root already held as another workspace's worktree.
- Stable project ids, readable slugs and `lastOpened` ordering that preserve identity across close and
  reopen.
- A legacy `theme` change without an explicit mode switching the host to fixed mode, and dropping a
  malformed system pair on load.
- Host-persisted terminal catalogs and a per-installation identity file.
