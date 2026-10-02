# Host wire contracts

Upstream: packages/contracts/SPEC.md @ be804a56

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
  branch catalog (optional default-base fetch; local, per-remote rows, the
  default base and suggested new worktree path/branch), editor listing and
  open-in-editor. Git scope travels as a string (`all` when empty); the comparison
  branch is the review target. Commit listing is its own operation so the commit
  catalog never requires a full working-tree snapshot.
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
- Message size limits come from `FileLimits` in Abstractions (read and save
  messages sized from the editable-file limit), so both ends agree on one number.
- Credentials never ride a DTO: authentication is the `authorization` metadata,
  owned by the transport.

## Invariants

- Adding a field means a new `ProtoMember` number; numbers are never reused or
  renumbered, and defaults must read as the old behavior for an older peer.
- Wire data is untrusted: the host validates roots, paths and state changes; a
  client validates nothing it could not also receive from a peer of another
  version.

## Not yet ported

- A protocol version exchanged at connect, with per-feature introduction versions
  so a newer client hides actions an older host cannot serve.
- Request-id deduplication on reconnect (replay under the same id, host-cached
  results, ack/resume frames).
- Pushed invalidations beyond host state: project/workspace lifecycle as separate
  channels, a worktree file-change nudge (`workspace.fsChanged`), and a host
  update notice.
- Project inspection (`repo`/`initable`/`missing`/`notDirectory`), `git init`,
  lazy "has specs", attaching existing worktrees, persisted diff-base re-pointing
  and a `GitDiffScope` of a single commit carried on file diffs as a typed value.
- A host HTTP endpoint for worktree files (relative Markdown images over remote).
- A named failure for an unresolvable scope so the client resets it rather than
  showing an error.
