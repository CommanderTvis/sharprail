# Host client adapters

Upstream: apps/web/src/transport/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The UI's only route to a host. Each host abstraction has two implementations of
the same interface: a direct local adapter delegating to Core in-process, and a
remote proxy over code-first gRPC. The composition root (`App.cs`) picks one per
service from `SHARPRAIL_REMOTE`; nothing downstream knows which it got, which is
what makes remoteness an adapter choice rather than a mandatory daemon.

## Boundary

- Owns `HostAdapters.cs` (`IWorkspaceHost`), `ProjectAdapters.cs`
  (`IProjectServices`), `StateAdapters.cs` (`IHostStateService`),
  `TerminalAdapters.cs` (`ITerminalService` and the remote terminal session),
  `TerminalCatalogAdapters.cs` (`ITerminalCatalogService`; refusals surface as the exceptions the local
  catalog throws, and a host without the service as `NotSupportedException`) `PluginAdapters.cs` (`IPluginService`) and
  `HostCalls.cs` (what every unary project and state call shares).
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
  deadline. Opening a pull request pushes and
  runs `gh`, so that call alone carries a 5 minute deadline. Long-lived streams (state watch, file watch, terminal) carry no deadline.
- `HostRequest.WithTimeout(value)` replaces that deadline for the project and state calls the current
  async flow starts until the scope is disposed; scopes nest and restore the enclosing one. It is for a
  call the host answers only after a person acts. Raising it is safe; lowering it below the host's own
  budgets hides their errors behind a bare deadline. The embedded host has no deadline and ignores it.
- Channels reconnect quickly (250 ms initial, 3 s maximum backoff) instead of
  gRPC's two-minute ceiling, because an interactive client is waiting.
- The project adapter remembers the root it opened last and sends it with each
  call, so the host needs no per-client session and a reconnect resumes silently.
- `HostConnection` is one client's identity towards a host; the composition root shares one between the
  state proxy and every window's project proxy. A mutation (`ReplayHeaders.IsReplayable`) carries that
  identity, a request id and the connection's unresolved ids. When its call ends `Unavailable`, the
  interceptor sends it again under the same id with the channel's reconnect backoff until a reply is read,
  the caller cancels or the call's own deadline passes, and the host answers with the first run's outcome:
  an accepted mutation neither reports a false failure nor runs twice. Replay needs the host's
  deduplication, so it happens only when the last handshake reported `HostProtocol.RequestReplay`;
  otherwise a mutation lost with its connection fails to the caller. Reads are not replayed.
- `HostConnection` is also the connection status surface: `Status` (`Connecting`, `Connected`,
  `Disconnected`), a `Generation` that is 1 for the first connection and rises by one per reconnect, and
  `Changed`. The owner of the state subscription reports transitions (`Report`), since a delivering state
  watch is the one signal that the host is reachable; an embedded host is connected at generation 1 for
  good. A consumer re-reads under each new generation instead of retrying each of its own subscriptions.
  Workspace rows are part of that read: they are Git's worktree list plus the labels of the state
  snapshot, both read again under the new generation, so a row whose branch or name changed while the
  client was away is corrected. There is no workspace registry or lifecycle push whose missed event a
  separate row repair would have to cover.
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
- A terminal attach carries the tab key, and the first attached reply's prefill
  becomes the session's `Prefill`; a reconnect's attach never sets it again.
- The local plugin adapter passes params, results and payloads through as objects.
  The remote one writes them as JSON with `PluginJson.Options` and returns
  `JsonElement`s (null for JSON `null`), so callers read every value with
  `PluginJson.Convert`. It maps `NotFound`, `FailedPrecondition`, `InvalidArgument`
  and `Unknown` back to the same `PluginCallException` a local call throws; other
  status codes stay transport failures, except that a call or subscription the caller
  cancels ends with `OperationCanceledException`, as locally. A subscription ends with
  an exception when the transport drops, and the caller resubscribes and re-reads its snapshot. Plugin
  file reads accept messages up to 256 MiB, since a UI half's assemblies arrive whole.
- Remote host-state snapshots map plugin namespaces back to `JsonElement` objects,
  the roster, agent records and the platform (null when the host sent none).

## Decisions

- Same interface, two adapters, rather than a local loopback server: embedded
  mode pays no socket or serialization cost, and local/remote parity is checked
  against one contract.
- The remote state adapter sends the client's protocol version in the handshake and maps `Unimplemented`
  to version 0. `HostCapabilities.Supports(version, introducedAt)` treats no handshake (null) and a lower
  version as unsupported.
- The project adapter rethrows a change write refusal (the `x-sharprail-change-code` trailer) as the
  `ChangeException` the embedded host throws, so callers branch on one type.
- `HostCalls.cs` holds the interceptor every unary call of the project and state proxies passes through.
  It rethrows a failure carrying the `x-sharprail-error-code` trailer as `HostException`; any other failure
  stays an `RpcException`, so having a code is how a caller tells a specific failure from a failed call.
- One channel per adapter; ordering is per gRPC call, and state convergence
  relies on full snapshots with revisions rather than on cross-call ordering.

## Not yet ported

- Gates for the operations added since `HostProtocol.Current` was last raised. The one gate in use is
  `HostProtocol.ChangeWritePath`: the diff's revert and undo controls consult
  `SharedState.Supports(introducedAt)`, which is false until the handshake answers and again while
  disconnected. Resource metadata needs no gate: a reply without it reads as text, which is what that
  host's own client showed.
- Raising `HostProtocol.Current` to `RequestReplay` (3). Until a host reports it, mutation replay stays
  off against every host, including this build's own, and a mutation lost with its connection fails.
- An HTTP base derived from the endpoint for host-served worktree files.
