# Terminal — terminal tabs in the UI

Upstream: packages/server/src/terminal/SPEC.md @ 4a65ed7f
Upstream: apps/web/src/shell/SPEC.md @ 4a65ed7f
Upstream: apps/web/src/shell/layout/SPEC.md @ 4a65ed7f
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md @ 4a65ed7f
Upstream: apps/web/src/store/SPEC.md @ 4a65ed7f

## Responsibility

The client side of host-owned shells: the terminal tab body (`TerminalView`), the backend seam that
attaches it to a host session (`ITerminalBackend`, `TerminalFactory`), the Ghostty native view
(`GhosttyTerminal`), the in-process socket that serves the app's own sessions to local relays
(`LocalTerminalRelay`) and the `--terminal-relay` child mode (`TerminalRelay`). PTY lifetime, output
recording and replay belong to the host (`Host.Core/PtyTerminalService.cs` behind
`ITerminalService`); tab placement belongs to the window's workspace view (see
[Docking/SPEC.md](../Docking/SPEC.md)).

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
  the tab is the only UI action that ends the shell. Local shells end when the app quits and remote shells
  when their host stops; restored tabs start new shells after a restart.
- Attach is exclusive with takeover. A session has one size, so a new attach becomes the recipient and the
  previous client shows "This terminal is open somewhere else" with Take it back. A displaced client's
  input and resizes are ignored and its reconnects never take the session back; taking back is an explicit
  gesture.
- Output is addressed to the attached client only. A fresh view receives a bounded snapshot of the main
  screen, preceded by the observed private modes except mouse tracking and never containing the alternate
  screen or a mode sequence, then the foreground program is nudged to redraw. A reconnecting client
  resumes from its last output position. Replay and the switch to live output are atomic, so nothing is
  shown twice.
- Metal texture tabs run the SharpRail executable in relay mode as their child. The relay reads endpoint,
  token, session and client from a private one-use file named by `SHARPRAIL_TERMINAL_RELAY`, puts its
  terminal in raw mode, attaches over authenticated gRPC streaming, forwards window-size changes and reports
  the real exit code or a takeover through a status file, because Ghostty's login wrapper does not
  propagate the child's exit code. Local tabs reach the app's host through a private Unix socket that starts
  with the first terminal; remote tabs reach the remote host. A lost connection reconnects for up to 30
  seconds; input typed before the loss is noticed can be lost, as with ssh.
- Each window is one terminal client. Terminals of different tabs are independent and survive workspace
  switches without a second shell.

## Tab lifecycle

A body is created only when its tab is selected in a visible, unfolded group, so hidden tabs and a hidden
bottom panel do not start shells. A shell that cannot start shows its reason and a Retry that restarts the
same tab; an exited shell's tab says so with its exit code. A shell that exits while detached keeps its
final output and status for the next attach. Closing a tab whose shell runs a foreground process asks
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
This bounds UI work, not host-side output queues. VT state stays UI-thread-owned.
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
`--texture-fallback` forces software composition and requires automatic Skia
fallback while preserving the local/remote shell and its exit status.

## Not yet ported

- A host-persisted per-workspace terminal catalog, reserved separately from starting a shell, bounded in
  size, broadcast to every client, and used to seed each client's placement.
- Reviving tabs across a host restart with their last recorded screen.
- Ending a workspace's shells when its worktree is removed.
- A host-configurable replay size.
