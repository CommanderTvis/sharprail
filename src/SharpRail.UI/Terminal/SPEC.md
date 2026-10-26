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

## Resume offer

A revived agent tab shows its host `TerminalResumeOffer` in a row above the surface: “Claude session <first 8 of
the id> was running here.” with Resume (Alt+R) and Start fresh (Alt+F) buttons, which take keyboard focus like any
button. Either answer goes to the host as `HostStateChange.AnswerResume`, so every window and remote client shows
and settles the same offer. When the host finds the session open in another tab, window or SharpRail instance the
row names the owner (pid, age, tty: “…is open in pid 4463 (started 2 days ago, ttys003). The SharpRail instance that
started it is no longer running.”, or “…another SharpRail window/instance (pid N), tab X”) and offers only Start fresh.
For a stray left by a stopped instance it also shows “Quit that process and resume” (Alt+Q): a confirmation dialog on
the clicking client names the pid, command and age, then `HostStateChange.QuitOwnerAndResume` asks the host to end it
(Plugins.SPEC); progress or failure shows inline in the row. A live SharpRail owner never gets the button. The row is applied when a terminal view is created and on every state
broadcast that changes `ResumeOffers`; it reads state only (Plugins.SPEC, Terminals).

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

Android's Skia terminal includes a single compact row of keys (32 dp, Termux-style) for Esc, Tab,
one-shot Ctrl and Alt, grouped arrows and Home/End/Page Up/Page Down; keys are sized to content
(at least 40 dp wide) and the row scrolls horizontally when it overflows. The row shows only while
the soft keyboard is open and its terminal has focus. It preserves input focus and uses the terminal's
negotiated key encoding. Watched and exited terminals do not offer the bar.
Switching to watching detaches the surface from the key-bar panel before moving it
into the read-only scroll viewer.

Android watched terminals fit their font to the pane between 10 and 13 device-independent
pixels. Wide desktop grids may still need horizontal scrolling at the 10-pixel readability
floor. Rotation and the holder's grid changes recalculate the fit without resizing the host
terminal or taking ownership; active terminals keep their usual font.

## Font size

Each terminal has its own font size, as each Ghostty surface does. A trackpad pinch (Avalonia's magnify gesture),
a Control- or Command-wheel and, on Android, a two-finger pinch over the terminal's surface change that terminal's
font, not the app zoom; `TerminalView` claims them before the window's pinch handler and before the renderer
scrolls, and gestures over accessories or the companion are not the terminal's. The steps are Ghostty's
`increase_font_size`/`decrease_font_size` in whole points: a point per wheel notch, and a point per tenth of
magnification (or pinch scale), with fractions carried so a trackpad stays smooth. The offset from the
renderer's base size is bounded to −8…+48 points; "Reset Font Size" in the terminal's context menu is Ghostty's
`reset_font_size`. The base is the renderer's own (13 for Skia, Ghostty's configured size for the texture, 14 for
restty; there is no Settings terminal font size), and the app zoom still scales on top. The keyboard zoom chords
always zoom the app, a focused terminal included.

The offset is kept per session for the life of the process, never in the profile: a view rebuilt for the
same terminal (tab switch, re-attach, renderer restart) keeps it, a restart of the app does not. As with
Ghostty's `window-inherit-font-size` (default on), a terminal created in this window (`TerminalLaunch.Yield`
false) starts at the size of the terminal focused last; restored, adopted and watched terminals start at the base.

The renderers apply it through `ITerminalBackend.SetFontSizeOffset`: Skia sets `GhosttySkiaView.FontSize`, the
texture sends Ghostty's binding actions (`GhosttyTextureView.FontSizeOffset`), restty calls its runtime's
`setFontSize` and catches Control/Command-wheel (a browser pinch) on its own DOM surface. The grid refits as it
does for any font change and an attached holder resizes its PTY through the usual resize path; a watcher's grid
is fixed to the holder's, so its font change only redraws its cells and never resizes another client's PTY.
Android watchers keep fitting their font to the pane. Restty has no context-menu entry. `--terminals`,
`--shell` and `--native-texture` cover it.

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
A terminal tab belongs to its workspace for life: layout moves act on the active workspace's view only, and the
host catalog has no move, since a session is named by workspace and tab key, so a client (remote ones too)
naming a tab key in another workspace reserves a separate tab with its own shell and leaves the original in
place. The one exception is `workspace_emigrate`, which the user approves per call
(Host Core `WorkspaceMcpTools.cs`).
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

## Secure keyboard entry

A clone of Ghostty's macOS secure input. Ghostty's sources: the options `macos-auto-secure-input` (default true:
"automatically enable the Secure Input feature when it detects that a password prompt" is being shown, by heuristics
that do not work over SSH) and `macos-secure-input-indication` (default true: a graphical indication while on), and
the keybind action `toggle_secure_input` ("applies to the entire application", stays until toggled off or Ghostty quits,
macOS only) — https://ghostty.org/docs/config/reference and https://ghostty.org/docs/config/keybind/reference.
libghostty detects the prompt by polling the PTY's termios every 200 ms (`src/termio/Exec.zig`: canonical and not
echo) and reports it to the apprt as the `GHOSTTY_ACTION_SECURE_INPUT` on/off/toggle action; the macOS app's
`SecureInput` singleton (`macos/Sources/Features/Secure Input/SecureInput.swift`) balances every
`EnableSecureEventInput` with one `DisableSecureEventInput`, enables only while the app is active and a scoped
surface is focused or the global toggle is on, and yields on resign-active.

SharpRail cannot take libghostty's action: every renderer feeds Ghostty from external I/O or the relay, so its
termios poll sees no shell. The host's PTY answers instead ([Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#decisions)),
which works the same for a remote host: the host reports the flag, the client's OS enables secure input locally.

- `TerminalView` polls `ITerminalBackend.IsPasswordInputAsync` every 200 ms off the UI thread, only while it has
  keyboard focus in the active window, is live and held by this client (never failed, exited, detached or
  watching), and the setting is on. Its claim goes to `SecureInput.Shared`; focus loss, deactivation, the prompt
  ending, exit, restart, close and leaving the visual tree withdraw it.
- `SecureInput` mirrors Ghostty's singleton: it wants the reference while the app is active and either the manual
  toggle is on or, with the setting on, some terminal claims it; it holds at most one reference, so enable and
  disable always alternate. `SecureInputLifetime` follows window activation and calls `ReleaseAll` on desktop exit,
  process exit and an unhandled exception; after that nothing re-enables. The Carbon call sits behind
  `ISecureEventInput`; checks install a counting fake process-wide and never touch the real one.
- A lock badge (top right of the terminal, with a tooltip) shows on the focused terminal while secure input is on
  because of it or the manual toggle, Ghostty's indication.
- Secure Keyboard Entry, a check item, is Ghostty's `toggle_secure_input`: primary in the terminal's context menu
  (appended through the renderers' `ExtendContextMenu`), secondary in the macOS Edit menu. The toggle is not
  remembered across launches.
- Settings › Terminal › "Secure input for password prompts (macOS)", default on, is an app preference
  (`SecureInputForPasswords`) like the renderer: it concerns this machine's keyboard, not the host. Off withdraws
  every automatic claim; the manual toggle still works.
- Elsewhere than macOS `SecureInput.Shared` has no system call: the menu items and the Settings section are hidden
  and no terminal polls.
- `--secure-input` (also run by `--terminals`) covers it; `--terminals` also drives a real `stty -echo; read` through
  the local and remote host.

## Validation

Both renderers underline HTTP(S) URLs under the pointer while Command is held
on macOS (Ctrl elsewhere), and modifier-click opens the URL using the system
browser. Soft-wrapped URLs remain one link across rows; explicit newlines remain
boundaries. Right-click over a web URL offers Open link alongside the terminal clipboard menu,
without Command/Ctrl. The action opens the URI captured at right-click even if output changes. Mouse
tracking forwards RMB to the program; Shift bypasses tracking for the menu, and right-click on an underlined resolved reference (a validated file or commit hash) always opens the menu, as a modifier-click on a link wins over reporting. Every renderer
also follows OSC 8 hyperlinks (a label whose target is not its text): Metal through Ghostty, Skia (desktop, Android and
the browser fallback) from the per-cell hyperlinks libghostty-vt records. Command-hover underlines every cell of the
hovered target, across soft wraps, and shows the target as the view's tooltip; Command-click routes it as any terminal
link: `file:` targets to the document tab through the host's resolver, web and other schemes to the system launcher
without a confirmation, as Metal does. OSC 8 cells take precedence over text-detected URLs, which keep working beside
them. The separate restty canvas
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
range is the navigation target. A directory links only where the text names it absolutely (an absolute
or `~/` path, or a `file://` URL), anywhere on the host; bare folder names stay plain. Clicking one inside
the workspace shows the Files pane and selects the folder there (its ancestors are listed in the background
first; the workspace root only shows the pane). Clicking one outside the workspace, which Files cannot
show, opens the host folder browser at it, and a file picked there opens in a document tab like any other link.
Bare and relative names also resolve against the absolute directories named earlier in the same
resolved text (the visible viewport, so the window is what is on screen), as when an agent prints
`/…/apps/web/src/chat` and then lists `Composer.tsx`: the most recently named of the last four is tried
first, since the text refers to it, then older ones, then the shell's directory. Only files count in a
named directory, and a directory named after a word does not resolve it. These context links come from
the viewport underline pass; the modifier-hover pass submits one line and so sees only the shell's directory.
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
streams, the view scrolls or resizes. Skia marks cells of the snapshot it paints, and does not present a
snapshot while a non-blank line other than the visible cursor's has never been resolved: it keeps the previous
rows until that resolution lands (requested without debounce) or 150 ms pass, so text never appears first and
gains its underline a frame later. One expired wait stops waiting until a resolution lands, so streaming output is
not throttled. Lines resolved without links are remembered as known, and a first row that is the tail of a line
whose start scrolled away keeps the visible part of that line's links. Metal (local and
remote) passes the underlined cells to Ghostty's renderer, which tracks them in screen coordinates and
draws them with the cell's style in the same frame, dropping a cell whose text changed until the next
resolution. The earlier Avalonia overlay was positioned on a 200 ms timer from whatever cell state was
current then, so it lagged and jumped against the Metal texture.

The terminal host runs a bounded character scanner and checks filesystem existence off the dispatcher.
Relative names resolve against the terminal process's directory (the shell's, unaffected by a
foreground agent such as Claude Code), falling back to the launch directory on POSIX when the probe
fails; Windows, where the directory is never known, links absolute paths only. Absolute paths outside
the workspace and `~/` paths link whenever the host can read them, files and directories alike; relative
directories and nonexistent names stay plain. Remote sessions resolve on their host, including
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

## Prompts and command output

These follow Ghostty and need shell integration marks (the host injects SharpRail's scripts; see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#decisions)). Both renderers read the OSC 133 marks their
own terminal state keeps, so local and remote terminals behave alike.

- Jump to prompt (Ghostty's `jump_to_prompt`, default bindings Command+Up/Down on macOS as in Terminal.app, Ctrl+Shift+Up/Down
  elsewhere, from Ghostty's `Config.zig` at the pinned VT commit): the viewport's top moves to the previous or next prompt,
  skipping the one on view; a prompt in the active area returns to the bottom. Metal sends the binding action to Ghostty;
  Skia reproduces `PageList.scrollPrompt` over libghostty-vt's row marks (`gav_vt_jump_to_prompt`). `--shell-integration`
  (Skia, real zsh) and `--native-prompts` (Metal window, Ghostty's marks) check it.
- Command output (Ghostty's `Screen.selectOutput`; gesture from Ghostty's `Surface.zig`): Command+triple-click on macOS
  (Ctrl+triple-click elsewhere) on a command's output selects exactly that output, from its first output row to the last
  non-blank row before the next prompt; prompt and input rows select nothing. The right-click menu offers "Copy Output of
  This Command" when the click was on output (the text is taken at the click). Metal answers through
  `gav_ghostty_command_output` (Native/prompt-surface.zig, prompts.zig; Ghostty 1.2.3 did not apply the gesture to events
  the view forwards, so the view selects on Avalonia's third click itself); Skia uses libghostty-vt's
  `ghostty_terminal_select_output` and the gesture's output behaviour. Skia's repeat clicks now carry Ghostty's 500 ms
  interval and one-cell distance; without them double and triple clicks never counted.
- Click to move (Ghostty's `cursor-click-to-move`, default true; a plain click since Ghostty's current docs, Option+click in
  1.2.3): a plain click without dragging or selecting, on the input line while the shell waits for input (OSC 133 B
  written, C not yet), sends the left or right arrows (cursor-keys mode respected) that move the shell's cursor there; a
  click past the text stops one cell after it. Never on the alternate screen, while a program tracks the mouse, or while a
  command runs. Skia follows Ghostty's `Screen.promptClickLine` over libghostty-vt's input cells (`gav_vt_prompt_click`);
  Metal's Ghostty 1.2.3 marks only rows, so `gav_ghostty_prompt_click` counts the cells of the cursor's soft-wrapped line
  and cannot tell prompt text on that row from input (a click on the prompt moves to the input's start, where the shell
  stops). Both views expose `ClickToMoveCursor`. A Skia release now ends the selection gesture before releasing the
  pointer capture, which had reset it first.

- Failed commands: a command whose OSC 133 D carries a non-zero exit code gets a small badge (the theme's ANSI red,
  3 px) in the left gutter beside its command line, which follows it as it scrolls; successful and unreported exits get
  none. Skia tracks the command line with a libghostty-vt tracked grid reference from B through D
  (`gav_vt_failed_rows`); Metal's Ghostty 1.2.3 ignored D, so `CommandEnd.patch` marks the row (`Row.command_failed`)
  and `gav_ghostty_failed_rows` reports the visible ones on each frame notification, drawn as an overlay in the gutter
  (it can trail the text by a frame, never covering a glyph). There is no scrollbar mark yet. Both views expose
  `FailedCommandRows`; the checks cover both.

## Working directory of new terminals

A terminal opened from another (New terminal in its group, or a pane below) starts where that terminal's shell last
reported being (OSC 7): `LayoutSession.NewTerminal` records the group's shown terminal, else the focused one, and the tab's
first attach carries its session as `TerminalLaunch.DirectoryFrom`; without a report the shell starts in the worktree.
The host resolves it ([Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#decisions)), so remote hosts behave
alike. `--terminal-directory` (and `--terminals`) checks it through the workbench; `--shell-integration` checks real zsh
locally and remotely and file links following the report.

## Dropped attachments

Plugin accessory controls advertise whether their agent accepts files and provide the prompt insertion
callback through the kit's `FileDrop` attached properties. The workbench resolves tree payloads against
their source workspace, shares native external files directly with a local agent, and uploads external
bytes for remote/browser agents. Host paths from another workbench are refused. Files and folders may
be dropped together; the plugin owns their syntax. The surface highlights accepted drops. Uploads show
progress with Cancel. Completion checks the current workspace, accessory and terminal ownership again;
a failed, exited, detached or watching terminal never accepts a drop or takes over implicitly.

Where nothing can be dropped (Android and the phone layout), a terminal running a Claude Code or Codex
session (its accessory accepts files, which the plugins derive from the host's agent record) shows an
Attach button above its accessories. Plain shells, exited, failed, detached and watching terminals hide
it (refreshed on output and accessory changes, rechecked before every choice). It opens a compact menu
with exactly three choices, each inserting through the same callback without submitting:

- Upload file from phone (from this device off Android): the platform file picker (images first, any
  file allowed); the choice is read off the UI thread within the 64 MB drop limit, always uploaded to the
  host with progress and Cancel, and the host paths are inserted. Uploading into a chosen host folder is
  out of scope (issue #46).
- Choose path from host: a folder browser over the host's folder listing
  ([Files.SPEC.md](../../SharpRail.Host.Core/Files.SPEC.md#host-folder-listing)), starting at the workspace
  root, with Up, Home and the current path shown. Folders load off the UI thread and a pending load is
  cancelled by opening another folder or closing; a missing or unreadable folder shows its error inline
  and Up and Home still lead out. The picked file's absolute host path (joined with the host's separator)
  is inserted; nothing is uploaded. Hidden against hosts below protocol 11.
- Choose file from workspace: the same browser over the workspace's file listing from its root (no Home);
  the file is inserted as `<workspace root>/<relative path>`, as a dragged workspace file is. Files shared into the app (Android's share sheet) queue in `SharedAttachments`
until the shown workspace has an attach-ready (running, held) agent terminal: the most recently focused one, or the only
one; with several and none focused they wait. On Android a pasted clipboard image is saved through the
host terminal service, as in the browser client, so the pasted path exists where the agent runs.

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

## Titles and pointer shapes

A program's OSC 0/2 title reaches the tab through the host (see
[Terminals.SPEC.md](../../SharpRail.Host.Core/Terminals.SPEC.md#program-titles)), so every client, local or remote,
labels the tab the same way whatever renderer it uses and whether or not the tab is mounted. Precedence is user rename >
an agent's `set_title` > the program's title > the tab's default "Terminal N"; a tab keeps the last title it was given
when the program clears its own. OSC 1 (icon name) is ignored, as libghostty does; the macOS window title does not follow
terminals.

OSC 22 pointer shapes become Avalonia cursors in Skia (read from libghostty-vt) and the Metal texture view (Ghostty's
`mouse_shape` action, which also reports Ghostty's own shape changes); a hovered link still shows a hand. The hosted
NSView control keeps AppKit's cursor. OSC 66 (kitty text sizing) is parsed but not implemented by the pinned libghostty,
so no renderer sizes text with it. `--terminals` (Skia) and `--native-terminal-links` (Metal) check OSC 22.

