---
id: submodule-host-state
type: submodule-design
status: active
title: Host state — projects, settings and persistence
parent: module-host-core
---

# Host state — projects, settings and persistence

Upstream: packages/server/src/projects/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))
Upstream: packages/server/src/settings/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))
Upstream: packages/server/src/persistence/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))

## Responsibility

The shared `vertical-tabs` and `tabs-in-projects` settings control the centre tab-strip mode for all clients.
At local store construction, previously absent values take the embedded desktop profile's supplied fallback,
otherwise false. Existing saved values always win, including false; migration persists only when values change.
This startup migration does not depend on the profile's older `StateMigrated` marker.

The state every client of one host shares: the open project list and recents, shared settings
(appearance mode, fixed theme and system light/dark pair, file and Markdown line widths with their bound
switches, the terminal replay size), the custom layout-preset catalog, workspace display labels, each workspace's
creation base and re-pointed review target, and the workspace registry, plugin settings namespaces and roots, plugin roster, terminal agent records and host platform.
`HostStateStore` implements `IHostStateService`: it reads, validates, persists and broadcasts complete
snapshots.

The workbench frame, per-workspace document placement, current or default preset, group limits, bottom
alignment, selection, focus and last location are frontend-local and never reach this store. Built-in
presets belong to the UI.

## Boundary

- Owns: `state.json` in the store's directory (none means memory only), `HostStateChange` application,
  normalization of loaded state, and the watcher channels.
- Supports reading snapshots, atomic batches of changes, and subscriptions to state and lifecycle
  updates. Workspace removal notifies the composer so its terminals can end; persistence errors
  remain available to the caller.
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
  the repository or its worktrees. Closing or forgetting a project ends its workspace terminals, including detached sessions; `project-forget` drops it from the open list.
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
- Recents is filtered against the host filesystem whenever a snapshot is read: a path that no longer
  names a directory (missing, or replaced by a file) disappears, while permission or other filesystem
  errors keep the entry. The projection never changes the stored list, so a restored folder returns.
- A plain folder opens as a project directly, with no Git required: it is its own root and its Default
  workspace is the only one it has. A folder that later gains a `.git` is a repository on its next open.

Project display names (ThinkRail #764): `project-label` stores a name for a project root in `ProjectLabels`,
persisted and kept across close and reopen; the path, project record and worktrees are unchanged. The host collapses
whitespace runs and trims, and rejects a name outside 1 to 100 characters or with control characters; the folder
name clears the label. `HostState.ProjectName` is the shown name. Remote clients receive it as `StateReply` member 24.

## Unread workspaces

Ported from ThinkRail #770 onto SharpRail's own unread model (the STATUS A18 gap). Upstream keeps per-chat completion
receipts; SharpRail has no embedded chat, so the unit is the workspace and the sources are the agent terminals.
`UnreadWorkspaces` maps a workspace path to the strongest reason it is unread: `blocked` (an agent waits for input),
`failed`, `done`, or `manual` (marked by hand); a weaker reason never replaces a stronger one. It is persisted, sent to
remote clients as `StateReply` member 25, and dropped with the workspace's record.

- A plugin's attention notification (Codex and Claude Code report `blocked`, `failed` or `done` as
  `AttentionNotification.Reason`) marks its workspace unread unless a focused window of that client shows it.
  Every client may report the same result; the change is idempotent.
- A focused window that shows the workspace (on mounting it or on activation) marks it read with `seen`, which
  clears every reason. Mark as read by hand clears any reason but `blocked`, which only seeing the workspace clears.
- Mark as unread on the shown workspace holds in that window until the user leaves it and comes back, so it is not
  read again on the next activation. Another client showing it may still read it; that is accepted, as upstream.

## Workspace review targets

- `WorkspaceBases` is creation provenance: the host records the base ref when it creates a worktree from
  anything but `HEAD`, and no client change touches it. `WorkspaceDiffBases` holds a target re-pointed away
  from it (`workspace-diff-base`); `HostState.DiffBase(path)` is the override, else the base, else empty.
- Re-pointing to an empty ref or to the creation base removes the override rather than storing a copy. A
  ref must pass the ref-shape check ([Git.SPEC.md](Git.SPEC.md)) but need not resolve. Both entries persist,
  are dropped with the label when the workspace's record leaves the registry, and entries with an invalid
  path or ref are dropped on load. The creation base is kept apart from the record's `BaseBranch`, which
  always names a branch: a workspace cut from `HEAD` has a `BaseBranch` and no review target.
- `SharprailExcludeDeclined` (`sharprail-exclude-declined`) lists absolute project roots whose user chose to keep an
  untracked `.sharprail` in the repository; it is persisted, idempotent and sorted, and invalid paths are dropped on load.

## Settings and presets

- Converge on broadcast, no client optimism. `ChangeAsync` serializes batches and validates them under
  the state lock. Project shutdown runs outside that lock; the final changes are applied, persisted,
  assigned a new `Revision` and published under it to every watcher, the initiator included.
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
  it reads as the default. `terminal-keep-after-exit` (`true`/`false`, default false) keeps an exited
  shell's tab with its exit notice instead of closing it like Ghostty; a host that predates it reads as
  closing. `settle-idle-days` is the Settled shelf's idle window: whole days 1–365, or
  `never` (null); it defaults to 7, a loaded value is clamped, and the wire carries zero for never with a
  host that predates it reading as the default. `window-decorations` (`auto`, `client` or `system`; stored as empty, `client` or `system`, default automatic) is how Linux
  clients frame their window; other clients ignore it, an unknown loaded value reads as automatic and a host that predates it reads as
  automatic. `notifications` (`true`/`false`, default on) is the master toggle for clients'
  away notifications; the wire carries its negation, so a host that predates it reads as on.
  `terminal-notifications` (`true`/`false`, default on, Ghostty's `desktop-notifications`) lets programs in terminals raise
  desktop notifications (OSC 9, OSC 777, OSC 99); the wire carries its negation. `bell-features` is a comma-separated subset of
  `system`, `attention` and `title`, or `none`, stored in that canonical order; the default `attention,title` is Ghostty's
  `bell-features` default. An empty wire value, from a host that predates it, and an unknown loaded value read as the default.
- Setting updates accept a closed set of keys and validate their values before publishing the batch;
  unknown setting keys and change kinds are rejected. The typed change-list contract replaces upstream's
  object-shaped partial update, so arbitrary JSON payloads are not a separate mutation surface.
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
- The shared `terminal-title` state change lets clients rename a terminal through the same title store;
  it validates nonempty identities and titles, persists and broadcasts through local and remote adapters.
- `TerminalTitles` persist beside them (`SetTerminalTitle`, `RemoveTerminalTitles`): at most one per terminal, with
  its source. A user rename (`terminal-title`) outranks an agent's `set_title`, which outranks the program's OSC 0/2
  title; a weaker source never replaces a stronger one, so `set_title` after a rename keeps the user's name and says so.
  Program titles ride snapshots and broadcasts but are never persisted. Titles stored before sources existed load as
  user renames. All are sent to remote clients (protocol field 4; an older host's titles read as user renames) and
  dropped with the terminal or its project.
  A window writes each title onto the tab that shows the terminal, on a broadcast and when a workspace mounts.
- `PendingEmigrations` holds each `workspace_emigrate` request awaiting the user (`RequestEmigrationAsync`); it rides
  snapshots and remote clients (protocol 12) and is never persisted. `terminal-emigration-answer` (`approve` or
  `deny`) removes a pending request and settles its waiting call; an unknown id changes nothing. The creator of each
  workspace made through `workspace_create` persists host-side only (`WorkspaceCreators`). See the
  [Workspaces SPEC](Workspaces.SPEC.md).
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

- The host-wide settled-workspace idle window: default seven days, a positive integer clamped to
  1–365, or null to disable idle settling. Validate mutations as known settings; malformed nonnumeric
  persisted values restore the default rather than silently disabling the lifecycle.
- A `NotGit` refusal of a non-repository. SharpRail opens a plain folder as a workspace on purpose (the
  startup root, and Git failure never blocks opening files) and offers Initialize afterwards, so refusing it
  needs the product decision upstream made; inspection already gives a client the classification, and the
  code already survives the local and remote adapters.

The first agent title may fill an unnamed generated managed workspace label through the workspace
tool. The write checks under the state lock that no manual label arrived in the meantime; manual
labels always take precedence. The workspace tool owns the corresponding initial branch rename.

Host-state stream channel waits continue without capturing their caller's synchronization context.
A plugin runtime may register its observer synchronously before activation, then cancel/drain it while
a UI thread is synchronously tearing down a window; host observation must not wait for that UI thread.
The watcher hygiene context regression checks activation, state updates and cancellation from a
recording synchronization context without requiring a blocked dispatcher to make progress.
