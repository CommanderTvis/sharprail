---
id: submodule-host-state
type: submodule-design
status: active
title: Host state — projects, settings and persistence
parent: module-host-core
---

# Host state — projects, settings and persistence

Upstream: packages/server/src/projects/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/settings/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/persistence/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The state every client of one host shares: the open project list and recents, shared settings
(appearance mode, fixed theme and system light/dark pair, file and Markdown line widths with their bound
switches), the custom layout-preset catalog, workspace display labels, each workspace's creation base and re-pointed
review target, and the published workspace lists.
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
- Opening a plain folder offers Initialize: `git init -b main`, `git add -A` and an allow-empty initial
  commit, supplying a fallback identity only for a field Git has none configured for. A failed commit
  removes the new `.git` again, and a folder that is already a repository is refused.

## Workspace review targets

- `WorkspaceBases` is creation provenance: the host records the base ref when it creates a worktree from
  anything but `HEAD`, and no client change touches it. `WorkspaceDiffBases` holds a target re-pointed away
  from it (`workspace-diff-base`); `HostState.DiffBase(path)` is the override, else the base, else empty.
- Re-pointing to an empty ref or to the creation base removes the override rather than storing a copy. A
  ref must pass the ref-shape check ([Git.SPEC.md](Git.SPEC.md)) but need not resolve. Both entries persist,
  are dropped with the workspace when the host removes it, and entries with an invalid path or ref are
  dropped on load.

## Settings and presets

- Converge on broadcast, no client optimism. `ChangeAsync` applies every change to the current snapshot
  under one lock, persists, increments `Revision` and publishes to every watcher, the initiator included.
  A control keeps showing the host's value until the broadcast arrives. A change that leaves state equal
  publishes nothing.
- An invalid change rejects the whole batch before persistence or broadcast. Line widths accept whole
  numbers 40–240; loaded values outside that range fall back to the client default (zero) without
  discarding valid siblings. Theme ids are opaque: availability and resolution belong to the UI.
  `themeMode` is `fixed` or `system`; anything else loads as `fixed`.
- Setting updates accept a closed set of keys and validate their values before publishing the batch;
  unknown setting keys and change kinds are rejected. The typed change-list contract replaces upstream's
  object-shaped partial update, so arbitrary JSON payloads are not a separate mutation surface.
- Custom presets are resource-free, uniquely named, opaque layouts. Saving replaces a preset of the same
  name; renaming onto an existing name is refused; deleting changes only the shared definition, never any
  window's instantiated frame.
- Watchers receive the current snapshot on subscribe and only the latest one when they fall behind,
  because every snapshot is complete.

## Get right

- One host, one truth: no window writes a local copy of shared state; it sends a change and applies the
  broadcast (`HostSync.cs`).
- Invalid paths (relative, empty, containing NUL) and blank or NUL-bearing text are dropped on load and
  rejected on change.
- Separate processes that share a directory are not coordinated; the last writer wins.

## Not yet ported

- Preserving unknown settings already on disk across valid updates, so an older host does not erase a
  newer host's settings. Mutation keys remain closed; new keys require protocol-version gating by clients.
- Resolving `~` and rejecting relative project paths against the host filesystem, and a path inspection
  that classifies a folder as repository, initializable, missing or not a directory before acting.
- Refusing to open a non-repository (`HostErrorCode.NotGit`) and a canonical root already held as
  another workspace's worktree (`HostErrorCode.AlreadyOpen`). Both codes already survive the local and
  remote adapters; opening still accepts plain folders and does not check workspace ownership.
- Stable project ids, readable slugs and `lastOpened` ordering that preserve identity across close and
  reopen.
- A legacy `theme` change without an explicit mode switching the host to fixed mode, and dropping a
  malformed system pair on load.
- Host-persisted terminal catalogs and a per-installation identity file.
