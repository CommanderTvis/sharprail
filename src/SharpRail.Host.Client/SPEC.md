# Host client adapters

Upstream: apps/web/src/transport/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The UI's only route to a host. Each host abstraction has two implementations of
the same interface: a direct local adapter delegating to Core in-process, and a
remote proxy over code-first WebSocket RPC. The composition root (`App.cs`) picks one per
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
- Allowed deps: Abstractions, Protocol, System.Net.WebSockets.
- Forbidden: Core, Remote, the UI or Avalonia; requesting or persisting
  current-layout state.

## Behavior

- The project adapter maps independent nullable commit lookup through the same local or WebSocket RPC API;
  selecting a graph commit does not require it to occur in a comparison catalog.
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
  long waits, because an interactive client is waiting.
- The project adapter remembers the root it opened last and sends it with each
  call, so the host needs no per-client session and a reconnect resumes silently.
- `HostConnection` is one client's identity towards a host; the composition root shares one between the
  state proxy and every window's project proxy. A mutation (`ReplayHeaders.IsReplayable`) carries that
  identity, a request id and the connection's unresolved ids. When its call ends `Unavailable`, the
  channel sends it again under the same id with the channel's reconnect backoff until a reply is read,
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
- A remote session is a watcher when the host's first reply says so, and a reconnect resumes in the role it
  has: a watcher asks to watch again, a holder never does, so losing the session while disconnected still
  ends in a detach. `Grid` follows the replies; a host that reports none leaves it at the requested size. A
  chunk that only changes the grid is passed to the reader empty.
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
- One channel per adapter; ordering is per WebSocket RPC call, and state convergence
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

Prompt attachments use `SavePromptImageAsync`: bounded PNG bytes are saved under the host state's
clipboard directory, returning an absolute host path. Local calls run off the dispatcher; remote calls
carry the bytes and return the same host path, never a client-local file path.



## Shared WebSocket calls

`RpcChannel` implements the typed wire client for every remote platform. Adapters share reference-counted
connections by endpoint, client identity and heartbeat configuration. Concurrent calls multiplex within
those connections. Cancellation, deadlines, bounded queues and receive credits follow Protocol's contract.
The channel owns mutation replay and domain error translation; typed generated proxies require no dynamic
code generation in WASM. Terminal fault injection wraps the typed terminal interface.

File imports map the same bounded byte/entry request through direct and remote adapters and return
host paths. File actions preserve the optional originating workspace. Client paths are never sent
as host source paths by the import API.


`LocalBrowserApprovalClient` owns the optional loopback browser consent bootstrap. It validates
literal loopback HTTP(S) targets without DNS lookup, posts using the browser-provided Origin,
consumes the Protocol JSON reply and returns only the temporary credential to the UI. HTTP
rejections retain their status codes for connect-screen guidance. The caller owns cancellation
and the approval deadline; no shorter HttpClient timeout interrupts the two-minute app prompt.

## Language queries

Local language requests run off the dispatcher. Remote requests preserve the explicit workspace and
map caller cancellation back to `OperationCanceledException`; the current project cannot retarget
an in-flight request. Diagnostic freshness, coverage and dependency-file read-only state survive both paths.

Language server inventory maps identically through local and WebSocket adapters for the LSP pane.

Diagnostic scope, selected paths and per-file coverage map identically through both adapters.

Host-wide LSP launcher settings round-trip in the shared host settings snapshot.

Language adapters preserve offset/limit, total/truncated metadata and source preview/body
fields identically across direct and remote calls.

Local and remote language adapters preserve workspace configuration, restart requests, and saved
versus active snapshots. State adapters retain workspace language metadata in every snapshot.


IntelliJ terms review prepares the managed distribution when needed on the workspace host, through direct/off-dispatcher or typed WebSocket adapters. The remote request allows 15 minutes for download and artifact verification; cancellation reaches the host. Clients never read installation files locally, and older hosts report the operation unavailable.


Context consent follows the standard project adapters: direct calls run off the dispatcher, remote calls carry exact reviewed identities and cancellation over typed RPC. Consent and installation live on the selected host; clients do not persist a local replacement receipt.


Language adapters preserve project-root metadata and pagination identically for direct/WebSocket clients. Context local-only installation status uses the existing typed consent operation; it never implies acknowledgement.


ContextAsync uses the same host implementation through off-dispatcher direct calls and generated WebSocket RPC. State mapping preserves enabled Context workspaces; a missing older-host method is unavailable. Native provisioning, authentication and indexing remain on the workspace host.
