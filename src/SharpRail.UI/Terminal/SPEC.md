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

Accessories select the terminal's agent newline encoding through `SetKeyEncoding`. The view retains
that selection across asynchronous startup and retry. In agent mode, an uncomposed Shift+Enter with
no other modifiers sends ESC+CR when neither kitty nor modifyOtherKeys is negotiated. Negotiated
protocols keep Ghostty's own encoding; default mode also restores Ghostty's own encoding. The pinned
embedding shim reads protocol state under the renderer lock and queues raw input, avoiding clipboard
paste transformation. Native checks capture actual AppKit key bytes for all four cases.

## Boundary

- Owns: attach/detach of one tab to one session, start-failure and retry, takeover notice and take-back,
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
- A session is held by one client and has that client's size. A tab's body attaches yielding
  (`TerminalLaunch.Yield`): it starts or joins a session nobody holds, and where another client holds it the
  body shows "This terminal is in use by another client." with Take over instead of attaching, so merely
  showing a tab never takes a terminal or resizes it. Take over, and the first attach of a terminal created
  in this window, attach plainly: the new attach becomes the recipient at its own grid and the previous
  client shows "This terminal is open somewhere else" with Take it back. A displaced client's input and
  resizes are ignored and its reconnects never take the session back. Upstream takes a terminal over when
  its tab is selected; here taking is always the button, because a phone that opens a workspace must not
  resize the terminal a desktop is using.
- Output is addressed to the attached client only. A fresh view receives a bounded snapshot of the main
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
  terminal in raw mode, attaches over authenticated gRPC streaming, forwards window-size changes and reports
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
  brought back. The view is then reconciled, and again on every snapshot, for every workspace this window
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
is needed. All paths currently require macOS native libraries; other platforms
show an availability message.

The Skia renderer reuses unchanged row pictures and framebuffer pixels, including
when only the cursor moves. Theme, font, geometry, selection and preedit changes
must invalidate affected rows. Cached native text blobs batch compatible ASCII
cells while preserving the grid and existing complex-grapheme behavior. Queued
draw operations retain row resources until released.
The Skia adapter awaits bounded output dispatches (up to 8 KiB, with a 2 ms
cooperative budget checked every 1 KiB); replay follows the same path. Exit is
reported only after output drains, and disposal cancels pending dispatches.
This bounds UI work; the host bounds its own queues (see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#backpressure)). VT state stays UI-thread-owned.
Design provenance and Royal Apps MIT attribution are in
[Ghostty.Avalonia/README.md](../../Ghostty.Avalonia/README.md#skia-rendering-design).

## Validation

Both renderers underline HTTP(S) URLs under the pointer while Command is held
on macOS (Ctrl elsewhere), and modifier-click opens the URL using the system
browser. Soft-wrapped URLs remain one link across rows; explicit newlines remain
boundaries. Ordinary clicks and drags retain terminal selection behavior.

Host checks cover attach idempotency, takeover and displaced-client rejection, replay without the
alternate screen or mouse modes, resume without duplication, busy detection and close. `--terminals` runs
them with the terminal and bottom-panel translations over local and remote hosts. Native acceptance
additionally requires live shell output with nonuniform pixels in Ghostty's IOSurface, keyboard input,
resizing and disposal (`scripts/check-terminal.sh`, `--native-terminal`); headless docking checks alone do
not establish terminal execution. `--ghostty-skia` verifies real rendered colours,
wide/combining text, keyboard and SGR mouse press/release, paste, selection,
scrollback, resize and a real host shell surviving a renderer restart.
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
