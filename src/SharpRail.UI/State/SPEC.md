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
  writes; each window's `ToastQueue`; the one-time migration of pre-host fields into the local host's `state.json`; the shared-state
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
drops and re-establishment. `SharedState` reports both to the client's `HostConnection`, whose
`Generation` counts connections. A window remembers the generation it read its content under and, on a
newer one, retries a pending startup restore or subscribes to its mounted workspace's file changes again
and re-reads it, once per generation. A remote file watch that ends with its connection therefore stops
instead of polling; one that fails while the host stays connected still retries each second. A local
host constructed with an initial snapshot starts connected at generation 1 so the first window routes
without waiting. `Supports(introducedAt)` answers whether the connected host serves a feature from its
handshake; it is false before the handshake answers and while disconnected.

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
location. Location is never shared between windows or clients; continuing elsewhere is an explicit action:
a serialized link, described with the window's Back/Forward list in [../SPEC.md](../SPEC.md).

A workspace that leaves the host's registry is tombstoned for the window's lifetime (`HostSync.cs`): its Git
selection, its place in the selection history and its view in the frame are dropped at once, an open of it
that was still in flight lands on Project Home instead of entering it, and leaving it afterwards writes no
selection back. The tombstone lifts when a workspace is registered at that path again.

## Tab view state

Each file and diff tab has a `TabView`: the renderer it chose and that renderer's own view state. The
window keeps them in memory by owning workspace and tab id, beside the loaded content and its metadata,
and drops them when the tab closes; they are not written to the profile. Writes name the owning workspace,
so a body that closes after a workspace switch still updates its own tab, and a write for a tab that no
longer exists is ignored. Only the renderer interprets its view state (picture fit and zoom, the image
diff's mode and position, a Markdown view's scroll offset or first visible source line, a rendered
Markdown diff's expanded runs). Choosing another
renderer drops it, and a late report from a renderer the tab has left is ignored. A tab that never chose
follows the best match, so a file that gains or loses a candidate view keeps working. Split or inline and
ignore-whitespace stay diff-only presentation held by the diff pane.

## Toasts

`ToastQueue` is one window's transient notifications, oldest first; `Rendering/ToastStack` draws it. A toast
has a variant (info, success, error), a message, an optional title, its own duration and at most one action
(an Undo receipt). Without a duration, info and success end after five seconds and an error stays until
dismissed. Pushing a notification identical to a visible actionless one returns that one instead of adding
another. Actionable toasts are never coalesced, since their actions name different inverses, and the cap of
five evicts only the oldest actionless ones, so a receipt stays until its duration ends or the user dismisses
it. The queue is never persisted or shared between windows.

## Not yet ported

- Routing the window's gesture notification (layout-cancelled drags, removed workspaces, protocol mismatch,
  a vanished commit) and its error line through the toast queue; they keep their own single surfaces, so
  those messages are neither coalesced nor capped.
- View state for the code editor, the table, the JSON tree and the notebook (their scroll position and
  expansion); those views report none.
- Telling user intent from passive moves in the Back/Forward list: every move to a different location adds
  an entry, so a remote project close or workspace removal that relocates the window is recorded too. The
  observed location is not persisted as a link; the profile keeps its own last-location fields.
