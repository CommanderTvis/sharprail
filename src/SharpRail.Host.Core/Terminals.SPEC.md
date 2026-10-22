---
id: submodule-host-terminals
type: submodule-design
status: active
title: Terminals — host-owned PTY sessions
parent: module-host-core
---

# Terminals — host-owned PTY sessions

Upstream: packages/server/src/terminal/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))
Upstream: packages/server/src/subprocess/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))

## Responsibility

Host-owned shells rooted in a workspace, one per terminal tab, and the recent output replayed when a
client attaches. A shell outlives every client that shows it. `PtyTerminalService` implements
`ITerminalService`; each attachment is an `ITerminalSession`. It also keeps the catalog of terminal tabs
its clients share (`ITerminalCatalogService`). Clients are opaque ids; which window places a tab where is
frontend-local and never reaches this service.

Closing a project ends its sessions across all workspaces, including detached shells, and revokes
their MCP identities. The host waits for shell shutdown before completing the project-close change.

## Boundary

- Owns: the session table keyed by session id, the PTY (`PtySession`: `posix_openpt`, `posix_spawn` of
  the shell as a new session leader, a wait thread and a read pump), exclusive attachment with takeover
  (`HostedTerminal`), the output recorder (`TerminalRecorder`), foreground-process detection and the
  recording store (`TerminalRecordingStore`, one file per hex session id in the optional recordings
  directory) and the catalog (`TerminalCatalogStore`, `catalog.json` in the same directory).
- Supports attaching to shells, checking foreground activity, closing sessions, and managing
  workspace terminal catalogs and subscriptions. The relay can set raw mode and window size.
  An in-memory catalog supports platforms without PTYs and embedder-supplied terminals.
- Forbidden: UI and transport types. Output reaches clients only through the attachment they hold.

## Decisions

- POSIX shell selection: an existing `SHELL` wins, else `/bin/zsh` on macOS, else `/bin/sh`. It starts as a
  login shell (`-l`) in the workspace root, matching Terminal.app; the PTY supplies interactivity. The
  environment is the host's minus `SHARPRAIL_TOKEN`, with 256-colour/truecolour `TERM` and a UTF-8 locale
  when none is set. `USER` and `LOGNAME` are stamped from the uid: the PTY registers no utmpx record, so a
  login shell without `LOGNAME` would ask `getlogin()`, which reads whatever stale record the reused device
  carries and can name another user.
- Once the host's MCP endpoint is listening (`McpEndpoint`), each session gets a random token, stable for
  its session id, and its shell gets `THINKRAIL_MCP_URL=<endpoint>/mcp/<token>` — the name the ThinkRail
  Claude Code plugin's `.mcp.json` expands, so an agent in the terminal reaches the spec tools of that
  terminal's workspace. `McpOwner(token)` resolves it to the workspace and tab; closing the session forgets
  the token. A shell
  started with no endpoint has the variable removed rather than inheriting one. This applies to POSIX and Windows shells.
- A shell is keyed by a stable session id the client derives from workspace and tab, never by a
  connection or a view. Attach is get-or-create under one lock, so concurrent attaches never start two
  shells. A resume (`Offset >= 0`) never starts a shell: a session that no longer exists is an error.
- Ownership is the host's, not a window's. Any client may attach; holding is exclusive with takeover.
  A PTY has one size, so the new attachment becomes the holder and the previous one completes
  `Detached`. Only the holder may write, resize or kill; a displaced client's input is
  ignored and it is told again that it is detached. A resuming client that lost the session to another
  client receives a detached attachment and never takes the session back; reclaiming is an explicit
  gesture ("Take it back").
- A yielding attach (`TerminalAttachRequest.Yield`) never takes: it starts a session that does not exist and
  joins one whose last attachment left, but where another client's attachment holds the session it returns
  already detached, with no replay, and the session keeps its holder and its grid. The same client's own
  attachment does not stop it, so a retried or resumed attach still lands. This is how a client shows a tab it
  did not create; the remote reply marks the attachment detached in its first message.
- A yielding attach that also asks to watch (`TerminalAttachRequest.Watch`) becomes a watcher where the plain
  yielding one returns detached (`ITerminalSession.Watching`). Any number of clients watch one session. A
  watcher receives the same fresh snapshot as any new view, including recorded live alternate-screen
  contents, and then every chunk the holder receives; its input, resizes and kill are dropped without detaching it, it never completes `Detached`, and it stays a
  watcher when the holder leaves or changes. The shell's exit reaches every watcher, and watching an exited
  session reports the exit after its final screen. `Grid` is the holder's grid as of the last chunk read; a
  resize with no output reaches a watcher as an empty chunk, a redraw nudge does not. A resume with `Watch`
  keeps watching whoever holds the session and replays only what was missed. After a take-over the previous
  holder is detached as before; watching again is its client's next attach.
- Each attachment has its own queue bounded by the resume window. A holder that falls behind pauses the
  PTY producer without dropping bytes or splitting escape sequences. A watcher never blocks delivery; it
  loses everything queued and, when it reads
  again, receives a terminal reset (RIS) and a snapshot taken at that moment, so a stalled watcher costs the
  host one marker and nothing per chunk, and its view is never left mid-sequence. Terminal query replies come
  only from the holder: a watcher's writes are dropped on the host, and snapshots carry no queries.
- A shell ends for exactly five reasons: its tab is closed (`CloseTabAsync`, or `CloseAsync` by session
  id; for a user close through `CloseTabForUndoAsync`, once the undo timeout passes), its workspace is
  removed, it exits, the host stops, or a client kills it. Detaching, closing a window or dropping a connection kills nothing. There is no idle
  culling and no abandoned-client reap. A shell that exits while detached keeps its final output and exit
  status for the next attach; the exit code is the shell's, or 128 plus the terminating signal.
- Busy means a process other than the shell owns the terminal's foreground process group (`tcgetpgrp`);
  a builtin running in the shell itself reads as idle. Closing a busy tab asks first; an idle tab closes
  immediately.
- Attach and resize refuse a grid outside 1–32,767 columns and rows (`TerminalGrid.Require`) before
  session lookup or PTY work, so a refused attach leaves no session. The remote adapter applies the same
  check before sending, which keeps the exception identical on both paths; the server ignores an
  out-of-range resize from a client that bypassed it. Dimensions are integers and input is bytes by type,
  so upstream's runtime string-input check has no equivalent here.
- A shell that cannot start reports fixed guidance chosen by where the shell came from: `SHELL`
  ("Couldn’t start the shell configured by SHELL. Fix or clear SHELL in the host environment, restart
  SharpRail, then retry.") or the host's own choice ("Couldn’t start the configured shell. Check the host’s
  shell installation, then retry."). The native error, the executable path and the value of `SHELL` stay
  on the host as the inner exception. Because macOS execs the shell from a trampoline, the host checks that
  the shell is an executable file before spawning; otherwise an unusable shell would read as one that
  exited. A missing workspace folder keeps its own message.
- Shell integration (host setting `ShellIntegrationEnabled`, Settings › Terminal, on by default, read when a POSIX shell
  starts) loads SharpRail's own scripts (`src/SharpRail.Host.Core/shell-integration`, written for SharpRail, shipped to
  the app's `shell-integration/` by the Host.Core build on every platform): zsh through `ZDOTDIR` naming the script's
  directory (a user's value kept in `SHARPRAIL_SHELL_ZDOTDIR`; its `.zshenv` restores or unsets `ZDOTDIR`, then sources
  the user's `.zshenv`, so zsh reads the rest of the user's files as usual), bash with `--posix` before the login option
  and `ENV` naming `sharprail.bash` (`SHARPRAIL_SHELL_INJECT`, `SHARPRAIL_SHELL_BASH_ENV`, and `HISTFILE` with
  `SHARPRAIL_SHELL_HISTFILE_UNEXPORT` when unset; the script leaves POSIX mode, restores them and sources
  `/etc/profile` and the first of `~/.bash_profile`, `~/.bash_login`, `~/.profile` for a login shell, else
  `/etc/bash.bashrc` and `~/.bashrc`). Interactive shells only: OSC 133 `A` and `B` wrap `PS1` as non-printing text
  and are re-applied at every prompt after the user's own hooks (prompt themes rebuild it), `C` before a command runs
  (zsh `preexec`; bash a DEBUG trap from the first prompt, chained before a user's trap), `D;<status>` before the next
  prompt only when a command ran (an empty Enter reports nothing), all terminated by BEL; OSC 7
  `file://<host><percent-encoded cwd>` at startup and on every directory change. Sourcing a script twice is a no-op. fish
  (4 and later marks its own prompts) and elvish get nothing; Apple's /bin/bash 3.2 ignores `ENV` in POSIX mode and is
  left alone. In bash a command that is only a subshell, such as `(exit 3)`, runs no DEBUG trap and so is not marked.
  A host without the scripts injects nothing. Inherited `GHOSTTY_*` and `SHARPRAIL_SHELL_*` variables are always
  removed, so with the setting off a shell gets none. `--shell-integration` checks real zsh in a fixture HOME locally and
  remotely, the setting off, a user ZDOTDIR whose `.zshrc` sets its own `precmd` and `PROMPT`, Apple bash and the bash
  and fish plans; a bash 4 or later was not available to run the bash script live under injection (it was run under
  Apple's bash 3.2 through `--rcfile`).
- The host reads shell integration from the output it records (`TerminalShellState`, per session, under the output lock,
  sequences split anywhere): the working directory from OSC 7 (`file://` percent-decoded, `kitty-shell-cwd://` raw; a host
  name other than this machine's, as an ssh session reports, is ignored) or a `cwd=` option on an OSC 133 mark. A new
  shell whose attach names `DirectoryFrom` (a session of the same workspace) starts in that directory when it still exists,
  else in the workspace root, as Ghostty's window/tab/split-inherit-working-directory (default true) do; the session stays
  keyed to its workspace. Relative file links resolve in the reported directory first, then the shell process's, then the
  launch directory. Local and remote hosts alike: the attach field crosses the wire (`TerminalInput` member 13).
- The same model gives every client the shell's state (`ITerminalService.ShellStatusAsync`, RPC `ShellStatusAsync`):
  Prompt after OSC 133 A, Input after B, Running after C, Prompt again after D; the last exit code (`133;D;<code>`); the
  last 256 finished commands as host output positions (C to D) with their exit codes; the directory; and whether any mark
  arrived (`Marked`), so consumers fall back to their own signals without one. A D with no command since the last (zsh
  closing an empty prompt) records nothing. Busy (close confirmation) is false while marks say the shell waits at its
  prompt, as Ghostty skips confirmation there; otherwise the foreground process group decides as before. A client of a
  host that predates the call reads no status.
- Marks that arrive between the shell's C and its D are the running program's (an agent drawing its own prompt):
  `TerminalShellState.Foreground` says so, and a D there counts as the program's only while it still holds the terminal
  (`tcgetpgrp`), else it is the shell's and ends the command. Plugins hear changes as `TerminalPromptChanged`.
- Killing sends SIGHUP to the shell's process group, then SIGKILL if it does not exit.
- Not tmux: no extra dependency, no competing tab model.

## Windows processes

Windows 10 (1809+) / Windows 11 x64 or ARM64 uses ConPTY behind the same hosted-session
interface as POSIX. PowerShell 7 (`pwsh.exe` on PATH) is preferred, with Windows
PowerShell as fallback; an existing `SHELL` executable still overrides the choice.
PowerShell receives `-NoLogo` and loads its normal profile; POSIX shells receive
their login option. Executable paths are quoted using Windows argument rules.
The Unicode environment block compares names case-insensitively and excludes
the host token even when a plugin contributes a differently cased spelling.

The shell is created suspended, assigned to a kill-on-close job, then resumed.
Closing a terminal ends its entire job. A dedicated reader drains the output pipe
while the wait thread closes ConPTY, including its final frame; the host reports
exit only after output drains. Attachments, replay, bounded delivery, watching,
takeover and catalog persistence are shared with the POSIX backend.

Windows has no POSIX foreground process group: any live child of the shell means
busy, including background jobs. Process ancestry for plugin workspace resolution
comes from a Toolhelp snapshot. Current-directory discovery is unavailable, so
terminal file links resolve absolute paths only; the initial workspace is not
misreported as the shell's directory after a `cd`. On POSIX hosts a failed
directory probe falls back to the session's launch directory, where agent
terminals start.

ConPTY lifetime follows Microsoft's
[pseudoconsole guidance](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session),
including draining output while closing the pseudoconsole.

## Plugin seams

`PtyTerminalService` implements `IPluginTerminalSeams` for the plugin runtime
([Plugins.SPEC.md](Plugins.SPEC.md)):

- An attach may name its tab key (`TerminalAttachRequest.TabKey`); the session then knows its `TerminalRef`
  (the full workspace root and the tab key). `SessionFor(TerminalRef)` is the same hash the app derives a
  session id from, so a tab nobody attached this run still maps to its session.
- Tokens are per session id and stable: `Token(TerminalRef)` mints or returns one, also for a tab with no
  shell yet, and `ForToken` and `McpOwner` resolve it. Closing the session forgets it.
- Before a shell starts for a known tab with none running, `EnvironmentPreparer` is awaited outside the service's
  lock; the plugin runtime resolves the workspace environment there (see `Plugins.SPEC.md`) and the synchronous
  `EnvironmentContributor` only reads the result.
- Shell environment hooks (`ShellEnvironmentHooks.cs`): when the resolved environment carries a script for the shell being started
  (`SHARPRAIL_SHELL_INIT_<SHELL>`, from mise's and direnv's steps), zsh is pointed at shim start-up files through
  `ZDOTDIR` that source the user's own `.zshenv`, `.zprofile` and `.zshrc` and then run the script (giving `ZDOTDIR` back,
  unset when the user had none), and fish gets a vendor snippet through `XDG_DATA_DIRS` that runs it before the user's
  `config.fish`. No dotfile is edited and the shell is started as before. Other shells get the resolved variables at start
  only. The shim files live under the state directory's `shell-integration` and are rewritten when stale.
- Both apply to one zsh: the hooks run first and the shell-integration injection last, so its `.zshenv` keeps the hooks'
  shim `ZDOTDIR` in `SHARPRAIL_SHELL_ZDOTDIR`, restores it and sources the shim's `.zshenv`; zsh then reads the shim's
  `.zprofile` and `.zshrc`, which run the user's files and the plugins' script. Inherited `SHARPRAIL_SHELL_*` variables are
  dropped before plugin contributions are added, so the hooks' `SHARPRAIL_SHELL_INIT_*` and `SHARPRAIL_SHELL_INTEGRATION`
  survive.
- When a shell starts for a known tab, `EnvironmentContributor` adds variables after core's own (never
  `SHARPRAIL_TOKEN`), `RevivePrefill` may hand the creating attachment a `TerminalPrefill` (or a program, below), and `Lifecycle`
  hears `TerminalSpawned` with the shell's pid and later `TerminalExited`. Closing a session calls
  `SessionClosed` with its id and, when known, its tab.
- `Write` types into a tab's shell host-side, ignoring attachment; `List` is the process table of known tabs
  (pid null once exited) and `WorkspaceForProcess` walks a pid's ancestors (`/proc` on Linux, one `ps` on
  macOS) to a live shell's workspace.

## Exit

- Ghostty's default (`wait-after-command = false`, Surface.zig `childExited`) closes a surface when its
  command exits, whatever the code, unless the run was abnormal: at most `abnormal-command-exit-runtime`
  (250 ms), with any code on macOS and only a non-zero one elsewhere; Ghostty then keeps the surface to
  show the failure. The host follows it: when a catalogued session's process exits, it closes the tab as
  `CloseAsync` does (catalog, recording and session go, every client's tab follows the catalog) unless the
  host setting `TerminalKeepAfterExit` is on or the exit was abnormal. Runtime counts from the attach that
  started the shell. A shell that fails to start never had a session and keeps its tab with Retry.
- The service takes the setting as a delegate from its composer (app and remote host); without one it
  keeps every exited tab, as a host that predates the setting did.
- `TerminalExitChecks.cs` covers the default close for code 0 and non-zero, keeping, persistence, the wire
  and the abnormal rule; `SharedTerminalsE2E` covers the close across two clients, focus and Retry.

## Replay

- The snapshot size is the host setting `TerminalReplayKb` (0–1024 KiB, default 64), read when a shell
  starts, so a change applies to terminals opened afterwards. Zero keeps no snapshot: a fresh view shows
  an empty screen over the live shell. The service takes the size as a delegate from its composer and has
  no dependency on the state store; without one it uses 64 KiB.
- A fresh view receives a bounded snapshot of the main screen: raw bytes, never the alternate
  screen (tracked as a stream, since a switch can split across reads), never a mode sequence itself,
  preceded by a reset and the last observed private modes other than mouse tracking. Resize events are
  never replayed. While the shell is live and a full-screen program is still on the alternate screen, the
  snapshot is followed by that screen's mode, its separately retained live output, and the program's
  mouse and alternate-scroll modes (1000, 1002, 1003, 1006, 1007), so a reattached view keeps wheel and pointer input. They never enter the
  snapshot itself, and leaving the alternate screen forgets them: a later view must not inherit an exited
  program's mouse tracking. Live alternate output obeys the same configured replay byte bound, drops
  historical queries, and is discarded on leaving that screen; it is never persisted or revived into a new
  shell. This is bounded byte replay, not an unbounded VT screen model: very long incremental sessions
  can outlive retained history. Watching never triggers a resize/redraw or input in the holder.
- Saved-screen snapshots omit cursor/status, device, keyboard, color, mode, geometry and capability
  queries, and every Kitty graphics command the terminal would answer (all but `q=2`): an agent's
  `a=q` probe replayed into a revived tab once typed `ESC _ Gi=31;OK ESC \` ahead of the resume
  command, which zsh read as insert-last-word plus junk. Repainting historical output must not send their replies into a new shell or corrupt its
  editable agent-resume command. The same filtering covers old recordings loaded from disk and both
  local/remote renderers. Incomplete trailing control sequences wait for a later complete snapshot;
  ordinary text, styles, titles and links remain. Live output and exact-offset resume bytes are unchanged.
- A resuming client receives exactly the bytes after its position while the 1 MiB resume window still
  holds them, else a fresh snapshot. Replay and the switch to live output happen under the output lock,
  so every byte reaches an attachment exactly once.
- Grid updates are change-only. A fresh view on an unchanged grid still needs the foreground program to
  repaint, and resizing to the same size sends no SIGWINCH, so attach narrows the grid by one column and
  restores it 50 ms later — not in the same tick, which a full-screen program would observe as no change
  and repaint incrementally over a stale buffer. The restore is skipped when the shell exited or a real
  resize landed meanwhile.

## Catalog

- Which terminals exist is shared state, keyed by workspace root and a client-minted tab key; the session
  behind a tab is `TerminalTab.SessionFor(root, key)`, the same for every client. Output stays addressed
  to one attachment; the catalog and terminal signals are the only terminal state that is broadcast.
- Reservation and attachment are separate. `ReserveAsync` records a tab without starting anything and is
  idempotent, its title included. Attach remains the only way a shell is born and does not touch the
  catalog, so a client reserves a tab before it shows one; a session no tab names is allowed (the relay,
  checks) and simply is not shared. Upstream's attach also adds an unknown tab; here that would let a
  client that missed a close resurrect the tab.
- A change is validated and applied, saved, then published. A reservation that cannot be saved is undone
  and reaches nobody; a removal that cannot be saved still takes effect and is reported through
  `LastError`.
- Bounds: at most 256 tabs per workspace, a key of 1–500 characters and a title of 1–1000. Loading
  truncates an oversized workspace, drops invalid or duplicate keys and repairs an invalid title to
  "Terminal"; a corrupt file starts empty.
- The initial terminal is optional and supplied by the client. `OpenWorkspaceAsync` on a workspace the host has not
  heard of records the client's tabs, keeping an empty list when it brings none; a known
  workspace only gains tabs it lacks and never that key again, so closing the initial terminal is final
  for every client. A workspace whose terminals were all closed stays known and empty. This replaces
  upstream's pending marker on the workspace record, which SharpRail's path-keyed workspaces do not have,
  and lets a client that predates the catalog bring its existing tabs.
- Every change returns the complete snapshot it produced, and `WatchAsync` yields the current one and then
  each later one, keeping only the latest for a slow watcher.
- `CloseTabForUndoAsync` (the remote request's `Undoable` flag) is a user's close of a live tab: the tab
  leaves the catalog and every client sees it gone, but its live shell keeps running, detached, for the
  host setting `TerminalUndoCloseSeconds` (default 5, Ghostty's `undo-timeout`; 0–600, zero disables). Each
  close has its own timer. Reserving the same tab within it restores it with the same session; afterwards
  the shell ends as `CloseTabAsync` ends it. An exited shell, a timeout of zero, an implementation without
  undo (the interface default, a memory catalog) or a host that predates the flag close outright. A tab still
  waiting for undo is ended, not saved for revival, when the host stops; a workspace removal ends it too.
  With close-on-exit wired (`keepAfterExit`), a shell that exits while its tab waits for undo ends at once,
  whatever the setting or the abnormal rule, so undo never returns a finished program; a tab closed by its
  shell's exit leaves the catalog through `CloseAsync`, never `CloseTabForUndoAsync`, so it is not undoable.
  `TerminalUndoCloseChecks.cs` covers restore with output written meanwhile, expiry, zero and host stop,
  locally and through a real WebSocket RPC host.
- `CloseTabAsync` removes the tab and ends its shell; `CloseAsync` by session id does the same for the tab
  that session belongs to. `CloseWorkspaceAsync` forgets the workspace and ends every shell started in
  that folder, catalogued or not, with their recordings. The composition root calls it when
  `HostStateStore.WorkspaceRemoved` fires after a worktree removal.
- `TerminalCatalogChecks.cs` covers all of this locally and through a real WebSocket RPC host, including a worktree
  removed through the project host.

## Terminal signals

Ghostty's apprt actions `progress_report`, `desktop_notification` and `ring_bell`, read by the host so every renderer
and every client sees them. `TerminalSignalParser` scans each session's live output on its read pump (never a
replay or recording), keeping escape state across reads: OSC 9;4;state[;percent] (states 0–4, percent clamped to
0–100), OSC 9;text other than a ConEmu command number (iTerm2), OSC 777;notify;title;body, OSC 99 (kitty's basic
subset: title and body payloads, base64 `e=1`, chunks joined by `i=` until `d` is not 0, urgency read and not
shown; queries, icons, buttons, sounds and close are ignored and nothing is answered), and BEL outside OSC, DCS, SOS,
PM and APC strings, at most one per 100 ms as in Ghostty. A full reset (RIS) removes the progress, as Ghostty's does.
Bytes still reach clients unchanged; an OSC over 4 KiB is dropped.

`TerminalSignalHub` keeps the progress of every session and fans signals out through `WatchSignalsAsync` on the
catalog service: a new watcher receives every current progress first, then each signal, and a slow watcher loses
its oldest (1024 queued). A progress with no further report for 15 s is removed (Ghostty's macOS surface timer), as
is a session's when its shell's output ends or it closes. Remote clients receive the same stream over
`ITerminalCatalogRpc.WatchSignalsAsync`; a host that predates it answers Unimplemented and the client shows no
signals. `MemoryTerminalCatalog` has no shells and reports `NotSupportedException`. `--terminals` and
`--terminal-signals` check parsing, the timeout, local and remote delivery and exit.

## Program titles

- The host follows OSC 0 and OSC 2 (BEL or ST terminated) in each tab's output as it records it, with a bounded
  scanner that survives chunk boundaries; OSC 1, an over-long (4 KiB) or cancelled sequence and every other OSC are
  ignored. Control characters become spaces and the title is cut to the catalog's 1000-character bound.
- Programs retitle often (Claude Code animates its title while it works), so a tab's latest title is published at most
  once a second and only when it changed. Publishing is `SetTerminalTitle` with the program source: it is the weakest
  title (user rename > `set_title` > program, [HostState.SPEC.md](HostState.SPEC.md)), lives only in memory and reaches
  every client through the ordinary state broadcast. An empty title clears the program's title; a title that arrives
  after its session closed is dropped. Sessions without a tab key (relay, checks) are not followed.
- `--terminals` checks the scanner and, through local and remote adapters, that a shell's title reaches the client,
  that clearing works, and the precedence over agent and user titles.

## Backpressure

- Delivery is pull-driven end to end: the read pump hands chunks to the session through a bounded queue
  and blocks when it is full, so the kernel holds the program back; the WebSocket RPC call writes one chunk, awaits
  the transport's flow control and only then takes the next. There is no drain latch to miss, which is why
  upstream's WebSocket latch and one-second buffered-byte reconciler have no counterpart.
- A reader coalesces already queued, contiguous output up to 128 KiB before yielding it. This reduces
  framing and browser thread transitions without waiting for more output. Resizes and watcher
  resynchronisations remain ordered boundaries; the position describes the last byte in the batch.
- Output buffers are pooled. The read pump reads into one reused buffer per session and copies each read
  once into a right-sized `ArrayPool` array, a reference-counted chunk. The recorder parses it in place and
  copies it into its preallocated resume ring and snapshot buffers; every attachment that queues the chunk
  holds a reference and releases it once its reader moves past it or delivery drops it. A reader batches
  into one pooled buffer of its own. So a chunk `ITerminalSession.ReadAsync` yields is valid only until the
  reader asks for the next; a reader that keeps bytes longer (the Skia view's queue, the RPC message)
  copies them. `--pty-output-allocations [seconds] [remote]` streams two shells at a fixed rate and fails
  when the embedded host allocates more than 0.3 bytes per delivered byte (copying each chunk twice cost
  about 3; pooled, about 0.15).
- An attachment whose reader stops reading keeps at most the resume window (1 MiB) queued. Beyond that the
  holder pauses the PTY read pump until space is available, preserving every byte. Detach, takeover and
  explicit close release a stalled pump; it never waits while holding the terminal output lock.
- A remote terminal connection is pinged while a terminal call is open (every 15 s, 10 s to answer). A
  connection that stays open but carries nothing — a slept machine, a dropped route — fails within that
  time and the session reconnects and resumes from its position; the attachment it left behind is displaced
  by the resume and was bounded meanwhile.
- `TerminalBackpressureChecks.cs` floods a session nobody reads, locally and remotely, and stalls a proxied
  connection without closing it.

## Terminal programs

- A creating attach may name a program (`TerminalAttachRequest.Program`, carried over RPC), and a revive
  hook may return its prefill as one (`TerminalPrefill.Program`); the request's wins. On POSIX the shell
  is started as `<shell> -l -i -c <script>` (the macOS trampoline keeps `-l`; Linux keeps the `-` argv0),
  so PATH and every rc file apply as at a prompt and the program line keeps the shell's syntax. The script
  (`TerminalProgram`) runs the program, prints `[exited with status N]` for a non-zero status, then
  `exec`s `<shell> -i`: an interactive, non-login shell in the same directory that inherits the login
  shell's environment without running the profile twice (macOS `path_helper` would reorder PATH again).
  fish gets the same script in its own syntax; every other shell is treated as POSIX.
- Nothing is typed for a program: the creating attachment carries no prefill, and an attach that joins a
  running session ignores its program. A revived program's terminal replays the recorded screen first,
  then the program's own output.
- Windows keeps the typed policy: a requested program is handed to the client as a submitted prefill, a
  revive hook's program as the non-submitted offer it would otherwise be.
- `TerminalHostChecks` covers a program locally and remotely (no typed line, status, shell in its
  directory, no rerun on join) and a program revival over a recorded screen.

## Revival

- With a recordings directory, `DisposeAsync` saves the snapshot of every session, exited ones included,
  and every recording still pending, before ending the shells. Writes are atomic (temporary file, then
  rename) and best effort; a recording over 1 MiB (the largest replay size) plus the mode preamble, or with an unreadable name, is
  ignored, and at most 256 recordings load, newest first, the rest being deleted.
- Loaded recordings wait in a pending table. The first attach of a session id starts a new shell and only
  then consumes its entry: `TerminalRecorder.Restore` re-parses the bytes like live output, so modes are
  tracked again rather than copied and the alternate screen and mouse modes stay absent. The attach reports
  `Created` and replays the old picture; no redraw nudge runs. A failed spawn keeps the pending recording.
- Replay strips historical cursor, colour and capability queries so their replies cannot enter the new
  shell. Live reconnect output retains its query bytes.
- Revival is keyed to the catalog. Membership is on disk after every change, so an unclean exit gives back
  the right tabs with blank screens. On start, once a catalog file exists, a recording that no catalogued
  tab names is deleted rather than left for the cap to evict; a directory that never had a catalog keeps
  every recording until its clients have brought their tabs.
- `CloseAsync` drops the pending entry and deletes the file. Without a recordings directory nothing is
  persisted. The local app uses `<profile>/terminals`; a remote host uses `<state directory>/terminals`
  and none without a state directory.
- Checks cover local and remote (stopped and restarted host) revival, close, failed spawn then retry,
  alternate screen and mouse hygiene, corrupt, oversized and surplus files, and the recorder round trip.
- `TerminalReplayChecks.cs` covers the replay size: validation, clamping, zero, a larger size locally and
  through a remote host, and a large screen surviving a host restart.
- `TerminalQueryReplayChecks.cs` covers historical query filtering, split reads and live replies.
- `TerminalLimitChecks.cs` covers refused grids on attach and resize and the start guidance, locally and
  through a real WebSocket RPC host.

## Bounded child runner

`ChildProcess.RunAsync` is the one way Core runs a prompt-free child (Git and `gh`).

- On POSIX the child is spawned with `posix_spawn` as the leader of a new session, with standard input
  on `/dev/null` and both output streams piped. One thread owns it from spawn to reap, so both streams
  are read from the first byte and neither can fill and stall the child.
- Completion is the child's exit (`waitpid`), not the end of its pipes. After the exit the pipes get a
  250 ms drain grace, so a grandchild that keeps them open costs that much and never turns a success into
  a timeout; a successful child's descendants are left running.
- The budget is clamped to zero through 2^31-1 ms. Expiry and cancellation send SIGKILL to the child's
  process group, and nothing is killed otherwise. Expiry throws `ExpiredException` (a `TimeoutException`)
  with the time waited and whatever the child wrote to standard error; cancellation throws
  `OperationCanceledException`.
- The environment is read at each spawn with the caller's overrides applied (null removes a variable),
  and the program is resolved against that `PATH`, so an environment repaired after startup reaches every
  later child. A program that is missing or cannot start throws `Win32Exception`.
- Standard output is returned as bytes and decoded as UTF-8 on request; standard error is text.
- Windows has no session to detach from: the same contract runs over `System.Diagnostics.Process`, with
  the process tree standing in for the group.
- `-- --process` checks exit status and both streams, opaque and multi-megabyte output, a grandchild
  holding the pipes, the own-session leader with no terminal, group kill on expiry and on cancellation,
  the live environment and overrides, a missing program, a clamped budget, and Git's prompt-free
  environment and three timeout messages.

## Not yet ported

- An accepted-input activity signal carrying the owning workspace to the host lifecycle. Input that
  actually reaches the PTY counts; displaced, exited or otherwise ignored writes must not reactivate
  a workspace (see [Workspaces.SPEC.md](Workspaces.SPEC.md#not-yet-ported)).
- Windows shells (PowerShell / cmd selection, its Settings picker and the per-shell start guidance). The
  host is POSIX only and a Windows PTY cannot be verified on the machines this port is built on.
- A streaming variant of the bounded child runner that relays output while the child runs, closing the
  stream only after a zero exit and failing it after a nonzero exit, expiry or failed launch, so a
  truncated relay is never mistaken for a complete one, and cancelling it kills the child. Nothing here
  streams a child's output yet; it belongs with the streamed byte route ([Git.SPEC.md](Git.SPEC.md)).

## File references in terminal output

A session's file-link operation scans one bounded logical line on a worker, checking the host's
filesystem. Relative paths use the live shell process's working directory (Darwin process info or
Linux `/proc`); inability to read it disables relative candidates. Home paths use the host's home.
The result carries UTF-16 spans, absolute paths, one-based line/column and file kind through both
adapters. Unknown sessions yield no links. Parsing never invokes the shell or recursively searches.
Exact files found for a session are admitted for document reading and saving in its workspace,
including files outside it; unrelated paths retain the ordinary containment checks.
Those external-file admissions expire when the terminal session closes or the host stops.

The terminal service accepts bounded PNG clipboard uploads for an existing session and writes a unique
file under the host profile clipboard directory with private permissions. The response is a host path;
browser-local files are never offered to host shells.

## Windows verification scope

The 2026-10-13 macOS verification cross-built x64 PE terminal/editor libraries and checked their
Windows OS/UCRT imports, PowerShell parsing and portable quoting, path/profile and local/remote Git
fixtures. It did not execute ConPTY, Recycle Bin or native Windows UI branches. An actual Windows
packaged --windows run is required for those branches; it remains distinct from manual clipboard,
IME, editor and desktop-client acceptance. Cross-compilation is not runtime proof. Current platform
acceptance tasks are tracked in [STATUS.md](../../STATUS.md#outstanding-acceptance-work).
