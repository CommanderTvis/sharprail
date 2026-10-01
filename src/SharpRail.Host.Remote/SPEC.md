# Remote host server

Upstream: packages/server/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/auth/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/mcp/SPEC.md @ b047c8f2 (CommanderTvis fork)

## Responsibility

Serves the host over the wire: a Kestrel HTTP/2 server exposing the Protocol
services as thin RPC adapters that delegate to Core, plus token authentication
for every connection. It is both a standalone executable (`Program.cs`, a
remote host on another machine) and an embeddable library (`RemoteServer`),
used by remote deployments and transport integration checks. The UI does not reference this project.

## Boundary

- Owns `RemoteServer.cs` (server composition and authentication), `McpRoute.cs` (terminal MCP), the RPC
  adapters `WorkspaceRpc.cs`, `ProjectRpc.cs`, `StateRpc.cs`,
  `TerminalRpc.cs` and `TerminalCatalogRpc.cs` and `PluginRpc.cs`, `ProjectSessions.cs` (per-call workspace resolution) and
  `Program.cs` (environment-driven startup).
- Public surface: `RemoteServer.Create(root, address, port, token,
  stateDirectory?, terminals?, plugins?)` for a full host (`plugins` adjusts the
  plugin runtime's seams, as the checks do) returns an unstarted `WebApplication`;
  the embedder starts and stops it. `LoopbackServer` is public so the app's own host
  composes the same server.
- Allowed deps: Core, Protocol, Abstractions, ASP.NET Core/Kestrel,
  protobuf-net.Grpc.AspNetCore.
- Forbidden: the UI or Avalonia; product behavior in the adapters. An RPC
  method maps DTOs to and from Core calls and translates expected failures to
  status codes, nothing more; behavior lives in Core so local and remote paths
  cannot diverge.

## Startup

`Program.cs` reads `SHARPRAIL_ROOT` (default the working directory),
`SHARPRAIL_BIND` (default `127.0.0.1`), `SHARPRAIL_PORT` (default 54123),
`SHARPRAIL_TOKEN` (required) and `SHARPRAIL_STATE_DIR` (default
`~/.sharprail/host` on the host machine). It prints `SHARPRAIL_HOST_READY
<unix ms>` once listening, for launchers and checks to wait on. Binding beyond
loopback is an explicit opt-in via `SHARPRAIL_BIND`.

## Connection authentication

- Every request must carry `authorization: Bearer <token>`. A middleware ahead of
  all services compares SHA-256 digests of the expected and actual header in
  constant time and answers 401 otherwise, so no service can be mapped without
  it and the comparison leaks neither length nor prefix.
- A server cannot be created without a non-blank token; there is no
  unauthenticated mode, including for the loopback relay.
- The token is a shared host secret, not a user identity: every authenticated
  client has the same authority. It is never written into a DTO, a log line or a
  shell's environment (terminal children have it removed), and relays receive it
  through a private one-use file rather than argv.
- The terminal relay socket is owner-only (mode 0600, normally in a private
  directory) and uses a random per-process token, so reaching it requires both
  the user's filesystem access and the secret.

The handshake is a state-service method behind the same authentication as every other call, so an
unauthenticated peer learns nothing about the host's version.

## Composition and state

- One `WorkspaceHost` for the startup root, one `HostStateStore` (persisted in
  the state directory, or in memory without one), `ProjectSessions`, and a
  terminal service. The host owns its `PtyTerminalService`, which records screens in
  the `terminals` subdirectory of the state directory, unless the embedder
  supplies one; supplied terminals outlive the server, which is how local shells
  survive relay restarts while the app runs.
- `ProjectSessions` resolves each project call from the workspace-root header,
  falling back to the startup root for a missing, relative or NUL-containing
  value, and caches one `ProjectServices` per root. Opening a project uses a
  detached instance so resolution never disturbs a cached workspace. Clients
  therefore never share a current project, and reconnects need no session.
- `RequestReplayCache` deduplicates mutations: the project service's save, Git action, workspace action,
  revert, undo, pull-request opening and open-in-editor, and the state service's change. Spec authoring
  and the terminal catalog's writes are not replayed yet. A call carrying
  `x-sharprail-client` and `x-sharprail-request` runs once per pair, detached from the call so a dropped
  connection cannot abandon it half done (it ends with the host instead); a replay of the pair awaits and
  returns the same outcome, success or failure. The request's method and serialized bytes are its
  fingerprint: an id reused with another payload is `InvalidArgument`. Each call's `x-sharprail-resume`
  names the client's still-unresolved ids and every other settled result is released. A client may hold
  512 requests (`ResourceExhausted` beyond) and 16 MiB of serialized replies; a reply over that budget is
  not kept and a replay of it is `FailedPrecondition`, never a second run. At most 64 clients are
  remembered, the least recently seen one without running work going first. Results are memory only, so
  a restarted host runs a replay afresh. A call without the metadata behaves as before.
- State watch streams end on application stopping, so graceful shutdown never
  waits for watchers. Changes are broadcast to every subscriber as full snapshots.
- `ProjectRpc.ReadContentBytesAsync` delegates to the session's host and returns bytes and metadata as one
  message, sized by the existing message limits; diff-side replies carry metadata and revisions.
- File watch streams send host filesystem invalidations for the workspace in request metadata;
  cancellation or application stopping releases each subscription's native watchers.
- A terminal call is one attachment: dropping the call detaches, and the shell
  keeps running on the host until it exits, its tab closes or the host stops.
  Output goes only to the attached client; another attach takes the session over
  and the previous call ends with a detached frame.
- Request bodies and gRPC messages are capped from `FileLimits`, sized for the
  largest save.

## Plugins

- `RemoteServer.Create` composes a `PluginRuntime` from the state directory (external plugins under
  `<stateDir>/plugins`), the state store, the host's `PtyTerminalService` and the loopback server, starts it
  before the first client can call, and disposes it first when the host stops, before the loopback server and
  the terminals. `PluginRpc` maps `IPluginRpc` onto it; a `PluginCallException` becomes the status code the
  contract names, and subscription streams end on application stopping.
- The host serves every plugin file a client asks for through `ReadFileAsync`, contained in the plugin's
  directory, so a remote host's external UI halves and assets reach the local app.

## Loopback server: MCP and plugin routes

- `LoopbackServer` is a loopback-only HTTP/1.1 server (agents' MCP clients do not speak prior-knowledge
  HTTP/2) that starts on first use of its `BaseUrl` and stops with its host: a remote host starts it once
  listening, the app's own host when its terminal relay starts or a plugin asks for its URL. It serves
  `POST /mcp/<token>`, where `GET` and `DELETE` answer 405, since every tool is request/response and there
  is no SSE stream or session, and `/plugin/<id>/<subpath>` for any method, which reaches the plugin
  runtime's route dispatch and answers 404 for an unknown, disabled or routeless plugin.
- The terminal relay no longer starts MCP itself; the app's relay starts the loopback server it is given.
- The route token is the per-terminal identity `PtyTerminalService` mints and stamps into the shell as
  `THINKRAIL_MCP_URL`; it resolves to that terminal's workspace root and nothing else. An unknown or closed
  terminal's token is 404 before any protocol handling. The host bearer token is never involved and never
  reaches a shell.
- The protocol and tools are Core's `McpServer` (see
  [Specs.SPEC.md](../SharpRail.Host.Core/Specs.SPEC.md)) plus the active plugins' tools for that terminal;
  the route only resolves the token and relays JSON.

## Decisions and trade-offs

- No process isolation: a fatal fault in a Core operation takes the host down.
- No cross-process coordination: two hosts pointed at one state directory are
  last-writer-wins.
- Commands return values directly; only host-state snapshots and terminal output
  stream.
- Expected failures (I/O, argument, invalid operation, access) become
  `FailedPrecondition` or `InvalidArgument` with the message as detail; other
  exceptions stay internal errors. A `HostException` from the project or state service additionally
  names its code in the `x-sharprail-error-code` trailer.

## Not yet ported

- Serving a static client or host HTTP endpoints (worktree files for relative
  Markdown images).
- A bounded, idempotent shutdown that settles work and disposes sockets, PTYs and
  watchers, awaited by every launcher, with SIGINT/SIGTERM handling and crash
  logging.
- Resolving the login-shell `PATH` at boot.
- Structured leveled diagnostics with rotating on-disk logs.
