---
id: submodule-host-state
type: submodule-design
status: active
title: Host state — projects, settings and persistence
parent: module-host-core
---

# Host state — projects, settings and persistence

## Responsibility

The shared `vertical-tabs` and `tabs-in-projects` settings control the centre tab-strip mode for all clients.
At local store construction, previously absent values take the embedded desktop profile's supplied fallback,
otherwise false. Existing saved values always win, including false; migration persists only when values change.
This startup migration does not depend on the profile's older `StateMigrated` marker.

Plugin ids can be renamed: loading older state moves the `air-governance` plugin namespace (enabled state and settings) to `air-gateway`, unless an `air-gateway` namespace already exists, and the old key leaves the file in the same save, so it runs once. Plugin data is keyed by id only in that namespace, so nothing else moves. The wire shape is unchanged (ids are data), so the protocol version was not raised.

The state every client of one host shares: the open project list and recents, shared settings
(appearance mode, fixed theme and system light/dark pair, file and Markdown line widths with their bound
switches, the terminal replay size), the custom layout-preset catalog, workspace display labels, each workspace's
creation base and re-pointed review target, and the workspace registry, plugin settings namespaces and roots, plugin roster, terminal agent records and host platform.
`HostStateStore` implements `IHostStateService`: it reads, validates, persists and broadcasts complete
snapshots.

Keymaps are shared state too: `HostState.KeymapPresets` (custom presets, name plus a versioned JSON bindings document the host only checks for shape; ids and chords it does not know are kept) and `HostSettings.ActiveKeymap` (a preset name; empty is the built-in IntelliJ). The changes are `keymap-save` (create or replace a preset, validated name, never IntelliJ), `keymap-rename` (the active selection follows) and `keymap-delete` (deleting the active preset resets the selection), plus the `keymap` setting. A store without these fields (an older profile) has no presets and IntelliJ active. The rules and the model live in `Tereditor.Host.Abstractions/Keymaps.cs`; the UI spec ("Keymap") defines chords.

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

- Local state lives in `~/.tereditor/state.json`; the first launch seeds it once from the profile's
  pre-host fields, which the UI clears only after the file is written. A remote host uses
  `TEREDITOR_STATE_DIR`, default `~/.tereditor/host` on its machine. Tests use isolated directories.
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
  an older host does not erase a newer host's settings. Which settings it knows comes from `HostJson`'s
  metadata for `HostSettings`, not reflection. Mutation keys remain closed; new keys require
  protocol-version gating by clients.
- The file's JSON comes from source-generated metadata (`HostJson`), never reflection, so the host can be
  published as NativeAOT; the bytes are those the reflection serializer wrote (PascalCase names, enums as
  numbers, nulls written, indented), pinned by the `--persisted-json` goldens. Generated deserialization gives
  an absent init-only member its type's default, so a settings property with a default (`HostSettings`) is
  settable rather than init-only: a file from an older host still reads every default.
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
- Recents, open projects and their workspaces are filtered against the host filesystem whenever a snapshot
  is read: a path that no longer names a directory (missing, or replaced by a file) disappears, while
  permission or other filesystem errors keep the entry. The projection never changes the stored lists, so a
  restored folder returns. Plugins watch the stored lists, so a hidden workspace is not a removed one to them.
- A host with terminals looks at every open project's folder, and at its workspaces' folders while the
  project's is there, every 3 seconds. A folder missing on every look for 10 seconds counts as deleted: its
  shells and MCP identities end (a project's with all its workspaces', detached ones included, as on a
  close), its language servers stop with the reason "its folder was deleted", the host logs it once, and the
  first attached tab carries a "Closed <name>" notification, which a client shows as a terminal
  notification. A folder back within the grace is untouched. Nothing on disk is deleted, and the stored
  entry, its terminal tabs and its plugin data stay: a removed entry would read to plugins as a removed
  workspace, and a deleted folder cannot be told from one on an unmounted volume safely. When the folder
  returns, its workspaces may start language servers again (a project's autostart ones start), and its
  tabs open fresh shells. A folder on a volume that is no longer mounted is not deleted: the host remembers
  the mount each folder was last seen on and waits while that mount is gone; a folder it never saw counts
  only when its parent folder still exists. A folder deleted together with its parent before the host saw
  it is therefore left alone.
- A plain folder opens as a project directly, with no Git required: it is its own root and its Default
  workspace is the only one it has. A folder that later gains a `.git` is a repository on its next open.

Project display names (ThinkRail #764): `project-label` stores a name for a project root in `ProjectLabels`,
persisted and kept across close and reopen; the path, project record and worktrees are unchanged. The host collapses
whitespace runs and trims, and rejects a name outside 1 to 100 characters or with control characters; the folder
name clears the label. `HostState.ProjectName` is the shown name. Remote clients receive it as `StateReply` member 24.

## Unread workspaces

Ported from ThinkRail #770 onto tereditor's own unread model. ThinkRail kept per-chat completion
receipts; tereditor has no embedded chat, so the unit is the workspace and the sources are the agent terminals.
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
- `TereditorExcludeDeclined` (`tereditor-exclude-declined`) lists absolute project roots whose user chose to keep an
  untracked `.tereditor` in the repository; it is persisted, idempotent and sorted, and invalid paths are dropped on load.

## Settings and presets

- Converge on broadcast, no client optimism. `ChangeAsync` serializes batches and validates them under
  the state lock. Project shutdown runs outside that lock; the final changes are applied, persisted,
  assigned a new `Revision` and published under it to every watcher, the initiator included.
  A control keeps showing the host's value until the broadcast arrives. A change that leaves state equal
  publishes nothing.
- An invalid change rejects the whole batch before persistence or broadcast. Line widths accept whole
  numbers 40–240, `editor-tab-width` 1–8 and `editor-line-numbers` a boolean (defaults 4 and on), `editor-indent-tabs` and
  `editor-detect-indent` booleans (defaults off and on), `editor-font-family` an opaque family name of at most 200
  characters without control characters (empty, the default, is the client's bundled font; trimmed), `editor-line-height`
  10–20 tenths of the natural line height (default 10; loaded values outside it fall back to zero, the default), `accent` empty, a lowercase slug or `#rrggbb` (opaque like theme ids; the UI resolves it); loaded values outside that range fall back to the client default (zero) without
  discarding valid siblings. Theme ids are opaque: availability and resolution belong to the UI.
  `themeMode` is `fixed` or `system`; anything else loads as `fixed`. A batch that changes `theme` without
  naming a mode switches the host to fixed mode, so a client that predates modes still sees its theme
  applied. A system pair with only one side is malformed and loads as no pair. The terminal replay size
  (`terminal-replay`) accepts whole numbers 0–1024 KiB and defaults to 64; a loaded value outside that
  range is clamped. Zero is a real value, so the wire always carries the field and a host that predates
  it reads as the default. `terminal-keep-after-exit` (`true`/`false`, default false) keeps an exited
  shell's tab with its exit notice instead of closing it like Ghostty; a host that predates it reads as
  closing. `window-decorations` (`auto`, `client` or `system`; stored as empty, `client` or `system`, default automatic) is how Linux
  clients frame their window; other clients ignore it, an unknown loaded value reads as automatic and a host that predates it reads as
  automatic. `notifications` (`true`/`false`, default on) is the master toggle for clients'
  away notifications; the wire carries its negation, so a host that predates it reads as on.
  `terminal-notifications` (`true`/`false`, default on, Ghostty's `desktop-notifications`) lets programs in terminals raise
  desktop notifications (OSC 9, OSC 777, OSC 99); the wire carries its negation. `terminal-option-as-alt` (`true`/`false`, default off, Ghostty's
  `macos-option-as-alt`) makes the macOS Option key an Alt (ESC prefix) in terminals instead of composing characters. `bell-features` is a comma-separated subset of
  `system`, `attention` and `title`, or `none`, stored in that canonical order; the default `attention,title` is Ghostty's
  `bell-features` default. An empty wire value, from a host that predates it, and an unknown loaded value read as the default.
  `terminal-hidden-chips` is the comma-separated ids of the terminal chips the user hid (`model`, `env/env-mise`), opaque to the
  host and stored distinct in ordinal order; empty, the default and what a host that predates it sends, hides none. An id that
  is longer than 128 characters or holds a control character rejects the change; such a loaded value reads as empty.
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
  user renames; a stored program title loses a leading status glyph on load, as the scanner does. All are sent to remote clients (protocol field 4; an older host's titles read as user renames) and
  dropped with the terminal or its project.
  A window writes each title onto the tab that shows the terminal, on a broadcast and when a workspace mounts.
- `PendingEmigrations` holds each `workspace_emigrate` request awaiting the user (`RequestEmigrationAsync`); it rides
  snapshots and remote clients (protocol 12) and is never persisted. `terminal-emigration-answer` (`approve` or
  `deny`) removes a pending request and settles its waiting call; an unknown id changes nothing. The creator of each
  workspace made through `workspace_create` persists (`WorkspaceCreators`) and rides snapshots and remote clients
  (additive protobuf member 33), dropping with the workspace, so clients can group by it. See the
  [Workspaces SPEC](Workspaces.SPEC.md).
- `OwnedProcesses` lists the processes agents reported with `owned_process_report` (terminal, pid, description); it rides
  snapshots and remote clients (protocol 16), is never persisted in `state.json` (the registry keeps its own file) and
  follows `PtyTerminalService.Owned`. See [Terminals SPEC](Terminals.SPEC.md#owned-processes).
- `TerminalAgents` persist (`SetTerminalAgent`, `RemoveTerminalAgents`); the roster (`PublishPlugins`) and
  `Platform` ride every snapshot and are never persisted. Publishing an unchanged roster publishes nothing.
- `HomeDirectory` is the host's own user home folder (`Environment.SpecialFolder.UserProfile`), set at snapshot time and never
  persisted. It rides the snapshot as additive `StateReply` field 34 (empty is null), so the protocol version stays 19: an older
  host sends none and a client then abbreviates no path. Clients show exactly that folder as `~` (see the UI Kit SPEC).
- Namespaces are kept as compact JSON objects, so a reloaded file and a fresh change compare equal; a
  stored namespace that is not an object, or whose `enabled` is not a boolean, is dropped on load.

## Get right

- One host, one truth: no window writes a local copy of shared state; it sends a change and applies the
  broadcast (`HostSync.cs`).
- Invalid paths (relative, empty, containing NUL) and blank or NUL-bearing text are dropped on load and
  rejected on change.
- Separate processes that share a directory are not coordinated; the last writer wins.

## Not built

- A `NotGit` refusal of a non-repository. tereditor opens a plain folder as a workspace on purpose (the
  startup root, and Git failure never blocks opening files) and offers Initialize afterwards, so refusing it
  needs a product decision; inspection already gives a client the classification, and the
  code already survives the local and remote adapters.

The first agent title may fill an unnamed generated managed workspace label through the workspace
tool. The write checks under the state lock that no manual label arrived in the meantime; manual
labels always take precedence. The workspace tool owns the corresponding initial branch rename.

Host-state stream channel waits continue without capturing their caller's synchronization context.
A plugin runtime may register its observer synchronously before activation, then cancel/drain it while
a UI thread is synchronously tearing down a window; host observation must not wait for that UI thread.
The watcher hygiene context regression checks activation, state updates and cancellation from a
recording synchronization context without requiring a blocked dispatcher to make progress.
