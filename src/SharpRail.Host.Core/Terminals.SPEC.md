---
id: submodule-host-terminals
type: submodule-design
status: active
title: Terminals — host-owned PTY sessions
parent: module-host-core
---

# Terminals — host-owned PTY sessions

Upstream: packages/server/src/terminal/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/subprocess/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

Host-owned shells rooted in a workspace, one per terminal tab, and the recent output replayed when a
client attaches. A shell outlives every client that shows it. `PtyTerminalService` implements
`ITerminalService`; each attachment is an `ITerminalSession`. Clients are opaque ids; which window places
a tab where is frontend-local and never reaches this service.

## Boundary

- Owns: the session table keyed by session id, the PTY (`PtySession`: `posix_openpt`, `posix_spawn` of
  the shell as a new session leader, a wait thread and a read pump), exclusive attachment with takeover
  (`HostedTerminal`), the output recorder (`TerminalRecorder`), foreground-process detection and the
  recording store (`TerminalRecordingStore`, one file per hex session id in the optional recordings
  directory).
- Public surface: `AttachAsync`, `IsBusyAsync`, `CloseAsync`, `DisposeAsync`; `TerminalDevice` for the
  relay's raw mode and window size.
- Forbidden: UI and transport types. Output reaches clients only through the attachment they hold.

## Decisions

- Shell selection: an existing `SHELL` wins, else `/bin/zsh` on macOS, else `/bin/sh`. It starts as a
  login shell (`-l`) in the workspace root, matching Terminal.app; the PTY supplies interactivity. The
  environment is the host's minus `SHARPRAIL_TOKEN`, with 256-colour/truecolour `TERM` and a UTF-8 locale
  when none is set. POSIX only; other platforms show an availability message.
- A shell is keyed by a stable session id the client derives from workspace and tab, never by a
  connection or a view. Attach is get-or-create under one lock, so concurrent attaches never start two
  shells. A resume (`Offset >= 0`) never starts a shell: a session that no longer exists is an error.
- Ownership is the host's, not a window's. Any client may attach; attach is exclusive with takeover.
  A PTY has one size, so the new attachment becomes the recipient and the previous one completes
  `Detached`. Only the current attachment may write, resize or kill; a displaced client's input is
  ignored and it is told again that it is detached. A resuming client that lost the session to another
  client receives a detached attachment and never takes the session back; reclaiming is an explicit
  gesture ("Take it back").
- A shell ends for exactly four reasons: its tab is closed (`CloseAsync`), it exits, the host stops, or
  a client kills it. Detaching, closing a window or dropping a connection kills nothing. There is no idle
  culling and no abandoned-client reap. A shell that exits while detached keeps its final output and exit
  status for the next attach; the exit code is the shell's, or 128 plus the terminating signal.
- Busy means a process other than the shell owns the terminal's foreground process group (`tcgetpgrp`);
  a builtin running in the shell itself reads as idle. Closing a busy tab asks first; an idle tab closes
  immediately.
- Killing sends SIGHUP to the shell's process group, then SIGKILL if it does not exit.
- Not tmux: no extra dependency, no competing tab model.

## Replay

- A fresh view receives a bounded snapshot (64 KiB) of the main screen: raw bytes, never the alternate
  screen (tracked as a stream, since a switch can split across reads), never a mode sequence itself,
  preceded by a reset and the last observed private modes other than mouse tracking. Resize events are
  never replayed.
- A resuming client receives exactly the bytes after its position while the 1 MiB resume window still
  holds them, else a fresh snapshot. Replay and the switch to live output happen under the output lock,
  so every byte reaches an attachment exactly once.
- Grid updates are change-only. A fresh view on an unchanged grid still needs the foreground program to
  repaint, and resizing to the same size sends no SIGWINCH, so attach narrows the grid by one column and
  restores it 50 ms later — not in the same tick, which a full-screen program would observe as no change
  and repaint incrementally over a stale buffer. The restore is skipped when the shell exited or a real
  resize landed meanwhile.

## Revival

- With a recordings directory, `DisposeAsync` saves the snapshot of every session, exited ones included,
  and every recording still pending, before ending the shells. Writes are atomic (temporary file, then
  rename) and best effort; a recording over 64 KiB plus the mode preamble, or with an unreadable name, is
  ignored, and at most 256 recordings load, newest first, the rest being deleted.
- Loaded recordings wait in a pending table. The first attach of a session id starts a new shell and only
  then consumes its entry: `TerminalRecorder.Restore` re-parses the bytes like live output, so modes are
  tracked again rather than copied and the alternate screen and mouse modes stay absent. The attach reports
  `Created` and replays the old picture; no redraw nudge runs. A failed spawn keeps the pending recording.
- `CloseAsync` drops the pending entry and deletes the file. Without a recordings directory nothing is
  persisted. The local app uses `<profile>/terminals`; a remote host uses `<state directory>/terminals`
  and none without a state directory.
- Checks cover local and remote (stopped and restarted host) revival, close, failed spawn then retry,
  alternate screen and mouse hygiene, corrupt, oversized and surplus files, and the recorder round trip.

## Bounded child runner

`ChildProcess.RunAsync` is the one way Core runs a prompt-free child (Git, `gh`, the login-shell probe).

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

- Rejecting attach and resize grids outside 1–32,767 before session lookup or PTY work. C# already types
  dimensions as integers and input as bytes; upstream's runtime string-input check does not apply here.
- Bounded host-side output backpressure and recovery after system sleep. Awaited gRPC delivery does not
  bound the unbounded PTY and attachment queues; a transport-specific implementation must guarantee
  progress without relying on a single drain notification. Upstream's WebSocket latch and 1-second
  buffered-byte reconciler have no direct equivalent in the current gRPC stream.
- A persisted per-workspace terminal catalog (at most 256 tabs, bounded keys and titles) shared by every
  client, with reservation separate from attachment so a hidden default terminal survives reload and
  other clients without a shell, and catalog membership broadcast on change.
- Revival keyed to a persisted terminal catalog: revival here is keyed by session id alone, so recordings
  of tabs closed while the host was down linger until the store's cap evicts them, and a crash (as
  opposed to a graceful stop) saves nothing.
- Closing every terminal of a removed workspace.
- Stable, non-native guidance when a shell cannot start, and Windows shells (PowerShell / cmd selection).
- A streaming variant of the bounded child runner that relays output while the child runs, closing the
  stream only after a zero exit and failing it after a nonzero exit, expiry or failed launch, so a
  truncated relay is never mistaken for a complete one, and cancelling it kills the child. Nothing here
  streams a child's output yet; it belongs with the streamed byte route ([Git.SPEC.md](Git.SPEC.md)).
