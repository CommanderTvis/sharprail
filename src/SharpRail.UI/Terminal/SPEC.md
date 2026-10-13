# Terminal — terminal tabs in the UI

Upstream: packages/server/src/terminal/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/layout/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/store/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

## Responsibility

The client side of host-owned shells: the terminal tab body (`TerminalView`), the backend seam that
attaches it to a host session (`ITerminalBackend`, `TerminalFactory`), the Ghostty native view
(`DirectGhosttyTerminal` for local sessions, `GhosttyTerminal` for remote sessions)
and the remote `--terminal-relay` child mode (`TerminalRelay`). PTY lifetime, output
recording and replay belong to the host (`Host.Core/PtyTerminalService.cs` behind
`ITerminalService`); tab placement belongs to the window's workspace view (see
[Docking/SPEC.md](../Docking/SPEC.md)).

## Plugin accessories and companions

`TerminalView` hosts what plugins contribute to a terminal (W6, W11 in the plugin API). Accessory rows sit below
the surface; a companion pane opens in an embedded split beside it, never as a tab of its own, and which companion
is open per terminal is the window's view state (`WindowProfile.Companions`). Available companions are offered as a
row of buttons above the accessories. `ITerminalBackend.Write` types into the shell as a user would and
`ReadScreen` returns the screen and scrollback as text: the Ghostty backend uses the bridge's existing
`sr_terminal_input` and `sr_terminal_read`, and the checks' headless backend writes to its PTY session and reads its
plain-text view. An accessory's `BufferTail` is the last lines of that text.

The companion starts 360 px wide; its content fills the column as the user drags the splitter.
Closing it returns the whole column to the terminal, and reopening restores the width for that view.
The column owns the width so a fixed-width child cannot leave unused space around a clipped drawing.

Accessories select the terminal's agent newline encoding through `SetKeyEncoding`. The view retains
that selection across asynchronous startup and retry. In agent mode, an uncomposed Shift+Enter with
no other modifiers sends ESC+CR when neither kitty nor modifyOtherKeys is negotiated. Negotiated
protocols keep Ghostty's own encoding; default mode also restores Ghostty's own encoding. The pinned
embedding shim reads protocol state under the renderer lock and queues raw input, avoiding clipboard
paste transformation. Native checks capture actual AppKit key bytes for all four cases.

Projects lists host-catalogued terminals on every client, including workspaces the
window has never opened and terminals whose tab strips are hidden or elsewhere.
Saved host agent titles take precedence over catalog titles in these lists too.
Choosing one reveals its tab and watches another client's live shell without taking
it over. Centre file-tab previews still follow the client's tabs-in-Projects setting.

## Android touch keys

Android's Skia terminal includes a two-row key bar for Esc, Tab, one-shot Ctrl and Alt,
arrows and Home/End/Page Up/Page Down. It preserves input focus and uses the terminal's
negotiated key encoding. Watched and exited terminals do not offer the bar.
Switching to watching detaches the surface from the key-bar panel before moving it
into the read-only scroll viewer.

On Android, every terminal-tab removal asks for confirmation, including idle, exited,
failed or unmounted terminals. Cancel keeps the tab and session; confirmation permits
the requested close. Desktop terminals retain their foreground-process confirmation.

## Boundary

- Owns: attach/detach of one tab to one session, start-failure and retry, the read-only view of a terminal
  another client holds with its take-over and take-back offer,
  exit notice, busy query for close confirmation, clipboard paste handling, theme propagation to Ghostty,
  and the relay's connection handoff.
- Forbidden: starting or ending shells except through `ITerminalService`; deciding placement or which tab
  is selected; putting the host token in argv or in a shell's environment; a WebView rendering fallback.

## Decisions

- A shell is keyed by its session id, never by a window, view or control. The id derives from workspace
  root and tab id (`TerminalLaunch.SessionFor`), so rebuilding a view or opening the same tab in another
  window reattaches to the same shell.
- A shell outlives every client that shows it. Disposing a view or closing a window only detaches; closing
  a tab or its project ends the shell. Project closure releases cached views across its workspaces;
  reopening restores saved tabs with fresh shells. Local shells end when the app quits and remote shells
  when their host stops; restored tabs start new shells showing their last recorded screen after a restart
  (the local host saves it on a graceful quit).
- A session is held by one client and has that client's size; every other client that shows its tab watches
  it. A tab's body attaches yielding (`TerminalLaunch.Yield`): it starts or joins a session nobody holds, and
  where another client holds it the body is a watcher (`ITerminalBackend.Watching`): the holder's screen and
  then its live output, read-only, over a slim bar "Read-only: in use by another client." with Take over. So
  merely showing a tab never takes a terminal, resizes it or sends it anything. Take over, and the first attach
  of a terminal created in this window, attach plainly: the new attach holds the shell at its own grid, and the
  previous holder's view attaches again as a watcher, its bar reading "Read-only: another client took this
  terminal over." with Take it back. Upstream takes a terminal over when its tab is selected; here taking is
  always one deliberate step, because a phone that opens a workspace must not resize the terminal a desktop is
  using.
- Taking over is needed only to type. A watched terminal's keyboard focus is its offer, never its screen: a
  click or tap on the button, or Enter or Space once the tab has focus, takes over, and any other key does
  nothing. Typing does not take over by itself, since focus lands in a tab by navigation alone, and a view that
  loses its terminal while being typed into does not get the keyboard, so the keys that follow cannot take it
  straight back. No soft keyboard opens for a watched terminal. `TerminalView.Write` (plugin accessories, a
  plugin's command) is dropped while watching.
- A watcher is always the Skia renderer (`SkiaTerminal`), also where Settings selects Metal: the Metal
  surfaces size the shell to themselves, so a Metal body yields as before and `TerminalView` attaches again
  with `TerminalLaunch.Watch`, which the factories answer with Skia. The view shows the holder's grid at its
  real cell size (`GhosttySkiaView.FixedGrid`) inside a scroller that starts at the top-left when the pane is
  smaller, and follows the holder's resizes in order with the output. It is not focusable or hit-testable: no
  key, paste, mouse or focus report, resize or terminal query reply leaves it, and the scroller takes the pan
  and wheel gestures; scrollback and selection need a take-over. A watch attach that finds nobody holding the
  session holds it, in the Skia renderer until the tab restarts. Changing the renderer in Settings leaves
  watchers alone.
- A host that predates watching returns a watch attach detached: the body is then hidden and the same bar
  reads "This terminal is in use by another client." (or "This terminal is open somewhere else." for the
  previous holder) with the same button, which is the earlier behaviour.
- Output goes to the holder and to every watcher. A fresh view receives a bounded snapshot of the main
  screen, preceded by the observed private modes except mouse tracking and never containing the alternate
  screen or a mode sequence, then the foreground program is nudged to redraw. A reconnecting client
  resumes from its last output position. Replay and the switch to live output are atomic, so nothing is
  shown twice.
- Local Metal texture tabs use Ghostty's external I/O backend. The app-owned PTY service attaches
  through direct calls; output enters Ghostty's parser under its renderer lock, while input and resize
  callbacks feed an ordered managed queue. No relay child, socket, HTTP/2, protobuf or RPC is involved,
  including busy queries, close and fallback to Skia. The local factory has no relay or endpoint input.
  Disposing the native surface joins its I/O thread before releasing callback state; output calls and
  disposal are serialized. The host still owns the shell and replay, exit and takeover semantics.
- Remote Metal texture tabs run the SharpRail executable in relay mode as their child. The relay reads endpoint,
  token, session and client from a private one-use file named by `SHARPRAIL_TERMINAL_RELAY`, puts its
  terminal in raw mode, attaches over authenticated WebSocket RPC streaming, forwards window-size changes and reports
  the real exit code or a takeover through a status file, because Ghostty's login wrapper does not
  propagate the child's exit code. Remote tabs reach the remote host. A lost connection reconnects for up to 30
  seconds; input typed before the loss is noticed can be lost, as with ssh.
- Each window is one terminal client. Terminals of different tabs are independent and survive workspace
  switches without a second shell.

## Shared terminals

Which terminal tabs a workspace has is the host's catalog (`ITerminalCatalogService`, see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#catalog)), so every window of every client shows
the same ones; where a tab sits stays each window's own. `Workbench` takes the catalog from its composition root
(the local PTY service, a remote adapter, or an in-memory one when there is neither), keeps one subscription for
all its windows and hands them the latest snapshot; a change's reply and the subscription race, and the higher
revision wins, except that the first snapshot of a subscription always applies because a restarted host counts
again. The window half is `TerminalTabs.cs`; placement is `LayoutSession.ReconcileTerminals`
([Docking/SPEC.md](../Docking/SPEC.md)).

- Mounting a workspace announces it (`OpenWorkspaceAsync`), once per connection. The window brings its own
  terminal tabs when its view never met the catalog or the host does not know the workspace, so saved layouts
  with older random tab ids are adopted; otherwise it brings none, and a tab closed elsewhere meanwhile is not
  brought back. An empty first view announces no tabs and the host preserves that empty workspace.
  The view is then reconciled, and again on every snapshot, for every workspace this window
  announced, shown or not.
- A terminal created here (New terminal, a plugin's terminal) is placed at once but its body waits, showing
  "Loading terminal…", until `ReserveAsync` has recorded it; a refused reservation removes the tab and reports
  why. Bodies of a workspace likewise wait for its first reconciliation, so no shell starts for a tab the host
  already closed.
- A terminal tab that leaves the layout by this window's doing is closed through `CloseTabAsync`, mounted or
  not, which removes it for every client and ends its shell. A tab the catalog removed only loses its body here.
- The session behind a tab is `TerminalTab.SessionFor(root, key)`, the same for every client.
- A host that predates the catalog, or refuses a workspace's tabs, leaves that workspace's terminals this
  window's own. A new connection generation announces the shown workspace again.
- Not ported: dismissing a busy-close confirmation when another client closes the same terminal meanwhile;
  the confirmation then closes a tab that is already gone.

## Tab lifecycle

A body is created only when its tab is selected in a visible, unfolded group, so hidden tabs and a hidden
bottom panel do not start shells. A shell that cannot start shows its reason and a Retry that restarts the
same tab; an exited shell's tab says so with its exit code. A shell that exits while detached keeps its
final output and status for the next attach. Removing a workspace's worktree ends every shell started in
it: the composition root passes the host's removal signal to the terminal service, in the local app and
in a remote host alike. Closing a tab whose shell runs a foreground process asks
first through the docking removal veto (`TerminalTabs.cs`); an idle or exited tab closes immediately.
Removing a group or applying a preset moves terminal tabs but preserves their session identity.
Appearance changes keep live terminal views (`DocumentCache.cs`); Ghostty receives the new background,
foreground, ANSI palette, cursor and selection colours and its contrast floor without restarting the
shell. Ordinary themes preserve terminal foreground colours without minimum-contrast adjustment
(Ghostty's default of 1); high-contrast themes use a minimum ratio of 7.
Reverse-screen mode (DECSCNM) swaps the default text and background colours together,
and resetting the mode restores both.
The Metal bridge loads theme values through a temporary Ghostty configuration file,
then deletes it after applying the configuration; it does not modify user config files.

## Platform

Settings → Terminal also offers the host's replay size (Off, 16 KB, 64 KB, 256 KB, 1 MB): how much recent
output a terminal keeps so a rebuilt view shows its screen again. It is host state shared by every client,
applies to terminals opened afterwards, and the selection moves only when the host's broadcast arrives.

Settings chooses Metal texture (default) or Skia (fallback) and persists the choice in app preferences.
Changing it reattaches existing views to their same host session; it must not end
the shell. Appearance changes update each renderer without reattaching.
The integration lives in the independent `Ghostty.Avalonia` project.

The library retains `GhosttyView` for NSView consumers and benchmarks; SharpRail does not instantiate or offer it.
Texture creation or drawing failure automatically switches that view to Skia on
the same host session, without changing the saved preference. Clipboard image
paste saves PNG files under the profile's `clipboard` directory.
Both renderers handle OSC 52 text clipboard writes from local and remote shells.
Writes are allowed and reads require confirmation on the client; clipboard data
is never taken from the host machine. The macOS system pasteboard serves all
selection destinations. Invalid UTF-8 writes leave it unchanged.

In Metal texture mode, Ghostty renders to IOSurface textures that Avalonia imports
directly into its Skia compositor. Completed targets are leased while Skia samples
them, with GPU completion before Ghostty can reuse them. No export copy,
snapshot copy or CPU readback is required. Avalonia handles input, clipping and overlays, and the
Ghostty platform view stays unparented. There is no hosted native terminal view.
The application prefers Avalonia's Metal backend; unsupported texture import is
handled by Skia fallback. Failure of that fallback uses the start-failure/retry flow.
Frame-ready notifications invalidate the control without polling. The compositor
wraps a leased source texture for each draw and waits for its GPU read to finish;
this favors bounded memory and explicit ownership over maximum GPU overlap.
The latest completed target and in-flight readers remain bounded by Ghostty's
three render targets. Resize, remount and disposal must release old targets. The native Ghostty build preserves enlarged scrollback
page allocations when recycling them, without reducing retained history limits.

In Skia mode, libghostty-vt owns terminal state and input encoding; Avalonia draws
changed rows into retained Skia pixels and owns focus, clipboard, selection and composition input. On macOS,
Option–Left/Right send ESC b/f for word navigation, matching Ghostty's bindings. The
adapter attaches directly to `ITerminalService`, queues input in order, uses the
arranged grid size and propagates exit/takeover to `TerminalView`. No relay child
is needed. Windows x64 also builds the VT library and always selects Skia, for
both local and remote hosts, even with a saved Metal preference. Its local host
owns ConPTY sessions. Native Metal libraries remain macOS-only.

Terminal file links are made workspace-relative using the host's path syntax,
without interpreting a remote Windows or POSIX path through the client's filesystem.

The Skia renderer reuses unchanged row pictures and framebuffer pixels, including
when only the cursor moves. Theme, font, geometry, selection and preedit changes
must invalidate affected rows. Cached native text blobs batch compatible ASCII
cells while preserving the grid and existing complex-grapheme behavior. Queued
draw operations retain row resources until released.
The Skia adapter reads live output independently into a bounded queue (128 pieces of at most
8 KiB) and drains available pieces in one UI dispatch, up to 256 KiB or a cooperative 2 ms
budget. Invalidations within that dispatch coalesce; small transport packets do not each wait
for a separate redraw. Holder grid changes remain ordered with watched output and apply before
parsing its bytes. Replay yields after 8 KiB or 2 ms, checked every 1 KiB. Exit is reported only
after output drains; disposal cancels both the reader and pending dispatches. No bytes are dropped.
This bounds UI work; the host bounds its own queues (see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#backpressure)). VT state stays UI-thread-owned.
Design provenance and Royal Apps MIT attribution are in
[Ghostty.Avalonia/README.md](../../Ghostty.Avalonia/README.md#skia-rendering-design).

## Validation

Both renderers underline HTTP(S) URLs under the pointer while Command is held
on macOS (Ctrl elsewhere), and modifier-click opens the URL using the system
browser. Soft-wrapped URLs remain one link across rows; explicit newlines remain
boundaries. Right-click over a web URL offers Open link alongside the terminal clipboard menu,
without Command/Ctrl. The action opens the URI captured at right-click even if output changes. Mouse
tracking forwards RMB to the program; Shift bypasses tracking for the menu. Native Metal resolves
OSC 8 label targets as well; browser Skia inherits HTTP(S) text detection. The separate restty canvas
has no exposed point-to-link query and does not yet offer this action. Ordinary clicks and drags retain terminal selection behavior.

Host checks cover attach idempotency, takeover and displaced-client rejection, watching, replay without the
alternate screen or mouse modes, resume without duplication, busy detection and close. `--terminals` runs
them with the terminal and bottom-panel translations over local and remote hosts. Native acceptance
additionally requires live shell output with nonuniform pixels in Ghostty's IOSurface, keyboard input,
resizing and disposal (`scripts/check-terminal.sh`, `--native-terminal`); headless docking checks alone do
not establish terminal execution. `--ghostty-skia` verifies real rendered colours,
wide/combining text, keyboard and SGR mouse press/release, paste, selection,
scrollback, resize, a real host shell surviving a renderer restart, and a watching view of a held shell
(the holder's grid in a smaller pane, its resize, no keys or query replies sent, Enter on the offer).
`--native-texture` requires real imported pixels with theme changes, Avalonia
overlays and parent clipping, native keyboard/clipboard input, Retina resize,
remounting and texture/Skia switches retaining local and remote shells.
`--native-direct` runs the local direct and remote session checks independently of renderer pixel probes.
It verifies zero local HTTP requests, shell retention, input, resize, takeover and exit.
`--texture-fallback` forces software composition and requires automatic Skia
fallback while preserving the local/remote shell and its exit status.

## Not yet ported

- Host-side attribution of accepted input to workspace activity. Local and remote terminal paths must
  count only input delivered to the PTY; viewing output, reconnecting or a displaced client's ignored
  write never reactivates a settled workspace (see
  [Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#not-yet-ported)).
- `TerminalKeyEncoding.AgentNewline`: the bridge has no key-encoding switch, so `SetKeyEncoding` is a no-op and
  Shift+Return keeps Ghostty's own encoding.
- Faint-cell blanking in `BufferTail(omitFaint: true)`: libghostty's text read carries no cell attributes, so faint
  placeholders are returned as text.

## File references

Claude Code, Codex and ordinary shells share the terminal's file-link interaction. Holding Command
on macOS (Ctrl elsewhere) over an existing file underlines it; modifier-click opens its source in
SharpRail, positioning the caret at the supplied one-based line and column. Ordinary selection stays
unchanged. Relative paths and bare filenames resolve against the shell's current working directory;
there is no recursive filename search. Absolute paths, home-relative paths, local `file://` URLs,
`:line`, `:line:column`, `:start-end` and `: (line, column)` positions are accepted. The start of a
range is the navigation target. Directory candidates aid parsing but are not editor links.

The terminal host runs a bounded character scanner and checks filesystem existence off the dispatcher.
Unknown working directories disable relative links. Remote sessions resolve on their host, including
home expansion; the client never probes its own filesystem for them. The scanner follows the general
approach of [IntelliJ's terminal filters at bf3ace2df](https://github.com/JetBrains/intellij-community/tree/bf3ace2df/plugins/terminal/backend/src/com/intellij/terminal/backend/hyperlinks/filter),
without a regex path matcher. Spaces in absolute paths retain the longest existing candidate; prose
punctuation is excluded from the highlighted span.

Only the hovered logical line is submitted after a short debounce. Both renderers map Unicode text
and soft wraps back to cells. Hover results are cached for at most one second in a bounded cache;
changed text, pointer exit, modifier release and disposal reject pending results. Activation rechecks
existence and the current directory. HTTP(S) links retain their browser action. The terminal library
exposes resolver/navigation callbacks and has no host or application dependency.

`--terminal-links` exercises parser boundaries, positions, existing/missing targets, working-directory
changes through direct and WebSocket RPC adapters, and Skia wrapped-path hover and click. `--native-file-links` verifies Metal Unicode and wrapped-line detection, modifier-click coordinates,
stale-link clearing and retained web-link hover.

Browser composition defaults to restty; see [Browser](../../SharpRail.Browser/SPEC.md).
Its initialization fallback uses the shared Skia backend over remote adapters and WASM libghostty-vt.
In that fallback, pasted PNG images are uploaded to the host terminal service, and only the returned host path is typed.
