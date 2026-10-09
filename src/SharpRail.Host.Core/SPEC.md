---
id: module-host-core
type: module-design
status: active
title: Host core
parent: architecture
depends-on: [module-host-abstractions]
---

# Host core

Upstream: packages/shared/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/host/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The host as an embeddable library: filesystem reads and saves, project and spec discovery, Git status,
diffs and worktrees, shared host state, and host-owned PTY terminals. It implements the interfaces in
`SharpRail.Host.Abstractions` and knows nothing of Avalonia, gRPC or the UI. The UI embeds it directly
through `SharpRail.Host.Client`'s local adapters; `SharpRail.Host.Remote` serves the same instances over
gRPC.

## Boundary

- Owns: `WorkspaceHost`, `ProjectServices` (one client's project session), `HostStateStore`,
  `PtyTerminalService` and its internals (`HostedTerminal`, `TerminalRecorder`, `PtySession`,
  `Posix.cs`), `ChildProcess` (the bounded runner), `GitRepository`, `SpecCatalog` and `TerminalDevice` (raw mode and window size for the
  relay).
- Public surface: the Abstractions interfaces as implemented by those public classes; everything else is
  `internal`.
- Allowed deps: `SharpRail.Host.Abstractions`, the .NET base library, the `git` executable and libc.
- Forbidden: any UI, Avalonia, Protocol, Remote or Client reference; any AI/agent dependency.

## Internal areas

| Area | Files | Spec |
| --- | --- | --- |
| Shared host state | `HostStateStore.cs` | [HostState.SPEC.md](HostState.SPEC.md) |
| Workspaces and worktrees | `ProjectServices.cs`, `WorkspaceActions.cs`, `WorkspaceRegistry.cs`, `HostStateStore.Workspaces.cs` | [Workspaces.SPEC.md](Workspaces.SPEC.md) |
| Git runner, status and diffs | `GitRepository.cs`, `ProjectServices.cs` | [Git.SPEC.md](Git.SPEC.md) |
| Files | `ProjectServices.cs`, `ProjectFileSaving.cs`, `WorkspaceHost.cs` | [Files.SPEC.md](Files.SPEC.md) |
| Specs | `SpecCatalog.cs` | [Specs.SPEC.md](Specs.SPEC.md) |
| Terminals | `PtyTerminalService.cs`, `HostedTerminal.cs`, `TerminalRecorder.cs`, `TerminalDevice.cs`, `Posix.cs` | [Terminals.SPEC.md](Terminals.SPEC.md) |

Edges between areas: `ProjectServices` publishes workspace membership into `HostStateStore` after it
creates or removes a worktree (the only cross-area write), and every Git read goes through
`GitRepository.RunAsync`. Terminals, specs and state depend on nothing else in Core.

## Composition

There is no composition root inside Core. Two composers wire it:

- `SharpRail.UI` composes one app-owned `Workbench`: a `HostStateStore` over `~/.sharprail`, a
  `PtyTerminalService` shared by every window, and one `ProjectServices` per window, all behind local
  adapters. Both local terminal renderers call that service directly in process; no local RPC server runs.
- `RemoteServer.Create` registers `WorkspaceHost`, a `HostStateStore` over `SHARPRAIL_STATE_DIR`, a
  `ProjectSessions` cache that resolves each call's `ProjectServices` from the client's root header, and a
  `PtyTerminalService` the host owns, then maps the four RPC services behind bearer-token authentication.

Features never reach back into their composer: pushes flow through `IHostStateService.WatchAsync` and
terminal attachments, never through a UI callback.

## Get right

- Host state changes reach every client only as complete snapshots from `HostStateStore`; a client that
  missed events rehydrates from the next one. No operation patches one client directly.
- A project session serializes its mutations (open, Git actions, saves) with one gate, while reads use the
  root they started with, so a concurrent project switch never redirects an in-flight write.
- Paths from a client are contained to the workspace root and never followed through a symbolic link.
- Every Git, `gh` and network child runs through one bounded runner (`ChildProcess.cs`,
  [Terminals.SPEC.md](Terminals.SPEC.md)): an explicit working directory, its own session, closed input,
  captured output, a wall-clock budget and, for Git, `LC_ALL=C` so diagnostics are parseable. Expiry and
  cancellation kill the child's whole process group. Launching an editor is the one unbounded spawn.
- User shells receive the host environment minus `SHARPRAIL_TOKEN`, with `TERM=xterm-256color`,
  `COLORTERM=truecolor` and a UTF-8 locale when none is configured (without one, line editing is
  byte-oriented and a backspace over a multi-byte character corrupts the line).
- No process isolation: a fault in Core is a fault of its composer. Separate processes sharing a state
  directory are not coordinated; the last writer wins.
- Stopping a remote host disposes its terminal service, ending its shells. Local shells end when their
  owning app stops; disposing a terminal view only detaches it.

## Not yet ported

- Login-shell `PATH` and `SSH_AUTH_SOCK` repair for a host launched from Finder, launchd or a service
  manager. On Unix, always probe the login shell once with an OS-base `PATH`, rather than guessing
  completeness from user-directory markers; prepend inherited entries absent from the login result so
  activated environments retain precedence. Use a bounded probe with a non-interactive retry and leave
  the inherited `PATH` untouched on failure.
- A retrying recursive tree removal for teardown, and first-free-port selection for the remote host.
- Host-side routes serving raw workspace files for relative Markdown images (images load through
  `ReadFileAsync` instead). A path's bytes at one commit are served as a gRPC call
  (`ReadContentBytesAsync`), not a route, and are not streamed.
- Crash logging to the state directory and one graceful, idempotent shutdown path shared by launchers.
