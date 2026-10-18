# Host wire contracts

Upstream: packages/contracts/SPEC.md (revision: [STATUS.md](../../STATUS.md#upstream-tracking))

## Responsibility

The client↔host wire spine for remote hosts: the single source of truth for the
typed protobuf service interfaces, DTOs, generated clients and shared WebSocket framing runtime.
The runtime owns multiplexing, call lifetime and bounded flow control; host behavior stays in the adapters. Embedded (local) mode never touches this
project: local adapters call Core directly, with no serialization.

## Boundary

- Owns the wire: the five services `IWorkspaceRpc` (`WorkspaceContract.cs`),
  `IProjectRpc` (`ProjectContract.cs`), `IStateRpc` (`StateContract.cs`),
  `IPluginRpc` (`PluginsContract.cs`), `ITerminalRpc` (`TerminalContract.cs`) and `ITerminalCatalogRpc`
  (`TerminalCatalogContract.cs`), their `[ProtoContract]` request/reply
  classes, and the metadata keys a call carries.
- Domain records belong in `SharpRail.Host.Abstractions`; DTOs here are their
  serialized projection. Client and Remote adapters map between the two, and both
  directions must stay consistent with the public abstraction. A DTO is never
  exposed to the UI.
- Allowed deps: protobuf-net and the .NET WebSocket/channel APIs.
- Forbidden: any reference to Core, Remote, Client, the UI or Avalonia. Contracts
  describe host behavior and compatibility, never the launcher or deployment
  that supplies it.

## Contents

- Workspace: the host's startup workspace identity and its root entries.
- Project: open a project (resolving the workspace/project roots), classify a
  host path before opening it (`InspectProjectReply.Kind`: repository, initable,
  missing, not a directory), list and
  read files, save a file with its original text for conflict detection, list
  specs, the spec graph (`SpecGraphReply`: specs, edges, dangling links, duplicate
  ids, parent cycles) and whether the workspace has durable specs, Git snapshot, commit list, per-file diff and diff sides, Git actions,
  open pull request lookup, pull request draft and opening, branch catalog (optional default-base fetch; local, per-remote rows, the
  default base and suggested new worktree path/branch), editor listing and
  open-in-editor. Git scope travels as a string (`all` when empty); the comparison
  branch is the review target. Commit listing is its own operation so the commit
  catalog never requires a full working-tree snapshot.
- `PrewarmWorkspaceAsync` names a workspace root whose watchers the host should start ahead of a
  subscription; it answers empty and is only a hint.
- Project file watching is a server stream with no deadline. `FileChangeReply` carries up to 100
  workspace-relative paths plus `Rescan` for startup registration, overflow or pathless invalidations.
- State: one complete `StateReply` snapshot (revision, settings, custom layout
  presets, open and recent projects with one `ProjectRecordMessage` each (id,
  path, slug, last opened), workspace labels, workspaces per project, plugin settings, roots, roster, agent records and platform),
  `ChangeAsync` taking an ordered batch of `(Kind, Key, Value)` changes and
  returning the resulting snapshot, and a server-streamed `WatchAsync` of full
  snapshots. Custom presets are the only layout value on the wire; current and
  default presets, frames and geometry stay client-local.
- Agent records carry the session's latest model and effort through snapshots and broadcasts.
  Empty strings on the wire represent unknown values and map back to null in the client.
- Terminal catalog: `OpenWorkspace`, `Reserve`, `CloseTab` and `CloseWorkspace` each return the complete
  catalog snapshot they produced, and a server-streamed `WatchAsync` yields full snapshots. A host that
  predates the service answers `Unimplemented`, which the client adapter reports as unsupported; the
  protocol version was not raised for it.
- Terminal: one bidirectional `RunAsync` stream per attachment. The first input
  is an attach (session id, workspace root, client id, size, resume offset, tab
  key); the first output acknowledges it with `Created`, the replay bytes, the
  stream position and, for a shell a plugin revives, the prefill text and whether
  to submit it. An attach may yield and ask to watch; the acknowledgement then says `Watching`, and it and
  every chunk carry the grid the shell runs at, a chunk without data marking a resize alone. These are added
  fields: a host that predates them acknowledges such an attach as detached and an older client never asks,
  so the protocol version was not raised. Later inputs carry data, resize or kill; later outputs carry data with
  positions and end with exactly one of exited (with code) or detached.
  `IsBusyAsync` and `CloseAsync` are unary.
- Plugins: the roster (`ListAsync`, `RescanAsync`, `RetryAsync`), one generic
  `CallAsync` (plugin id, method name, params JSON → result JSON, `null` for a null
  result), one generic server-streamed `SubscribeAsync` (plugin id, channel, optional
  key JSON → payload JSON, never a replay) and `ReadFileAsync` (a file of the
  plugin's directory, `Found` false when missing). JSON is always `PluginJson.Options`;
  roster enums travel as its camelCase names and absent strings as empty. A failed
  call maps one to one: `Unknown` is `NotFound`, `Disabled` is `FailedPrecondition`,
  `InvalidParams` is `InvalidArgument` and `Failed` is `Unknown`, the message riding
  the status detail.

## Decisions

- Code-first contracts rather than `.proto` files: C# interfaces are the schema,
  shared by the server and the client proxies, so there is no generation step.
- RPC adapters implement these interfaces directly, so missing or mistyped methods fail compilation;
  no separate string-keyed method registry needs synchronization with the wire contract.
- Snapshots, not deltas. Every state reply and broadcast is the complete
  host-state snapshot with a monotonic revision, so a replayed, duplicated or
  post-reconnect frame is idempotent and a client rehydrates whatever it missed.
- Remote calls carry the workspace, not a session. Project calls send the
  workspace root the client last opened in the binary `x-sharprail-root-bin`
  header (`ProjectHeaders.Root`, UTF-8 so non-ASCII paths survive), so clients
  never share a current project and a reconnecting client needs no server
  session to resume.
- Terminal positions are byte offsets into the session's output. Resuming from an
  offset is what makes reconnects show nothing twice; position rides every output
  frame rather than being inferred from lengths.
- Failures use WebSocket RPC status codes, not a bespoke error-code enum. A code earns a
  distinct meaning only when a client behaves differently for it: terminal attach
  distinguishes `FailedPrecondition` (the shell cannot start; show the reason)
  from transient transport codes (reconnect); state changes report invalid input
  as `InvalidArgument`. Everything else stays the status detail text.
- The change write path (`RevertChangeAsync`, `UndoChangeAsync`; protocol version 2) names the scope, the
  target as 1-based inclusive line spans on both sides (a zero count is an insertion point before the
  start; never a patch or hunk header, since the client must not dictate bytes) and the SHA-256 of both
  sides the client saw. A hash is never empty, so an omitted one means the side is absent. The reply is a
  receipt (before/after hash, length and mode, plus the trash claim path for a whole-file removal) that is
  also the undo token; an undo's receipt is itself undoable once, so redo needs no third operation.
  Receipts are host memory only (per workspace at most 20 and 64 MiB of held bytes, oldest evicted,
  newest always kept), because Git and the OS trash already back recovery.
- A refused change is a `FailedPrecondition` status carrying the failure in the `x-sharprail-change-code`
  trailer (`StaleView`, `ScopeImmutable`, `RangeInvalid`, `ReceiptUnknown`, `UnsupportedChange`); the
  client proxy rethrows it as the same `ChangeException` the embedded host throws.
- A named failure is a `FailedPrecondition` status carrying its `HostErrorCode` in the
  `x-sharprail-error-code` trailer (`HostHeaders.ErrorCode`: `UnknownCommit`, `NotGit`, `AlreadyOpen`) on
  the project and state services; the client proxies rethrow it as the `HostException` the embedded host
  throws. An older client ignores the trailer and shows the detail text. Upstream's other codes have no
  counterpart here: a rejected push is `PrResult.Action == "authFailed"`, and the rest are chat failures.
- Mutations are replayable (`ReplayContract.cs`, protocol version 3 as `HostProtocol.RequestReplay`). The
  client sends its identity (`x-sharprail-client`, one per client process), a request id that stays the
  same across replays (`x-sharprail-request`) and its complete set of unresolved ids
  (`x-sharprail-resume`) with every replayable call (`ReplayHeaders.IsReplayable`). WebSocket RPC has no
  per-connection frame to carry upstream's `ack` and `resume` messages, so one header does both jobs: an
  id missing from it has had its reply read, and restating the whole set on each call repairs anything a
  lost call failed to say. An older host ignores the headers, so a client replays only against a host
  whose handshake reaches the version.
- `DiffSidesReply` carries a hash per side and the original commit as additive members 3 to 5, then
  content metadata per side and each side's revision as 6 to 9; `DocumentReply` gains metadata as 4. An
  older client ignores them. `ReadContentBytes` takes a path and a revision (`WorkingTree` says null, since
  null and empty are the same on the wire) and replies with the raw bytes and their metadata.
- Message size limits come from `FileLimits` in Abstractions (read and save
  messages sized from the editable-file limit), so both ends agree on one number.
- Credentials never ride a DTO: authentication is the `authorization` metadata,
  owned by the transport.

- Settings carry the core file-type icon switch as an additive disabled flag, so an omitted field keeps the default enabled. Protocol version 7 gates writes to hosts that understand the new setting; local and remote adapters preserve the same value.
- The state service answers `Handshake` with the protocol and build versions. The request carries the
  caller's own version (0 when unknown); the host records nothing from it yet. Existing member numbers
  never change; a host without the method answers `Unimplemented`.

## Invariants

- Adding a field means a new `ProtoMember` number; numbers are never reused or
  renumbered, and defaults must read as the old behavior for an older peer.
- Wire data is untrusted: the host validates roots, paths and state changes; a
  client validates nothing it could not also receive from a peer of another
  version.

## Not yet ported

- Additive, version-gated workspace activity timestamps, settled/active override and its timestamp,
  settle/unsettle mutations returning the updated record, and a nullable idle-day setting (seven days
  by default; null means never). Full snapshots and lifecycle updates converge clients; the live/shelf
  partition stays client-derived. The native protocol needs its own capability version rather than
  copying upstream's numeric version (see
  [Workspaces.SPEC.md](../SharpRail.Host.Core/Workspaces.SPEC.md#not-yet-ported)).
- Pushed invalidations beyond host state and the lifecycle stream: a worktree
  file-change nudge (`workspace.fsChanged`) and a host update notice.
- `git init` by path and a `GitDiffScope` of a single commit carried on file diffs
  as a typed value.
- A host HTTP endpoint for worktree files (relative Markdown images over remote).
- Resource metadata on file reads and diff sides is ported, including a byte-only file read answered as
  empty text plus metadata, with the bytes fetched over WebSocket RPC rather than the host's file/blob HTTP
  endpoints. Still open: a streamed rather than single-message transfer.

Prompt attachments use `SavePromptImageAsync`: bounded PNG bytes are saved under the host state's
clipboard directory, returning an absolute host path. Local calls run off the dispatcher; remote calls
carry the bytes and return the same host path, never a client-local file path.



## WebSocket protocol

Every remote client uses `/rpc` with the required `sharprail.rpc.v1` WebSocket subprotocol.
Binary protobuf envelopes carry call ids, method names, request metadata, remaining deadline budgets,
payload chunks, half-close, final status/error metadata, cancellation, receive credits and heartbeats.
`scripts/generate-rpc.py` generates typed clients and a closed host dispatch table from the interfaces;
there is no runtime proxy generation and no gRPC dependency or loopback forwarding.

Each connection allows at most 64 active calls and queues at most 64 outgoing frames. Payload chunks
are at most 32 KiB; envelopes are at most 48 KiB. Reassembled messages are limited to 144 MiB, with a
192 MiB connection budget for retained receive buffers and pending serialized sends. Each call grants
16 messages of receive credit in each direction. A consumer advances its stream to grant another;
the bounded window pipelines terminal chunks without a round trip per chunk, and a stalled stream never blocks the connection's receive pump. Protocol violations disconnect the peer.
Call cancellation and deadline expiry end only that call, preserve the connection and cancel host work;
mutations protected by the replay cache retain their established independent lifetime.

Ping/pong detects a silent connection within three heartbeat periods. A lost connection faults its
calls as Unavailable; subscriptions reconnect through their owners and mutations retain replay ids.
Domain error metadata survives the transport. The backing buffers belong to their call and are released
on consumption, completion or cancellation. Network clients and servers must both support this wire
protocol; the old HTTP/2 gRPC wire format is not accepted.

Client file transfers carry relative entry names, directory flags and raw byte arrays in a bounded
unary upload. An optional folder distinguishes workspace imports from private attachments; replies
return host paths. File actions also carry an optional expected workspace root. Older hosts refuse
the new upload operation without a protocol-version change.


The optional loopback browser approval bootstrap uses JSON `LocalBrowserApprovalReply` for a
temporary origin-bound credential. It is separate from typed WebSocket RPC and introduces no
new RPC operation or protocol-version requirement; hosts without app approval remain usable
with the existing manually supplied token. See Remote’s spec for the consent and validation boundary.

## Language queries

`LanguageAsync` carries its workspace explicitly in a typed request, independent of the adapter's
currently opened project. DTOs preserve draft identity/version, UTF-16 ranges, server state,
freshness and coverage. `DocumentReply.ReadOnly` preserves dependency navigation's read-only status.

Language replies carry server inventory rows (id, representative path, state, detail and managed version)
for management without starting processes; older hosts may return an empty inventory.

Language request field 11 carries an optional server id for path-free lifecycle operations.

Language request fields 12 and 13 carry diagnostic scope and selected paths. Reply field 11
carries per-file path, state, freshness and detail, preserving partial coverage across transports.

Host settings fields 15 and 16 carry language-server launcher configuration and LSP enablement; fields 12–14 retain tab and settling settings. Server-specific setting
changes and broadcasts retain the same behavior through local and remote adapters.

Host settings carry LSP enablement with an explicit true default, preserving false across
protobuf serialization and compatibility with settings that omit the field.

Language wire DTOs carry navigation offset/limit (default 200), total/truncated results and
location preview/body/truncation fields. Older callers retain the default page size.

Language RPC preserves workspace configuration requests and saved/active snapshots on replies
and server rows. Host state snapshots carry workspace language metadata, including remote clients.


IntelliJ terms review/explicit acknowledgement has a dedicated typed request/reply RPC, including expected artifact/EULA identities. It is not an operation on agent-exposed language requests.


Context consent uses a typed project RPC operation with reviewed artifact/text hashes and an acknowledgement timestamp. It is independent of terminal-authenticated MCP and preserves the same explicit consent boundary as direct clients. Older servers may return unimplemented, surfaced as unavailable.


LanguageReply field 16 carries project-root DTOs (root, manifest, server, kind, state, detail). Existing operation/offset/limit fields carry projects discovery without new RPC methods. Context status uses the existing consent operation without fetching terms or recording acceptance.


LanguageReply field 17 carries semantic-color capability; LanguageItemDto field 12 carries semantic modifier names. Both are additive with false/empty defaults and are mapped by direct and WebSocket adapters. Existing RPC operations and protocol version remain unchanged.

Air Context is a plugin (protocol 8): the Context RPC service, `SettingsReply` field 18 (its visibility) and `StateReply` field 19 (its enabled
workspaces) are retired and their numbers are not reused. Its operations are the plugin's own methods over the generic plugin call.
