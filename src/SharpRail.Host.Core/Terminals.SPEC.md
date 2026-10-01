---
id: submodule-host-terminals
type: submodule-design
status: active
title: Terminals — host-owned PTY sessions
parent: module-host-core
---

# Terminals — host-owned PTY sessions

Upstream: packages/server/src/terminal/SPEC.md @ 4a65ed7f
Upstream: packages/server/src/subprocess/SPEC.md @ c44534ea

## Responsibility

Host-owned shells rooted in a workspace, one per terminal tab, and the recent output replayed when a
client attaches. A shell outlives every client that shows it. `PtyTerminalService` implements
`ITerminalService`; each attachment is an `ITerminalSession`. Clients are opaque ids; which window places
a tab where is frontend-local and never reaches this service.

## Boundary

- Owns: the session table keyed by session id, the PTY (`PtySession`: `posix_openpt`, `posix_spawn` of
  the shell as a new session leader, a wait thread and a read pump), exclusive attachment with takeover
  (`HostedTerminal`), the output recorder (`TerminalRecorder`) and foreground-process detection.
- Public surface: `AttachAsync`, `IsBusyAsync`, `CloseAsync`, `DisposeAsync`; `TerminalDevice` for the
  relay's raw mode and window size.
- Forbidden: UI and transport types. Output reaches clients only through the attachment they hold.

## Decisions

- Shell selection: an existing `SHELL` wins, else `/bin/zsh` on macOS, else `/bin/sh`. It starts as a
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
  started with no endpoint has the variable removed rather than inheriting one. POSIX only; other platforms show an availability message.
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

## Plugin seams

`PtyTerminalService` implements `IPluginTerminalSeams` for the plugin runtime
([Plugins.SPEC.md](Plugins.SPEC.md)):

- An attach may name its tab key (`TerminalAttachRequest.TabKey`); the session then knows its `TerminalRef`
  (the full workspace root and the tab key). `SessionFor(TerminalRef)` is the same hash the app derives a
  session id from, so a tab nobody attached this run still maps to its session.
- Tokens are per session id and stable: `Token(TerminalRef)` mints or returns one, also for a tab with no
  shell yet, and `ForToken` and `McpOwner` resolve it. Closing the session forgets it.
- When a shell starts for a known tab, `EnvironmentContributor` adds variables after core's own (never
  `SHARPRAIL_TOKEN`), `RevivePrefill` may hand the creating attachment a `TerminalPrefill`, and `Lifecycle`
  hears `TerminalSpawned` with the shell's pid and later `TerminalExited`. Closing a session calls
  `SessionClosed` with its id and, when known, its tab.
- `Write` types into a tab's shell host-side, ignoring attachment; `List` is the process table of known tabs
  (pid null once exited) and `WorkspaceForProcess` walks a pid's ancestors (`/proc` on Linux, one `ps` on
  macOS) to a live shell's workspace.

## Replay

- A fresh view receives a bounded snapshot (64 KiB) of the main screen: raw bytes, never the alternate
  screen (tracked as a stream, since a switch can split across reads), never a mode sequence itself,
  preceded by a reset and the last observed private modes other than mouse tracking. Resize events are
  never replayed. While the shell is live and a full-screen program is still on the alternate screen, the
  snapshot is followed by that screen's mode and the program's mouse and alternate-scroll modes (1000,
  1002, 1003, 1006, 1007), so a reattached view keeps wheel and pointer input. They never enter the
  snapshot itself, and leaving the alternate screen forgets them: a later view must not inherit an exited
  program's mouse tracking.
- A resuming client receives exactly the bytes after its position while the 1 MiB resume window still
  holds them, else a fresh snapshot. Replay and the switch to live output happen under the output lock,
  so every byte reaches an attachment exactly once.
- Grid updates are change-only. A fresh view on an unchanged grid still needs the foreground program to
  repaint, and resizing to the same size sends no SIGWINCH, so attach narrows the grid by one column and
  restores it 50 ms later — not in the same tick, which a full-screen program would observe as no change
  and repaint incrementally over a stale buffer. The restore is skipped when the shell exited or a real
  resize landed meanwhile.

## Not yet ported

- A persisted per-workspace terminal catalog (at most 256 tabs, bounded keys and titles) shared by every
  client, with reservation separate from attachment so a hidden default terminal survives reload and
  other clients without a shell, and catalog membership broadcast on change.
- Revival across a host restart: restoring the persisted tabs, whose first attach starts a fresh, blank
  shell. Terminal output is deliberately not persisted: old output beside a new prompt reads as current.
- Closing every terminal of a removed workspace.
- Stable, non-native guidance when a shell cannot start, and Windows shells (PowerShell / cmd selection).
- A bounded child-process runner: completion on the child's exit rather than pipe EOF (a grandchild
  holding the pipes must not turn success into a timeout), a drain grace after the deadline race is
  decided, process-group kill on expiry only, clamped budgets, stdin closed, both streams read from spawn,
  and the live environment rather than a launch-time snapshot. Standard output is captured as decoded
  text or, for opaque content, as undecoded bytes; a streaming variant relays output while the child
  runs, closing the stream only after a zero exit and failing it after a nonzero exit, expiry or failed
  launch, so a truncated relay is never mistaken for a complete one, and cancelling it kills the child.
