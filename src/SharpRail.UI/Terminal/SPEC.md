# Terminal — terminal tabs in the UI

Upstream: packages/server/src/terminal/SPEC.md @ 12830b08
Upstream: apps/web/src/shell/SPEC.md @ 12830b08
Upstream: apps/web/src/shell/layout/SPEC.md @ 12830b08
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md @ 12830b08
Upstream: apps/web/src/store/SPEC.md @ 12830b08

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
  is selected; putting the host token in argv or in a shell's environment; a CPU or WebView rendering
  fallback.

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
- Every Ghostty tab runs the SharpRail executable in relay mode as its child. The relay reads endpoint,
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
shell.

## Platform

Ghostty (libghostty in native AppKit views hosted by Avalonia) owns emulation, fonts and Metal rendering
and requires an available Metal device. Keyboard input, composition, selection, scrolling, clipboard and
Retina resizing pass through the native view. Command-V pastes text, or saves PNG/TIFF clipboard images
as distinct PNG files in the profile's `clipboard` directory and pastes a shell-quoted path; saved images
outlive the terminal. Other operating systems show an explicit availability message instead of a body.

## Validation

Host checks cover attach idempotency, takeover and displaced-client rejection, replay without the
alternate screen or mouse modes, resume without duplication, busy detection and close. `--terminals` runs
them with the terminal and bottom-panel translations over local and remote hosts. Native acceptance
additionally requires live shell output with nonuniform pixels in Ghostty's IOSurface, keyboard input,
resizing and disposal (`scripts/check-terminal.sh`, `--native-terminal`); headless docking checks alone do
not establish terminal execution.

## Not yet ported

- A host-persisted per-workspace terminal catalog, reserved separately from starting a shell, bounded in
  size, broadcast to every client, and used to seed each client's placement.
- Reviving tabs across a host restart with their last recorded screen.
- Ending a workspace's shells when its worktree is removed.
- A host-configurable replay size.
