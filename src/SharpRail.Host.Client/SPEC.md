# Host client adapters

Upstream: apps/web/src/transport/SPEC.md @ c44534ea

## Responsibility

The UI's only route to a host. Each host abstraction has two implementations of
the same interface: a direct local adapter delegating to Core in-process, and a
remote proxy over code-first gRPC. The composition root (`App.cs`) picks one per
service from `SHARPRAIL_REMOTE`; nothing downstream knows which it got, which is
what makes remoteness an adapter choice rather than a mandatory daemon.

## Boundary

- Owns `HostAdapters.cs` (`IWorkspaceHost`), `ProjectAdapters.cs`
  (`IProjectServices`), `StateAdapters.cs` (`IHostStateService`) and
  `TerminalAdapters.cs` (`ITerminalService` and the remote terminal session).
- Local adapters are pure delegation: no sockets, serialization or copying.
- Remote adapters own channel setup, the bearer token on every call, per-call
  deadlines, mapping DTOs to Abstractions records, and reconnection.
- Allowed deps: Abstractions, Protocol, Grpc.Net.Client, protobuf-net.Grpc.
- Forbidden: Core, Remote, the UI or Avalonia; requesting or persisting
  current-layout state.

## Behavior

- Every remote call carries `authorization: Bearer <token>`; constructing a
  remote adapter without a token fails immediately.
- Deadlines: short (15 s) for cheap workspace and terminal control calls, 60 s
  for project, Git and state calls. The host's own network Git budget must stay
  under that ceiling so its error, naming the ref, wins the race against a bare
  deadline. Long-lived streams (state watch, terminal) carry no deadline.
- Channels reconnect quickly (250 ms initial, 3 s maximum backoff) instead of
  gRPC's two-minute ceiling, because an interactive client is waiting.
- The project adapter remembers the root it opened last and sends it with each
  call, so the host needs no per-client session and a reconnect resumes silently.
- The state watch yields complete snapshots and ends with an exception when the
  transport drops; the caller (`SharedState`) resubscribes and receives a fresh
  snapshot, so nothing missed while disconnected stays stale.
- A remote terminal session attaches, retrying a connection that fails before
  the host's reply with the same fresh attach, so a lost reply still yields one
  shell and one replay. After a mid-stream loss it reconnects from its last read
  position for up to `ReconnectTimeout` (30 s), queues a reattach replay ahead of
  new output, and keeps the last requested size for the reattach. A start
  failure (`FailedPrecondition`) surfaces as an `IOException` with the host's
  reason; only transport-level failures are retried. Exit and detach are one-shot
  outcomes, never replayed; a session has a single reader.
- A `unix:` address dials a Unix domain socket, which is how local Ghostty tabs
  reach the app's in-process terminal relay.

## Decisions

- Same interface, two adapters, rather than a local loopback server: embedded
  mode pays no socket or serialization cost, and local/remote parity is checked
  against one contract.
- One channel per adapter; ordering is per gRPC call, and state convergence
  relies on full snapshots with revisions rather than on cross-call ordering.

## Not yet ported

- A connection status surface with a generation per reconnect, driving
  re-hydration explicitly rather than per-subscription retries.
- A protocol version sent at connect, and capability gates that hide actions an
  older host cannot serve; the Changes diff withholds every revert/undo
  affordance from a host that predates the change write path. Resource metadata
  needs no gate: a reply without it reads as text, which is what that host's own
  client showed.
- Reconnect-safe unary requests: replaying an in-flight mutation under the same
  request id with host deduplication, plus ack/resume frames, so an accepted
  mutation can neither report a false failure nor run twice. Today a unary call
  lost with its connection fails to the caller.
- Named host error codes surfaced as a typed exception distinct from generic
  failures.
- A per-request timeout override for calls answered only after a human acts.
- An HTTP base derived from the endpoint for host-served worktree files.
- Re-reading already-known workspace rows after reconnect without treating it as
  membership reconciliation.
