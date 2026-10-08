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
- `TerminalLimitChecks.cs` covers refused grids on attach and resize and the start guidance, locally and
  through a real gRPC host.

## Not yet ported

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
- Windows shells (PowerShell / cmd selection, its Settings picker and the per-shell start guidance). The
  host is POSIX only and a Windows PTY cannot be verified on the machines this port is built on.
- A bounded child-process runner: completion on the child's exit rather than pipe EOF (a grandchild
  holding the pipes must not turn success into a timeout), a drain grace after the deadline race is
  decided, process-group kill on expiry only, clamped budgets, stdin closed, both streams read from spawn,
  and the live environment rather than a launch-time snapshot. Standard output is captured as decoded
  text or, for opaque content, as undecoded bytes; a streaming variant relays output while the child
  runs, closing the stream only after a zero exit and failing it after a nonzero exit, expiry or failed
  launch, so a truncated relay is never mistaken for a complete one, and cancelling it kills the child.
