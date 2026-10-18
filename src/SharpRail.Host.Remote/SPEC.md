# Remote host server

Upstream: packages/server/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/auth/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/mcp/SPEC.md @ b047c8f2 (CommanderTvis fork)

## Responsibility

Serves the host over the wire: a Kestrel HTTP/1.1 WebSockets server exposing the Protocol
services as thin RPC adapters that delegate to Core, plus token authentication
for every connection. It is both a standalone executable (`Program.cs`, a
remote host on another machine) and an embeddable library (`RemoteServer`),
used by remote deployments, the app's optional runtime listener and transport integration checks.

Windows hosts compose the same terminal service and catalog as embedded Windows
hosting, including recording persistence, MCP identities and plugin terminal seams.
The platform choice belongs to Core's process backend, not to the wire protocol.

## Boundary

- Owns `RemoteServer.cs` (server composition and authentication), `McpRoute.cs` (terminal MCP), the RPC
  adapters `WorkspaceRpc.cs`, `ProjectRpc.cs`, `StateRpc.cs`,
  `TerminalRpc.cs` and `TerminalCatalogRpc.cs` and `PluginRpc.cs`, `ProjectSessions.cs` (per-call workspace resolution) and
  `Program.cs` (environment-driven startup).
- Embedders create an unstarted server and control its lifetime. They may supply terminal services
  and plugin integration points. The app can compose the same loopback server for its own host.
- `RemoteServer.CreateListener` borrows an embedded host's existing state, terminals and plugin runtime.
  `HostListener` serializes runtime Start/Stop, reports the bound endpoint, disposes failed starts and
  prevents restart after disposal. Neither path recreates or owns the borrowed services. Embedded
  listeners have a passive host lifetime, leaving process signals and app shutdown to their owner.
  Listener shutdown allows five seconds for connections to drain, then aborts remaining transport calls.
  Embedded listeners resolve configuration from the executable directory without file reload watchers;
  the current workspace's appsettings files do not configure or delay serving.
- Allowed deps: Core, Protocol, Abstractions, ASP.NET Core/Kestrel,
  protobuf-net.
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

`scripts/host.sh` builds and launches only this host, binding to `0.0.0.0`.
It defaults to Release and uses `SHARPRAIL_TOKEN` or generates a fresh session token, and prints a terminal
QR code and `sharprail://connect?host=…&token=…` link for the Android client.
The advertised address is the first active non-loopback IPv4 address; set
`SHARPRAIL_CONNECT_HOST` to override its hostname/IP. The port, root, state directory
and build configuration retain their environment overrides. The QR helper uses
the pinned QRCoder package through the checkout's .NET file-based app support.

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
- An embedded app can start and stop authenticated WebSocket serving while it runs. Remote clients
  share its live state, plugins and terminal service; the app keeps its direct adapters and project
  sessions. Stopping serving cancels remote state, file, plugin and terminal streams, detaches
  remote terminal attachments, and leaves shells and local calls alive. The app stops serving
  before disposing plugins, loopback routes or PTYs at exit. No second state directory is opened.
- `ProjectRpc.ReadContentBytesAsync` delegates to the session's host and returns bytes and metadata as one
  message, sized by the existing message limits; diff-side replies carry metadata and revisions.
- Plugin and loopback cleanup run without the caller's synchronization context before the host
  releases its terminals. Stopping an embedded server from a UI thread must not deadlock its cleanup.
- A terminal call is one attachment: dropping the call detaches, and the shell
  keeps running on the host until it exits, its tab closes or the host stops.
  Output goes to the client that holds the session and to every watching call; another plain attach takes
  the session over and the previous holder's call ends with a detached frame. A watching call reports the
  holder's grid with every frame, and its data, resize and kill messages change nothing.
- Request bodies and WebSocket RPC messages are capped from `FileLimits`, sized for the
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
- The protocol is Core's `McpServer` (see
  [Specs.SPEC.md](../SharpRail.Host.Core/Specs.SPEC.md)); all tools come from active plugins for that terminal;
  the route only resolves the token and relays JSON.

## Workspace file changes

Project RPC also exposes an independent nullable commit lookup for explicit commit scopes, preserving
the same metadata and missing-object behavior as the local adapter.

Project file-change streams capture the request's workspace root and deliver relative paths,
Git metadata invalidation and rescan requests. They end on client cancellation or host shutdown.

## Decisions and trade-offs

- No process isolation: a fatal fault in a Core operation takes the host down.
- No cross-process coordination: two hosts pointed at one state directory are
  last-writer-wins.
- Commands return values directly; host-state snapshots, workspace file changes and terminal output
  stream.
- Expected failures (I/O, argument, invalid operation, access) become
  `FailedPrecondition` or `InvalidArgument` with the message as detail; other
  exceptions stay internal errors. A `HostException` from the project or state service additionally
  names its code in the `x-sharprail-error-code` trailer.

## Not yet ported

- Host HTTP endpoints for worktree files used by relative Markdown images.
- A bounded, idempotent shutdown that settles work and disposes sockets, PTYs and
  watchers, awaited by every launcher, with SIGINT/SIGTERM handling and crash
  logging.
- Resolving the login-shell `PATH` at boot.
- Structured leveled diagnostics with rotating on-disk logs.

## Tailscale pairing

The optional [Tailscale plugin](../SharpRail.Plugins.Tailscale/SPEC.md) supplies approved client-token
validation through its injected serving bridge. The listener accepts those tokens alongside its configured
master token, rejects revoked tokens, and aborts existing calls within one second of revocation. Embedded
serving can start on the Tailscale address after approval; standalone serving retains its configured bind
and port. The plugin owns bootstrap and peer identity; Remote owns WebSocket RPC listener lifetime and authentication.

Prompt attachments use `SavePromptImageAsync`: bounded PNG bytes are saved under the host state's
clipboard directory, returning an absolute host path. Local calls run off the dispatcher; remote calls
carry the bytes and return the same host path, never a client-local file path.




## Remote transports and browser assets

The normal host endpoint accepts WebSocket RPC at `/rpc` for desktop, Android and browser clients.
`WebSocketRpc` authenticates each call's bearer metadata and dispatches the generated method table
directly into the existing RPC adapters. Paired Tailscale credentials are rechecked while calls run.
Tokens never travel in URLs. The desktop listener's endpoint and token work for every client.
The standalone host can serve public browser assets from `SHARPRAIL_BROWSER_ROOT` on the same port,
with COOP/COEP isolation headers. `SHARPRAIL_PORT` configures that single endpoint.
`BrowserAssets` negotiates precompressed Brotli/gzip sidecars for GET/HEAD using Accept-Encoding
quality values, with Brotli preferred at equal quality. Content types describe the original asset;
Vary: Accept-Encoding and representation-specific static-file ETags preserve cache correctness.
Missing/stale sidecars fall back to identity, or 406 when identity is explicitly forbidden.
Default documents retain negotiation; direct sidecar URLs return 404. Compression applies only
to public static assets, never RPC. No request-time compression is performed.

File-transfer RPC maps entry names, directory flags and bytes to the host import operation, preserving
the expected workspace and optional destination. The reply contains host paths. File actions preserve
the expected workspace so a client switch cannot redirect a pending drop.


## Local browser approval

An embedded app listener offers POST `/browser/connect` to loopback browser origins. Both TCP
endpoints and the HTTP Host must be loopback; the Origin must be a canonical HTTP(S) loopback
origin. Missing/null, nonlocal and malformed origins are rejected before any prompt or CORS
permission. OPTIONS preflight returns only the validated origin and never asks for consent.
Standalone servers without an app window retain token authentication and offer no approval endpoint.

`HostListener` owns one `LocalBrowserConnections` generation per Start/Stop. The app supplies an
asynchronous consent callback; no window or handler means deny. One request can await consent
at a time, requests expire after two minutes, denials have a short cooldown, and grants are bounded
to 32 per listener. Cancellation, timeout or stopping the listener dismisses the prompt and issues
no credential. The response is no-store and returns a newly generated 256-bit credential, never
the listener’s master token.

Every approved credential is bound to the exact browser Origin and loopback transport on each
RPC call. It can reconnect during the browser session. In-memory origins are listed in the local
Host settings, where Revoke access invalidates all sessions from that origin. Existing subscriptions
are cancelled within the existing one-second credential recheck interval; new calls fail immediately.
Stopping/restarting the listener forgets grants and rejects old credentials. Manual master-token
and Tailscale authentication retain their existing behavior. Approval is transport bootstrap rather
than a new host domain operation, so the typed RPC contract and protocol version are unchanged.

The 2026-10-17 focused transport checks cover loopback addresses without DNS trust, Host/Origin
validation before prompting, exact-origin CORS, pending-request exclusion, denial cooldown, client
cancellation (including a callback that ignores cancellation), real authenticated WebSocket calls,
wrong/missing origins, master-token compatibility, active-call revocation and restart expiry.
Headless app checks additionally exercise the compiled consent dialog, Deny focus/Escape, browser
cancellation, Settings revocation and dismissal when the listener stops.

## Language queries

The project RPC maps typed language queries to the host-owned service. Terminal-authenticated MCP
also exposes navigation, diagnostics and status; workspace overrides are restricted to the terminal's
Git project. Host shutdown ends language processes, while stopping an embedded listener leaves them alive.

The terminal-authenticated MCP route exposes the three core LSP tools to both Codex and Claude tokens,
independently of enabled SharpRail plugins. Claude CLI discovery uses its bundled plugin declaration.

Language RPC carries explicit diagnostic scopes, file sets and per-file coverage. Agent MCP
diagnostics defaults to a project source scan; `paths` selects edited files and `path` selects one.
These scans use running servers only and never provision or restart them.

Host settings broadcasts include LSP launcher choices; the language catalog is a workspace-free
read for Settings and does not start or install a process.

Language RPC maps paged navigation, hover and selected symbol bodies including source excerpts
and truncation metadata. Terminal MCP exposes the same host operations in compact, bounded
responses with explicit diagnostic coverage and continuation metadata.

Language RPC and terminal-authenticated MCP expose workspace configuration read/save/restart,
including initialization options, server settings and clangd include/compiler/database options.
MCP configuration mutations remain scoped to the terminal's project workspaces. State RPC exposes
persisted workspace configuration to connected clients.


The authenticated project RPC carries user-interface IntelliJ terms review/acknowledgement separately from LSP MCP. Server-side identity checks and storage stay on the host; no agent acceptance tool is exported.

The standalone host may explicitly enable `SHARPRAIL_HEADLESS_MCP=1` to expose
POST `/mcp` with the production language tools and the configured root as default
workspace, without a GUI, terminal or agent. It requires the host bearer token
using constant-time digest comparison. This grants host-level workspace access;
it is not a terminal-scoped credential. The route is absent by default and offers
no license-acceptance operation. Malformed JSON receives a JSON-RPC parse error.


Context consent is authenticated project RPC only, delegating review and explicit accept/download to Core. Terminal MCP exposes no acceptance operation; its credentials cannot implicitly acknowledge agreements. Downloads stay on the host.


Language RPC maps detected project roots and provenance; lsp_status supports operation projects with bounded pagination. It starts no servers. Context consent RPC also supports local-only installation status and cannot infer user acceptance from an installed binary.


Context RPC delegates typed workspace operations to Core. The terminal-authenticated namespace and explicit headless MCP route include context_search/status/index, bound to the calling workspace. They cannot accept terms, enable workspaces, sign in or select arbitrary native tools. Logs and outputs remain bounded; lifecycle processes belong to the host, not the listener.
