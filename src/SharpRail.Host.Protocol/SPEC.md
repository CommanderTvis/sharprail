# Host wire contracts

Upstream: packages/contracts/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The client↔host wire spine for remote hosts: the single source of truth for the
code-first protobuf-net.Grpc service interfaces and their DTOs. It holds contracts
only, with no behavior; the one runtime constant beyond DTO shapes is the
`ProjectHeaders.Root` metadata key. Embedded (local) mode never touches this
project: local adapters call Core directly, with no serialization.

## Boundary

- Owns the wire: the four services `IWorkspaceRpc` (`WorkspaceContract.cs`),
  `IProjectRpc` (`ProjectContract.cs`), `IStateRpc` (`StateContract.cs`) and
  `ITerminalRpc` (`TerminalContract.cs`), their `[ProtoContract]` request/reply
  classes, and the metadata keys a call carries.
- Domain records belong in `SharpRail.Host.Abstractions`; DTOs here are their
  serialized projection. Client and Remote adapters map between the two, and both
  directions must stay consistent with the public abstraction. A DTO is never
  exposed to the UI.
- Allowed deps: protobuf-net and protobuf-net.Grpc attribute/`CallContext` types.
- Forbidden: any reference to Core, Remote, Client, the UI or Avalonia. Contracts
  describe host behavior and compatibility, never the launcher or deployment
  that supplies it.

## Contents

- Workspace: the host's startup workspace identity and its root entries.
- Project: open a project (resolving the workspace/project roots), list and
  read files, save a file with its original text for conflict detection, list
  specs, Git snapshot, commit list, per-file diff and diff sides, Git actions,
  open pull request lookup, pull request draft and opening, branch catalog (optional default-base fetch; local, per-remote rows, the
  default base and suggested new worktree path/branch), editor listing and
  open-in-editor. Git scope travels as a string (`all` when empty); the comparison
  branch is the review target. Commit listing is its own operation so the commit
  catalog never requires a full working-tree snapshot.
- Project file watching is a server stream with no deadline. `FileChangeReply` carries up to 100
  workspace-relative paths plus `Rescan` for startup registration, overflow or pathless invalidations.
- State: one complete `StateReply` snapshot (revision, settings, custom layout
  presets, open and recent projects, workspace labels, workspaces per project),
  `ChangeAsync` taking an ordered batch of `(Kind, Key, Value)` changes and
  returning the resulting snapshot, and a server-streamed `WatchAsync` of full
  snapshots. Custom presets are the only layout value on the wire; current and
  default presets, frames and geometry stay client-local.
- Terminal: one bidirectional `RunAsync` stream per attachment. The first input
  is an attach (session id, workspace root, client id, size, resume offset); the
  first output acknowledges it with `Created`, the replay bytes and the stream
  position. Later inputs carry data, resize or kill; later outputs carry data with
  positions and end with exactly one of exited (with code) or detached.
  `IsBusyAsync` and `CloseAsync` are unary.

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
- Failures use gRPC status codes, not a bespoke error-code enum. A code earns a
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
  (`x-sharprail-resume`) with every replayable call (`ReplayHeaders.IsReplayable`). gRPC has no
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

- Pushed invalidations beyond host state and the lifecycle stream: a worktree
  file-change nudge (`workspace.fsChanged`) and a host update notice.
- Project inspection (`repo`/`initable`/`missing`/`notDirectory`), `git init`,
  lazy "has specs" and a `GitDiffScope` of a single commit carried on file diffs
  as a typed value.
- A host HTTP endpoint for worktree files (relative Markdown images over remote).
- Resource metadata on file reads and diff sides is ported, with the bytes fetched over gRPC rather than
  the host's file/blob HTTP endpoints. Still open: a byte-only file read (invalid UTF-8 still fails as
  "binary"), and a streamed rather than single-message transfer.
