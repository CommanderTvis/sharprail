# Remote host server

Upstream: packages/server/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/auth/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

Serves the host over the wire: a Kestrel HTTP/2 server exposing the Protocol
services as thin RPC adapters that delegate to Core, plus token authentication
for every connection. It is both a standalone executable (`Program.cs`, a
remote host on another machine) and an embeddable library (`RemoteServer`),
used by remote deployments and transport integration checks. The UI does not reference this project.

## Boundary

- Owns `RemoteServer.cs` (server composition and authentication), the RPC
  adapters `WorkspaceRpc.cs`, `ProjectRpc.cs`, `StateRpc.cs` and
  `TerminalRpc.cs`, `ProjectSessions.cs` (per-call workspace resolution) and
  `Program.cs` (environment-driven startup).
- Public surface: `RemoteServer.Create(root, address, port, token,
  stateDirectory?, terminals?)` returns an unstarted `WebApplication`; the embedder starts and stops it.
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

## Decisions and trade-offs

- No process isolation: a fatal fault in a Core operation takes the host down.
- No cross-process coordination: two hosts pointed at one state directory are
  last-writer-wins.
- Commands return values directly; only host-state snapshots and terminal output
  stream.
- Expected failures (I/O, argument, invalid operation, access) become
  `FailedPrecondition` or `InvalidArgument` with the message as detail; other
  exceptions stay internal errors.

## Not yet ported

- Serving a static client or host HTTP endpoints (worktree files for relative
  Markdown images).
- A bounded, idempotent shutdown that settles work and disposes sockets, PTYs and
  watchers, awaited by every launcher, with SIGINT/SIGTERM handling and crash
  logging.
- Resolving the login-shell `PATH` at boot.
- Structured leveled diagnostics with rotating on-disk logs.
- A per-client request-result cache keyed by client identity and request id for
  reconnect deduplication.
- Pushed project/workspace lifecycle events.
