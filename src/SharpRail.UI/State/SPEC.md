# State — profile and shared host state

Upstream: apps/web/src/store/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/navigation/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

## Responsibility

The client's two state owners. `ProfileStore` owns on-disk app state that belongs to this app install:
app preferences and one entry per window. `SharedState` is the app's one subscription to its host's shared
state, read by every window. Neither owns rendering, and neither is a second authority over the other's
values: host-owned values live in the host and are mirrored here only as the latest snapshot.

## Boundary

- Owns: `Profile`, `Preferences`, `WindowProfile`, `GitSelection` and their normalization; atomic profile
  writes; the one-time migration of pre-host fields into the local host's `state.json`; the shared-state
  subscription, its reconnect loop and the mirroring of snapshot settings into `Preferences`.
- Forbidden: UI controls or dialogs; writing host-owned values locally as the source of truth; persisting
  derived snapshots such as commit catalogs or file listings; project-directory state (default user state
  lives in `~/.sharprail`; `SHARPRAIL_PROFILE` overrides it for isolated checks).

## Ownership split

Host state (`IHostStateService`, see Host.Abstractions `HostState.cs`) holds appearance mode, the fixed
theme and system pair, file and Markdown line widths with their bounded switches, custom layout presets,
workspace display labels, the open project list and recents, and workspace lifecycle. A window changes
them only through `SharedState.ChangeAsync` and updates when the resulting snapshot arrives, so every
window and client converges on the same values and a control keeps showing the host's value until then.
There is no optimistic write, pending queue or rollback. A rejected change is reported in the window that
made it.

Profile state (`profile.json`) holds interface size, page zoom, hidden-files visibility, the terminal renderer,
rail expansion, per-workspace
Git target/scope/selected commit, and one `WindowProfile` per open window: its frame (`DockState`, which
includes its workspace views), its default preset and its last location. Frame, default preset and group
limits are window-local and never shared through the host; the host carries only the shared custom preset
definitions.

The terminal renderer accepts `texture` and `skia` and defaults to Metal texture; legacy `native` and unknown values normalize to that
default. It is app-local because rendering is a client capability, not host state.

## Shared-state subscription

Snapshots are complete. A client receives the current one on subscribe and again after every reconnect,
which is how it rehydrates anything missed while disconnected; there is no event log to replay. Local
clients receive in-process events; remote clients use a gRPC server stream. `Changed` is raised on the UI
thread with the previous and new snapshot so windows can diff what moved; `ConnectionChanged` reports
drops and re-establishment, after which windows retry a pending startup restore or refresh their mounted
workspace. A local host constructed with an initial snapshot starts connected so the first window routes
without waiting.

Each snapshot is normalized on the read side: unknown or malformed theme mode or pair falls back to fixed
mode retaining a valid opaque theme id, invalid line widths fall back to defaults, and a custom preset
that fails to parse or validate is dropped rather than poisoning the window.

Project and workspace membership follow the snapshot. A client whose project is closed elsewhere moves on
to the next open project's Home or Welcome; a background open never steals a window's location; a removed
workspace returns only the windows that showed it. Workspace selection history is per window and never
host state.

After each (re)connect the subscription asks the host for its handshake in the background and publishes it;
the version is cleared when the connection drops, and an answer from a superseded connection is discarded.
A window that sees a protocol version other than its own logs it and shows a non-blocking notice.

## Profile persistence

Loading is defensive: unreadable files yield a fresh profile with the error kept in `LastError`; missing
lists become empty; invalid frames reset that window to Balanced; paths must be fully qualified; Git
selections with a malformed commit fall back to All changes; theme values from older profiles (`system` as
a theme id) migrate to system mode. Saving writes a temporary file and moves it into place, so a failed
write leaves the previous profile intact; the window reports the failure once while its live state stays
usable. Legacy single-window fields are read into the first `Windows` entry and cleared.

Shared fields migrate once: `OpenState` seeds the local host's `state.json` from the profile's pre-host
fields and clears them only after the state file is written, so a failed migration never loses data. A
remote host keeps its own state in `SHARPRAIL_STATE_DIR`. The local terminal service saves recorded screens
in the profile directory's `terminals` subdirectory, so `SHARPRAIL_PROFILE` isolates them.

## Location

A window's location (Welcome, Project Home or a workspace) is window-local intent, persisted in its
`WindowProfile` and validated against host state before it changes what the window shows. Validation
waits for the host's snapshot rather than treating an empty or disconnected state as absence. Only a
successful read that lacks the project or workspace falls back (workspace to Project Home, project to
Welcome); a timeout or disconnect keeps the remembered location and retries after reconnect. Superseded
project opens are cancelled by a monotonic request counter so a late response never replaces a newer
location. Location is never shared between windows or clients; continuing elsewhere is an explicit action.

## Not yet ported

- A transient toast queue that coalesces identical notifications and caps visible ones at five; the window
  shows one gesture notification at a time. A toast may carry its own duration and one action (an Undo
  receipt). Actionable toasts are never coalesced, since their actions name different inverses, and the cap
  evicts only actionless ones, so a receipt stays until its duration ends or the user dismisses it.
- Per-tab renderer choice and opaque renderer view state for file and diff documents, cached with the
  loaded content and its metadata and written against the owning workspace so a body closing after a
  workspace switch still updates its own tab. Only the renderer interprets its view state; changing
  renderer drops it. Split or inline and ignore-whitespace stay diff-only presentation. Document bodies
  hold their own toggles today, and a Markdown diff's Rendered choice is a flag rather than a renderer.
- Serialized routes and Back/Forward history over locations, with push for user intent and replace for
  passive changes.
- A page-lifetime tombstone for removed workspaces so an in-flight read cannot recreate their local state.
