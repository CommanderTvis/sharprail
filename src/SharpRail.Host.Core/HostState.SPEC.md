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
switches, the terminal replay size), the custom layout-preset catalog, workspace display labels, each workspace's
creation base and re-pointed review target, and the workspace registry.
`HostStateStore` implements `IHostStateService`: it reads, validates, persists and broadcasts complete
snapshots.

The workbench frame, per-workspace document placement, current or default preset, group limits, bottom
alignment, selection, focus and last location are frontend-local and never reach this store. Built-in
presets belong to the UI.

## Boundary

- Owns: `state.json` in the store's directory (none means memory only), `HostStateChange` application,
  normalization of loaded state, and the watcher channels.
- Public surface: `GetStateAsync`, `ChangeAsync` (atomic batch), `WatchAsync`, `WatchLifecycleAsync`, plus
  `ChangeWorkspaces`, `RecordWorkspaceBase`, `WorkspaceRemoved` (raised with the path of a workspace whose
  record left the registry, so its terminals can end) and `LastError` for Core and the composer.
- Forbidden: storing frame or view state, workspace resources or window identity; reading old layout
  snapshots.

## Persistence

- Local state lives in `~/.sharprail/state.json`; the first launch seeds it once from the profile's
  pre-host fields, which the UI clears only after the file is written. A remote host uses
  `SHARPRAIL_STATE_DIR`, default `~/.sharprail/host` on its machine. Tests use isolated directories.
- Writes go to a temporary file and are moved over the original. A failed load or save is reported
  through `LastError` and never discards the in-memory state; a corrupt file starts from defaults rather
  than blocking the app.
- The workspace registry is persisted with the rest. `ChangeWorkspaces` rewrites it atomically, keeps
  each project's Default workspace first and drops the label of a record that leaves. Loading keeps only
  well-formed records, one per id and path ([Workspaces.SPEC.md](Workspaces.SPEC.md)).
- After each published snapshot the store pushes the lifecycle events of its difference to
  `WatchLifecycleAsync` subscribers: projects opened and closed, workspaces created, updated and removed.
  The stream replays nothing, so a subscriber that needs the present state reads the snapshot.
- Settings in the file that this host does not know are kept verbatim and written back on every save, so
  an older host does not erase a newer host's settings. Mutation keys remain closed; new keys require
  protocol-version gating by clients.
- `installation.json` beside the state file holds the directory's identity (`Installation.EnsureIn`,
  surfaced as `HostStateStore.InstallationId`): a UUID created exclusively on first use, so racing first
  launches agree on one id. A malformed file is reported and never replaced; a memory-only store has none.

## Projects

- A project is identified by its absolute root path. `project-open` puts an unknown project first and
  removes it from recents; `project-close` moves it to the front of recents (at most 10) without touching
  the repository, its worktrees or their terminals; `project-forget` drops it from the open list.
- Every open or recent project has one `ProjectRecord`: a UUID, a readable slug (lower-case name with runs
  of other characters as one dash, `-2`, `-3`… on a clash) and `lastOpened` in Unix milliseconds. Opening
  mints the record or advances `lastOpened`; closing keeps it, so identity survives close and reopen, and
  it is dropped only when the project leaves both lists. A state file without records gets them minted and
  written on load. The open list keeps newest-first order because an opened project goes to the front.
- A client path is resolved on the host (`ProjectPaths.Resolve`, used by `OpenProjectAsync` and
  `InspectProjectPathAsync`): `~` and `~/…` are the host user's home, anything else must be absolute, and
  a relative path is refused rather than resolved against the host's working directory.
  `InspectProjectPathAsync` classifies the folder before a client acts: `Repository` (inside a working
  tree), `Initable` (a plain folder), `Missing` or `NotDirectory`. The picker flow refuses the last two
  before opening anything and offers Initialize for the second.
- `project-open` of a folder that is a linked worktree of an open project is refused as
  `HostErrorCode.AlreadyOpen`, read from the workspace registry and the folder's `.git` file without
  running Git. The code reaches local and remote clients alike; a worktree whose project is not open may
  still be opened on its own.
- Opening a plain folder offers Initialize: `git init -b main`, `git add -A` and an allow-empty initial
  commit, supplying a fallback identity only for a field Git has none configured for. A failed commit
  removes the new `.git` again, and a folder that is already a repository is refused.
- A plain folder opens as a project directly, with no Git required: it is its own root and its Default
  workspace is the only one it has. A folder that later gains a `.git` is a repository on its next open.

## Workspace review targets

- `WorkspaceBases` is creation provenance: the host records the base ref when it creates a worktree from
  anything but `HEAD`, and no client change touches it. `WorkspaceDiffBases` holds a target re-pointed away
  from it (`workspace-diff-base`); `HostState.DiffBase(path)` is the override, else the base, else empty.
- Re-pointing to an empty ref or to the creation base removes the override rather than storing a copy. A
  ref must pass the ref-shape check ([Git.SPEC.md](Git.SPEC.md)) but need not resolve. Both entries persist,
  are dropped with the label when the workspace's record leaves the registry, and entries with an invalid
  path or ref are dropped on load. The creation base is kept apart from the record's `BaseBranch`, which
  always names a branch: a workspace cut from `HEAD` has a `BaseBranch` and no review target.

## Settings and presets

- Converge on broadcast, no client optimism. `ChangeAsync` applies every change to the current snapshot
  under one lock, persists, increments `Revision` and publishes to every watcher, the initiator included.
  A control keeps showing the host's value until the broadcast arrives. A change that leaves state equal
  publishes nothing.
- An invalid change rejects the whole batch before persistence or broadcast. Line widths accept whole
  numbers 40–240; loaded values outside that range fall back to the client default (zero) without
  discarding valid siblings. Theme ids are opaque: availability and resolution belong to the UI.
  `themeMode` is `fixed` or `system`; anything else loads as `fixed`. A batch that changes `theme` without
  naming a mode switches the host to fixed mode, so a client that predates modes still sees its theme
  applied. A system pair with only one side is malformed and loads as no pair. The terminal replay size
  (`terminal-replay`) accepts whole numbers 0–1024 KiB and defaults to 64; a loaded value outside that
  range is clamped. Zero is a real value, so the wire always carries the field and a host that predates
  it reads as the default.
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

- The host-wide settled-workspace idle window: default seven days, a positive integer clamped to
  1–365, or null to disable idle settling. Validate mutations as known settings; malformed nonnumeric
  persisted values restore the default rather than silently disabling the lifecycle.
- A `NotGit` refusal of a non-repository. SharpRail opens a plain folder as a workspace on purpose (the
  startup root, and Git failure never blocks opening files) and offers Initialize afterwards, so refusing it
  needs the product decision upstream made; inspection already gives a client the classification, and the
  code already survives the local and remote adapters.
