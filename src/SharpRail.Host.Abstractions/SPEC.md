---
id: module-host-abstractions
type: module-design
status: active
title: Host abstractions — the public host surface
parent: architecture
tags: [public-surface-checked]
---

# Host abstractions — the public host surface

Upstream: packages/shared/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/host/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/contracts/SPEC.md @ 4737df6d (CommanderTvis fork), the plugin roster and agent record

## Responsibility

The transport-independent contract between the workbench and a host: service interfaces and the domain
records they exchange. The UI programs only against these types, so an embedded host and a remote one are
interchangeable adapter choices. Wire DTOs live in `SharpRail.Host.Protocol`, implementations in
`SharpRail.Host.Core`, adapters in `SharpRail.Host.Client` and `SharpRail.Host.Remote`.

## Boundary

- Owns: `IWorkspaceHost` (`IWorkspaceHost.cs`), `IProjectServices`
  (`ProjectServices.cs`), `IHostStateService` with `HostState`, `HostSettings`, `LayoutPreset` and
  `HostStateChange` (`HostState.cs`), the workspace registry records `WorkspaceRecord`, `WorkspaceAction`,
  `WorkspaceCatalog` and `LifecycleEvent` (`WorkspaceRegistry.cs`), `ITerminalService` / `ITerminalSession` with
  `TerminalAttachRequest` and `TerminalGrid` (`TerminalServices.cs`), `ITerminalCatalogService` with
  `TerminalTab` and `TerminalCatalog` (`TerminalCatalog.cs`), and `FileLimits`.
- Allowed deps: the .NET base library and `SharpRail.Plugins.Api`, whose roster, agent-record and project
  records `HostState` and `IPluginService` share with plugins rather than mirror.
- Forbidden: serialization attributes, gRPC, Avalonia, filesystem or process access, and any AI or agent
  behaviour. A terminal's agent record is data a plugin writes; the host only stores and broadcasts it.

## Surfaces

- `IProjectServices` is one client's project session: open a project root, list and read files, save
  with a conflict check, list specs, search the workspace, Git snapshot/commits/diffs/diff sides, Git actions
  (stage, unstage, init, create and remove worktree, delete branch, fetch), branch catalog, open pull request lookup, pull request draft and opening
  (`PrResult.Action` is `created`, `updated`, `pushed`, `compare` or `authFailed`), and editor
  detection/launch. Every call is
  cancellable. Paths are workspace-relative.
- `ListWorkspacesAsync` and `ApplyWorkspaceActionAsync` name their project or workspace explicitly rather
  than using the session's root, so a client can list and change any open project's workspaces. Kinds are
  `default`, `managed` and `external`; a record's id never changes. Results also reach every client
  through `HostState.Workspaces`.
- `IHostStateService.WatchLifecycleAsync` pushes `LifecycleEvent`s on a `project` channel (`opened`,
  `closed`) and a `workspace` channel (`created` and `updated` carrying the record, `removed` its id).
  It replays nothing; a client rehydrates from the state snapshot.
- `WatchFilesAsync` subscribes to the current workspace and yields bounded `FileChange` invalidations.
  Its first frame requests a rescan; later frames name paths or request a full rescan when paths are
  unavailable or capped. Cancelling the subscription releases its share of the workspace's watchers.
  `PrewarmWorkspaceAsync` is a hint to start them for a workspace about to be opened.
- `InspectProjectPathAsync` classifies a host path as `ProjectPathKind` (`Repository`, `Initable`,
  `Missing`, `NotDirectory`) without opening it. It and `OpenProjectAsync` take `~` as the host user's
  home and refuse a relative path.
- `GetSpecGraphAsync` returns `SpecGraph`: the specs, one `SpecEdge` per link (kinds in `SpecLinks`) and
  the validation lists (dangling links, duplicate ids, parent cycles). `HasDurableSpecsAsync` answers
  whether the workspace holds any spec that is not a task spec, and never fails. `SpecDocument.Status`
  carries the frontmatter status.
- A read of a byte-only file returns a `FileDocument` with empty text and `Info.IsText` false; a picture
  the client draws arrives in `ImageData`.
- `RevertChangeAsync` and `UndoChangeAsync` write the worktree on the client's behalf: the client sends
  a scope, a `RevertTarget` (a file, or a line span per side) and the SHA-256 of each side it saw
  (`ChangeExpectation`), never bytes. They return a `ChangeReceipt`, which is also the undo token, or
  throw `ChangeException` with a `ChangeFailure` the client branches on. `HostProtocol.ChangeWritePath`
  is the version that introduced them.
- `DiffSides` carries `ContentMetadata` per side and the revision each was read at; a byte-only side has
  empty text. `ReadContentBytesAsync` returns `ContentBytes` (raw bytes plus metadata) for the working
  tree (null), the index (empty) or a commit id. `FileDocument.Info` carries the same metadata on reads.
  `ContentMetadata.IsActive` marks HTML, XHTML and SVG, which a client must treat as inert.
- `HostException` is a failure a client reacts to specifically; its `HostErrorCode` is `UnknownCommit`
  (a commit named by a scope or revision no longer resolves), `NotGit` or `AlreadyOpen` (the two
  project-open refusals). It derives from `IOException`, so a caller that handles none of them sees an
  ordinary failure. Both adapters deliver the same type and message.
- `IHostStateService` is the shared state of one host. `GetStateAsync` reads, `ChangeAsync` applies a
  batch atomically and returns the published snapshot, and `WatchAsync` yields the current snapshot and
  then every later one. Snapshots are complete and carry a `Revision`, so a client that missed events
  rehydrates from the next. `HostState.ProjectRecords` holds one `ProjectRecord` (id, path, slug, last
  opened) per open or recent project.
- `ITerminalService` owns sessions; `ITerminalSession` is one client's attachment. The client names the
  session and identifies itself; `Offset = -1` asks for a fresh replay, a non-negative offset resumes.
  Disposing an attachment detaches without ending the shell; `CloseAsync` is the only way a client ends
  one.
- `IHostStateService.GetHandshakeAsync` returns a `HostHandshake` (protocol version and build version).
  `HostProtocol.Current` rises when a host operation or message changes in a way an older client must
  know about; features record the version that introduced them beside it (`ChangeWritePath` 2,
  `RequestReplay` 3). Version 0 means a host that predates the handshake.
- `IPluginService` is the host's plugin runtime: the roster (also on every `HostState` snapshot), rescan and
  retry, the generic plugin call and channel subscription, and reads of a plugin directory's files. Payloads
  are typed objects in process and JSON elements after the wire; plugin settings namespaces and extra plugin
  roots are host state, changed through `HostStateChange.PluginSettings`/`PluginEnabled`/`PluginPaths`. See
  `src/SharpRail.Plugins.Api/SPEC.md`.
- `IWorkspaceHost` is the minimal probe of the host's root workspace: its identity and top-level entries.

- `IProjectServices.GetDiffStatsAsync` has a default body answering null, so a test double or an older
  implementation simply offers no badge.

## Public surface

The public top-level types of the assembly, held to it in both directions by the conformance checks
(a type added here is added to this list in the same change):

`BranchCatalog`, `ChangeException`, `ChangeExpectation`, `ChangeFailure`, `ChangeIdentity`,
`ChangeReceipt`, `ContentBytes`, `ContentMetadata`, `DiffSides`, `DiffStats`, `EditorInfo`,
`ExistingWorktree`, `WorkspaceFileChanges`, `FileAction`, `FileDocument`, `FileEntry`, `FileLimits`, `FileSaveRequest`,
`GitAction`, `GitChange`, `GitCommit`, `GitSnapshot`, `HostErrorCode`, `HostException`,
`HostHandshake`, `HostProtocol`, `HostSettings`, `HostState`, `HostStateChange`,
`IPluginService`, `PluginCallRequest`, `PluginSubscription`, `SearchHit`, `SearchHits`,
`TerminalAgent`, `TerminalPrefill`, `TerminalTitle`, `IHostStateService`, `IProjectServices`, `ITerminalCatalogService`, `ITerminalService`,
`ITerminalSession`, `IWorkspaceHost`, `LayoutPreset`, `LifecycleEvent`, `LineSpan`, `OpenReview`,
`PrDraft`, `PrRequest`, `PrResult`, `ProjectFile`, `ProjectPathKind`, `ProjectRecord`,
`RemoteBranch`, `RevertTarget`, `SpecDocument`, `SpecDuplicate`, `SpecEdge`, `SpecGraph`,
`SpecLinks`, `TerminalAttachRequest`, `TerminalCatalog`, `TerminalGrid`, `TerminalTab`,
`WorkspaceAction`, `WorkspaceCatalog`, `WorkspaceInfo`, `WorkspaceKinds`, `WorkspaceRecord`,
`WorktreeInfo`.

## Invariants

- Hydrate, then stream: every piece of shared domain state is readable in full and observable as
  complete snapshots; no surface relies on a client having witnessed an event.
- One definition per concept: a diff scope is a string understood identically by status, commit listing
  and diff reads (`all`, `uncommitted`, `staged`, `commit`, `pinned`, plus `branch` and `working` for diff reads).
- A record added here gets a Protocol DTO and both adapters in the same change, with local/remote
  parity verified in `tests/SharpRail.Checks`.
- Size limits that both sides must agree on (`FileLimits`) are defined once here.

## Not yet ported

- Workspace activity and explicit settled/active overrides with timestamps, settle/unsettle actions,
  and the nullable host-wide idle-day setting. These are host facts; the client derives the Settled
  shelf. No partition boolean belongs in `WorkspaceRecord` (see
  [Workspaces.SPEC.md](../SharpRail.Host.Core/Workspaces.SPEC.md#not-yet-ported)).
- A partial interface keeps `IProjectServices` one contract across files; the operations added since
  `HostProtocol.Current` was last raised (path inspection, pre-warm, spec graph, durable-spec query) have
  no version constant yet, so a client cannot gate them against an older host.
