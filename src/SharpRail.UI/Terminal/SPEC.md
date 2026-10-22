# Terminal — terminal tabs in the UI

Upstream: packages/server/src/terminal/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/layout/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/store/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))

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

`BufferTail(lines, omitFaint: true)` reads `ITerminalBackend.ReadScreen(true)`. The Skia renderer answers from the
visible rows' cells (`GhosttySkiaView.ReadViewport`, no scrollback) with faint cells blanked, plus an inverse cell
directly before a faint one, which is the agent's own cursor drawn on a placeholder's first glyph. Faint is
treated as hint whatever it says: agents do not draw typed text faint. The texture renderer, the browser and the
default interface implementation return plain text. Until now the argument was ignored everywhere. The checks feed
the SGR sequences agents emit (2 faint, 7 inverse) through a real `GhosttySkiaView`; they were not captured from
a live Claude Code or Codex session.

An accessory that types a command into an agent (a slash command, a picker drive) takes `HoldInput()` first and
disposes the handle in a `finally` when the entry is complete, submitted included. While a hold is in force
`TerminalView` swallows the user's key, composed-text and non-primary pointer events on the way down (tunnelling
handlers, so every renderer is covered and none sees them) and shows "Input paused while SharpRail types…" in the
corner. Keys typed meanwhile are dropped, not queued: replaying them after the command would type into whatever the
agent shows next. The driver's own `Write` is unaffected. Holds nest and a handle disposes once. A hold also ends by
itself when the terminal restarts, its shell exits, the view is disposed (closing the tab) and after 30 seconds, so a
driver that never returns cannot leave the terminal locked. Switching to another tab or workspace leaves the hold
with the driven terminal: the driver either finishes or cancels (Claude Code cancels), and either way releases it.
`--terminal-resume-hint` covers the lifecycle against a real `TerminalView`.

The companion starts at 45% of the terminal's width, as in the fork; its content fills the column as the user drags the splitter.
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

## Session revival hint

A backend that acknowledges writing an offered, non-submitted revival prefill shows “Press Enter to
resume this tab’s previous session.” as a noninteractive overlay. It does not submit the command,
change the terminal grid or move focus. Ordinary pasted text, submitted prefills and read-only watchers
never create it, nor does a revival the host runs as the terminal's program, which offers nothing to type. User input or cancellation, detaching the view, exit and disposal dismiss it; late
write acknowledgements cannot bring it back after input. Automatic terminal-query replies are not
user input. The hint remains generic: plugins own revival commands and policy.

The optional client-side state is shared through renderer fallback. Remote Metal communicates only
prefill readiness through a sidecar beside its private relay status; the UI reads it in the background,
without adding a host operation or exposing command text. Browser DOM input is distinguished from
terminal transport replies for the same reason. Its DOM surface carries the same noninteractive
overlay so a native-control layer cannot cover the hint. Failure to write the optional relay readiness
sidecar never replaces a working terminal with an error.

## Android touch keys

Android's Skia terminal includes a two-row key bar for Esc, Tab, one-shot Ctrl and Alt,
arrows and Home/End/Page Up/Page Down. It preserves input focus and uses the terminal's
negotiated key encoding. Watched and exited terminals do not offer the bar.
Switching to watching detaches the surface from the key-bar panel before moving it
into the read-only scroll viewer.

Android watched terminals fit their font to the pane between 10 and 13 device-independent
pixels. Wide desktop grids may still need horizontal scrolling at the 10-pixel readability
floor. Rotation and the holder's grid changes recalculate the fit without resizing the host
terminal or taking ownership; active terminals keep their usual font.

On Android, every terminal-tab removal asks for confirmation, including idle, exited,
failed or unmounted terminals. Cancel keeps the tab and session; confirmation permits
the requested close. Desktop terminals retain their foreground-process confirmation.

## Boundary

- Owns: attach/detach of one tab to one session, start-failure and retry, the read-only view of a terminal
  another client holds with its take-over and take-back offer,
  exit notice, busy query for close confirmation, clipboard paste handling, theme propagation to Ghostty,
  and the relay's connection handoff.
- Forbidden: starting or ending shells except through `ITerminalService`; deciding placement or which tab
  is selected; putting the host token in argv or in a shell's environment.

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
  The holder's grid is applied before parsing the initial replay, including alternate-screen
  cursor positions outside the smaller client's viewport, so later incremental updates augment
  the complete replay rather than a clipped initial screen.
- A host that predates watching returns a watch attach detached: the body is then hidden and the same bar
  reads "This terminal is in use by another client." (or "This terminal is open somewhere else." for the
  previous holder) with the same button, which is the earlier behaviour.
- Output goes to the holder and to every watcher. A fresh view receives a bounded snapshot of the main
  screen, preceded by the observed private modes except mouse tracking. If the live shell is on the
  alternate screen, the host also supplies its retained live output and modes; watchers do not require
  the foreground program to redraw. A reconnecting client
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
  The user's close of a live terminal (its tab's close button, Mod+W, the tab menu, redo) goes through
  `CloseTabForUndoAsync` instead while the host's undo timeout is above zero: its view only detaches and the
  host keeps the shell (see Undo close below).
- The session behind a tab is `TerminalTab.SessionFor(root, key)`, the same for every client.
- A host that predates the catalog, or refuses a workspace's tabs, leaves that workspace's terminals this
  window's own. A new connection generation announces the shown workspace again.
- Not ported: dismissing a busy-close confirmation when another client closes the same terminal meanwhile;
  the confirmation then closes a tab that is already gone.

## Undo close

Ghostty 1.2's undo close (`undo`/`redo` keybind actions, `undo-timeout`, default 5 s, zero disables; macOS
only there, bound to Cmd+Z and Cmd+Shift+T for undo and Cmd+Shift+Z for redo, all "performable" so the key
passes through when there is nothing to undo; each operation keeps its own timeout).

- The timeout is the host setting `TerminalUndoCloseSeconds` (Settings › Terminal › Undo close: Off, 5 s
  default, 15 s, 1 min; at most 600), shared by every client. The host reads it at each close.
- A user close of a document or terminal tab (`LayoutSession.TabClosing`) records the tab, its group, index
  and pane, and shows a toast "Closed <title> (N s)" with Undo and a live countdown for the timeout. A live
  terminal in a host-shared workspace keeps its session: the host removes the tab from the catalog (other
  clients see it gone) and ends the shell only when the timeout passes. An exited or failed terminal, one of
  a window without a catalog, a tool tab, and every close the layout makes for other reasons (catalog
  reconciliation, moves, removed groups) are not undoable this way.
- Undo (`WindowShortcuts.UndoClose`: Mod+Z or Mod+Shift+T; the toast's Undo; Edit › Undo Close Tab on
  macOS) reopens the most recent unexpired close of the shown workspace in place and selects it. A terminal
  is reserved again, which the host treats as restore, and its view attaches to the same session, screen and
  output written meanwhile included; other clients get the tab back beside their others. A document reopens by
  its path and reloads from disk, so edits a close discarded are not restored.
- Redo (Mod+Shift+Z) closes the most recently reopened tab again through the ordinary close, undoable again.
- Like Ghostty's performable bindings the chords act only when there is something to undo or redo; a text box
  or editor keeps Mod+Z for its own undo, and off macOS a focused terminal keeps Ctrl+Z (suspend) and the
  chords stand aside. On macOS Cmd+Z reaches undo close even from a terminal, as in Ghostty, since terminal
  input ignores Command chords.
- Closing the window ends the sessions it kept for undo; stopping the host ends any left.
- `--shared-terminals` (and `--terminals`) cover close → toast → Cmd+Z restoring the same live session in place
  for both clients, Ctrl+Z left to a focused terminal, redo, and zero turning undo off; host behaviour is covered
  by `TerminalUndoCloseChecks.cs`.

## Tab lifecycle

Workspaces start empty; explicit terminal and agent actions create terminal tabs in workspace groups.
A body is created only when its tab is selected in a visible, unfolded group, so hidden tabs do not start shells. A shell that cannot start shows its reason and a Retry that restarts the
same tab. By default an exited shell's tab closes like Ghostty's: the host removes it from the catalog, so
it leaves every client without the busy confirmation, its group shrinks, and a focused terminal hands
focus to the tab its group selects next (`TerminalTabs.cs`). An abnormal exit (Host Core `Terminals.SPEC.md`,
Exit) or Settings › Terminal › "When the shell exits" set to keep leaves the tab saying so with its exit code. A shell that exits while detached keeps its
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
That file sets `window-colorspace = display-p3`: Ghostty's output is Display P3 and would otherwise
convert the sRGB theme into it, while Avalonia composes the texture untagged, as it draws its own
brushes. Taking theme values as written keeps the terminal background byte-identical to Avalonia chrome
of the same colour on every display; `--native-texture` compares the texture with an Avalonia swatch
rather than an absolute value, which changed with the display's colour profile.

## Program signals

Progress bars, bell marks and program notifications do not come from libghostty's apprt actions: the Metal
renderers' native `progress_report`/`desktop_notification`/`ring_bell` callbacks are not wired, and the Skia,
Android and Browser renderers have none. The host parses the same sequences from the PTY output, so all renderers
and remote viewers agree without a native rebuild (`TerminalSignalBoard.cs`;
[UI SPEC](../SPEC.md#program-progress-notifications-and-bells)).

## Platform

Settings → Terminal also offers the host's replay size (Off, 16 KB, 64 KB, 256 KB, 1 MB): how much recent
output a terminal keeps so a rebuilt view shows its screen again. It is host state shared by every client,
applies to terminals opened afterwards, and the selection moves only when the host's broadcast arrives.
"When the shell exits" offers Close the tab (like Ghostty, default) or Keep the tab with an exit notice:
the host setting `terminal-keep-after-exit`, shared by every client and applied to the next exit.

Settings → Terminal also has the host's Shell integration switch (on by default; see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#decisions)); it applies to shells started afterwards.

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
Frame-ready notifications invalidate the control without polling, at most once per composited
frame per view: Ghostty's renderer thread sets an atomic flag and posts one cached callback, and
the flag clears only when the compositor next samples the texture, so later frames in that window
are covered by that draw and a frame completing after it posts again (the last frame of a burst is
never lost). The callback allocates no closure; frame counts and texture size are recorded on the
render thread, and only the first draw posts (to complete readiness). A view that is not
effectively visible stops posting until it is composed or shown again, and its link highlights
stay dirty until then. Link state shares two cursors and assigns one only on a change: each new
`Cursor` owns a native platform cursor released only by its finalizer through a dispatcher post.
`--native-frame-pump` measures this: two terminals stream at a fixed rate for 20 s (in-process
8 KB per 2 ms each, then two embedded-host PTYs running a fixed-rate producer), one shown and one
hidden. Recorded 2026-10-22 on an Apple M4 Pro (ProMotion), two runs each, before → after: shown
view posts 221–230/s → 95–107/s (equal to its composited frames/s); hidden view 113–119/s → 0;
in-process allocation 1.24 → 0.75 MiB/s; host-path allocation 20.0–20.4 → 13.4–16.9 MiB/s, gen0
2.8–2.9/s → 1.9–2.4/s, GC pause 71–72 ms → 58–59 ms per 20 s; longest dispatcher stall
17–30 ms → 10–15 ms. The remaining host-path garbage is output byte arrays copied per PTY read and
per recorder push in the host, not UI notifications. The compositor
wraps a leased source texture for each draw and waits for its GPU read to finish;
this favors bounded memory and explicit ownership over maximum GPU overlap.
The latest completed target and in-flight readers remain bounded by Ghostty's
three render targets. Resize, remount and disposal must release old targets. The native Ghostty build preserves enlarged scrollback
page allocations when recycling them, without reducing retained history limits.
Reusing the latest target must leave a completed frame available to the compositor
until another target completes. Only Ghostty's encoding worker waits for that
replacement; compositor acquisition stays nonblocking. GPU failures and disposal
wake the producer, while outstanding read leases remain protected. Scrolling down
at the bottom must not expose blank terminal frames or disable ordinary scrolling.

In Skia mode, libghostty-vt owns terminal state and input encoding; Avalonia draws
changed rows into retained Skia pixels and owns focus, clipboard, selection and composition input. On macOS,
Option–Left/Right send ESC b/f for word navigation, matching Ghostty's bindings. The
adapter attaches directly to `ITerminalService`, queues input in order, uses the
arranged grid size and propagates exit/takeover to `TerminalView`. No relay child
is needed. Windows x64 and ARM64 also build the VT library and always selects Skia, for
both local and remote hosts, even with a saved Metal preference. Its local host
owns ConPTY sessions. Native Metal libraries remain macOS-only.

Terminal file links are made workspace-relative using the host's path syntax,
without interpreting a remote Windows or POSIX path through the client's filesystem.

Metal and Skia also resolve standalone hexadecimal commit ids (4–64 digits) against the terminal's
workspace repository. Only a unique existing commit becomes a link; URLs and existing files take
precedence, and missing, ambiguous, noncommit and gitless targets remain plain text. Lookup uses
the targeted host commit operation in a separate workspace session off the dispatcher, never a
history catalog. Existing commits are persistently underlined throughout the visible viewport,
without requiring a modifier or pointer hover. Visible-text lookups are debounced off-thread and
bounded to 32 cached viewport texts (30 seconds for positive results, one second for empty results);
Metal scans dirty viewports at most five times per second. Output changes, detach and disposal reject
superseded work. Modifier-click and the terminal's Open link context action (right-click without Command/Ctrl) revalidate the target
and open the existing kept continuous Changes tab for that commit. Hover results are bounded to
16 logical lines for one second and reject superseded requests. Leaving the workspace or closing
the renderer prevents stale navigation. Restty's canvas runtime has no custom link-provider seam;
these commit links apply to the browser's Skia fallback, not its Restty renderer.

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
tracking forwards RMB to the program; Shift bypasses tracking for the menu, and right-click on an underlined resolved reference (a validated file or commit hash) always opens the menu, as a modifier-click on a link wins over reporting. Native Metal resolves
OSC 8 label targets as well; browser Skia inherits HTTP(S) text detection. The separate restty canvas
has no exposed point-to-link query and does not yet offer this action. Ordinary clicks and drags retain terminal selection behavior.

Host checks cover attach idempotency, takeover and displaced-client rejection, watching, replay without the
alternate screen or mouse modes, resume without duplication, busy detection and close. `--terminals` runs
them with terminal translations and checks for empty startup and explicit tab-group placement over local and remote hosts. Native acceptance
additionally requires live shell output with nonuniform pixels in Ghostty's IOSurface, keyboard input,
resizing and disposal (`scripts/mise run check:terminal`, `--native-terminal`); headless docking checks alone do
not establish terminal execution. `--ghostty-skia` verifies real rendered colours,
wide/combining text, keyboard and SGR mouse press/release, paste, selection,
scrollback, resize, a real host shell surviving a renderer restart, and a watching view of a held shell
(the holder's grid in a smaller pane, its resize, no keys or query replies sent, Enter on the offer).
`--native-texture` requires real imported pixels with theme changes, Avalonia
overlays and parent clipping, native keyboard/clipboard input, Retina resize,
remounting and texture/Skia switches retaining local and remote shells.
`--native-scroll` isolates native target-reuse/lease and failure/disposal handoffs,
then exercises fractional downward wheel input at the bottom in a real Metal
window. Draws retain a completed texture, the screen stays unchanged, ordinary
upward scrolling still moves the viewport and downward scrolling returns it.
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
- Faint-cell blanking in `BufferTail(omitFaint: true)` for the Metal texture renderer: its text read is plain, so
  faint placeholders come back as text and the draft guards recognise them by wording instead (see
  [Agent.UI](../SharpRail.Plugins.Agent.UI/SPEC.md)). The Skia renderer does blank them (below).

## File references

Claude Code, Codex and ordinary shells share the terminal's file-link interaction. Holding Command
on macOS (Ctrl elsewhere) over an existing file underlines it; modifier-click opens its source in
SharpRail, positioning the caret at the supplied one-based line and column. Ordinary selection stays
unchanged. Relative paths and bare filenames resolve against the shell's current working directory;
there is no recursive filename search. Absolute paths, home-relative paths, local `file://` URLs,
`:line`, `:line:column`, `:start-end` and `: (line, column)` positions are accepted. The start of a
range is the navigation target. Directory candidates aid parsing but are not editor links.
Existing files, including bare names such as `DEVELOPMENT.md` and `./DEVELOPMENT.md`, are also
underlined throughout the visible viewport without a modifier, through the same visible-text
resolution, debounce and cache as commit ids; files take precedence over commits. Words that do not
name an existing file (`1.13.0`, `github.com`, `e.g.`, a sentence-ending `done.`, branch names) stay plain.
A plain click (without dragging) on an underlined file opens it the same way as modifier-click, in a
SharpRail document tab at its line and column; files outside the workspace open as other external
files do. Selection and mouse reporting still receive the press.

A terminal link never opens a file in Finder or another application. Ghostty's own links (Command-click
on an OSC 8 hyperlink: `file://` URLs that `ls --hyperlink` and hyperlink-aware tools print) used to reach
NSWorkspace and opened Finder or the file's default application; the Metal bridge now hands every such
URL to the view, which opens `file:` URLs through the terminal host's resolver and the document tab and
sends only web and other schemes to the system launcher. No explicit "Reveal in Finder" action exists.

Underlines are part of the rendered frame on every renderer, so they stay on their text while output
streams, the view scrolls or resizes. Skia marks cells of the snapshot it paints. Metal (local and
remote) passes the underlined cells to Ghostty's renderer, which tracks them in screen coordinates and
draws them with the cell's style in the same frame, dropping a cell whose text changed until the next
resolution. The earlier Avalonia overlay was positioned on a 200 ms timer from whatever cell state was
current then, so it lagged and jumped against the Metal texture.

The terminal host runs a bounded character scanner and checks filesystem existence off the dispatcher.
Relative names resolve against the terminal process's directory (the shell's, unaffected by a
foreground agent such as Claude Code), falling back to the launch directory on POSIX when the probe
fails; Windows, where the directory is never known, links absolute paths only. Absolute paths outside
the workspace and `~/` paths link whenever the host can read the file. Directories and nonexistent
names stay plain. Remote sessions resolve on their host, including
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
changes through direct and WebSocket RPC adapters, Skia wrapped-path hover and click, and the
renderer's no-modifier underlines for bare and `./` file names, prose false positives and cwd changes. On macOS it
also starts `--native-terminal-links` in a child process: a script launched as the session's program
(no shell, no cwd reports) draws a Claude Code-style transcript on the alternate screen with cursor
positioning and SGR styling, and Metal (local), Skia (local) and Metal (remote relay) backends built
by `TerminalBackends` exactly as the app composes them (the local host behind `LoopbackTerminals`)
must underline every listed form (bare, `./`, relative, `:line`, `:line:col`, absolute inside and outside the
workspace, `~/`) with exact ranges, leave directories and missing names plain, and open line and column
on activation, including a plain click on the underlined text through the view's pointer handling, with
no call to the system launcher seam. It then Command-clicks an OSC 8 `file://host/path` hyperlink in a
Metal view (no mouse tracking) and requires the document callback, not the launcher, while a web
hyperlink still reaches the launcher; and it streams red link lines and wheel-scrolls Metal and Skia
views, capturing the check window repeatedly: every underline run in each captured frame must be the
link's own colour (drawn by the renderer) with link text directly above it in that frame. Before the
fix the Metal frames carried the foreground-coloured overlay. Every `ITerminalService` wrapper must forward `FindFileLinksAsync`: the interface's
empty default once silently disabled all links in the app while direct-host checks passed. `--loopback-terminals`
requires `LoopbackTerminals` and `LocalTerminalAdapter` to implement every `ITerminalService` member themselves
(the throwing `SaveClipboardImageAsync` default once refused every local clipboard image) and pastes a PNG
through the app's local wiring. `--native-file-links` verifies Metal Unicode and wrapped-line detection, modifier-click coordinates,
stale-link clearing and retained web-link hover.

Browser composition defaults to restty; see [Browser](../../SharpRail.Browser/SPEC.md).
Its initialization fallback uses the shared Skia backend over remote adapters and WASM libghostty-vt.
In that fallback, pasted PNG images are uploaded to the host terminal service, and only the returned host path is typed.

## Dropped attachments

Plugin accessory controls advertise whether their agent accepts files and provide the prompt insertion
callback through the kit's `FileDrop` attached properties. The workbench resolves tree payloads against
their source workspace, shares native external files directly with a local agent, and uploads external
bytes for remote/browser agents. Host paths from another workbench are refused. Files and folders may
be dropped together; the plugin owns their syntax. The surface highlights accepted drops. Uploads show
progress with Cancel. Completion checks the current workspace, accessory and terminal ownership again;
a failed, exited, detached or watching terminal never accepts a drop or takes over implicitly.

## Recorded native measurements and acceptance

The saved final aggregates and harnesses under [benchmarks/ghostty](../../../benchmarks/ghostty)
record the 2026-10-01 direct-texture comparison on Apple M4 Pro / 24 GiB, AC power, a 960×540-point
window at 2×, 480 redraws at 60 Hz and three 67,113,264-byte output bursts. Three fresh-process RAM
trials measured 262.0 MiB mean active footprint for direct sampling versus the saved pooled-texture
356.9 MiB and hosted-NSView 236.2 MiB baselines. Ten seconds after disposal measured 118.0 MiB versus
100.5 MiB for NSView. Separate presentation trials measured median/p95 24.5/30.5 ms versus the saved
NSView 13.3/20.5 ms; redraw CPU was 18.8% versus 9.3% of one core. Prior baselines were reused, not
rerun. This is one renderer control without workbench, relay or network overhead; background machine
activity was not exclusively isolated, and WindowServer timestamps are not physical-pixel or
keyboard-to-photon latency. These are dated measurements, not a current release performance claim.

The earlier multi-gigabyte scrollback growth was traced to enlarged Ghostty pages recycled with a
smaller allocation layout. Preserving allocation size/capacity fixed that native leak; export-buffer
pooling alone did not. Removing copy/snapshot textures then reduced steady graphics storage. Neither
change implies that all graphics-driver caches return to the cold baseline on disposal.

The 2026-10-15 real-window --native-scroll pass on Apple M4 Pro covered 300 fractional downward
scroll events at the bottom with no unavailable draws and unchanged viewport pixels, upward scrolling
and pixel-exact return, plus lease/failure/recovery/stop cases. That pass did not complete the broader
--native-texture theme-color assertion or --native-direct initial renderer-creation wait. Physical
trackpad/video reproduction and complete current local/remote native parity remain separate gates.
Physical IME and accessibility acceptance cannot be inferred from synthetic input or pixel checks.

On 2026-10-22 the native texture and terminal checks were found to exit 0 after a failed scenario
(the lifetime's `Shutdown(1)` result was discarded); they now exit 1. With that visible, the
--native-texture theme assertion passed only on a display whose profile was close to Display P3; on
another display the texture background was sRGB(18, 52, 86) converted to Display P3 (27, 51, 83 raw)
next to Avalonia's raw 18, 52, 86. After the colour-space fix the theme assertion passed on every run
that reached it. The same runs still showed intermittent, pre-existing failures elsewhere in the
mode: --native-scroll's fixed 250 ms pixel captures (Ghostty presents the scrolled frame and Avalonia
draws it within ~15 ms, yet the window capture stays stale until the next composition), OSC 52 query
timeouts, and the reattachment assertion losing `TEXTURE_GPU` when a host/user prompt wider than the
narrow resize makes the shell's prompt redraw erase earlier lines.

Validated commit underlines on unchanged visible lines survive asynchronous viewport refreshes and scrolling, with offsets remapped to those lines. Changed lines discard old highlights immediately; detach clears retained results.
