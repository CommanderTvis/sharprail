---
id: module-host-abstractions
type: module-design
status: active
title: Host abstractions — the public host surface
parent: architecture
---

# Host abstractions — the public host surface

Upstream: packages/shared/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/host/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/contracts/SPEC.md @ 4737df6d (CommanderTvis fork), the plugin roster and agent record

## Responsibility

`HostSettings.VerticalCenterTabs` and `VerticalTabsInProjects` share the tab-strip mode between desktop,
browser and other clients. Nullable fields distinguish older hosts, which leave local preferences intact.
The corresponding setting keys are `vertical-tabs` and `tabs-in-projects`; pane arrangement and strip width
are client-local. Local hosts seed missing values from desktop preferences, otherwise default to false.

`HostSettings.CustomHighlighting` carries custom TextMate grammars and filename
patterns as a JSON array. `HostStateChange.Setting("custom-highlighting", json)`
replaces that collection through the host; clients never write the state file directly.
The UI owns grammar semantics; Core validates the bounded transport/persistence shape.

The transport-independent contract between the workbench and a host: service interfaces and the domain
records they exchange. The UI programs only against these types, so an embedded host and a remote one are
interchangeable adapter choices. Wire DTOs live in `SharpRail.Host.Protocol`, implementations in
`SharpRail.Host.Core`, adapters in `SharpRail.Host.Client` and `SharpRail.Host.Remote`.

## Boundary

- Owns the service contracts and domain records for workspace identity, project operations,
  shared state, workspace lifecycle, terminal sessions and catalogs, and agreed file limits.
- Allowed deps: the .NET base library and `SharpRail.Plugins.Api`, whose roster, agent-record and project
  records `HostState` and `IPluginService` share with plugins rather than mirror.
- Forbidden: serialization attributes, WebSocket RPC, Avalonia, filesystem or process access, and any AI or agent
  behaviour. A terminal's agent record is data a plugin writes; the host only stores and broadcasts it.

## Surfaces

- `IProjectServices` is one client's project session: open a project root, list and read files, save
  with a conflict check, list specs, search the workspace, Git snapshot/commits/diffs/diff sides, Git actions
  (stage, unstage, init, create and remove worktree, delete branch, fetch), branch catalog, open pull request lookup, pull request draft and opening
  (`PrResult.Action` is `created`, `updated`, `pushed`, `compare` or `authFailed`), and editor
  detection/launch. Every call is
  cancellable. Paths are workspace-relative.
- `ImportFilesAsync` carries a pinned workspace, a destination folder (null for private prompt attachments)
  and relative file/directory entries; it returns absolute host paths. See [host files](../SharpRail.Host.Core/Files.SPEC.md).
  `FileAction.WorkspaceRoot` optionally pins a move/copy to its originating workspace.
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
- The core file-type icon setting is enabled by default and independent of the plugin roster. Protocol version 7 (`HostProtocol.FileTypeIcons`) introduces its shared toggle; older hosts do not accept that setting.
- `IHostStateService` is the shared state of one host. `GetStateAsync` reads, `ChangeAsync` applies a
  batch atomically and returns the published snapshot, and `WatchAsync` yields the current snapshot and
  then every later one. Snapshots are complete and carry a `Revision`, so a client that missed events
  rehydrates from the next. `HostState.ProjectRecords` holds one `ProjectRecord` (id, path, slug, last
  opened) per open or recent project.
- `ITerminalService` owns sessions; `ITerminalSession` is one client's attachment. The client names the
  session and identifies itself; `Offset = -1` asks for a fresh replay, a non-negative offset resumes.
  Disposing an attachment detaches without ending the shell; `CloseAsync` is the only way a client ends
  one. File-link lookup resolves a hovered logical line on the session's host; `TerminalFileLink`
  carries its UTF-16 span, existing absolute target, file kind and one-based source coordinates.
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

## API validation

The Public API analyzer checks public declarations against the committed `PublicAPI.Shipped.txt`
and `PublicAPI.Unshipped.txt` baselines during compilation. Added, removed or changed signatures
require a deliberate baseline update. This spec owns behavior and transport invariants, not a
second list of declarations.

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

Prompt attachments use `SavePromptImageAsync`: bounded PNG bytes are saved under the host state's
clipboard directory, returning an absolute host path. Local calls run off the dispatcher; remote calls
carry the bytes and return the same host path, never a client-local file path.

Project services expose host-side GitHub authentication status. Terminal services accept a bounded PNG
clipboard upload for a session and return its host-visible path; unsupported hosts report unavailability.

## Language queries

`LanguageRequest` carries an explicit workspace, document identity/version and optional editor draft;
positions are zero-based UTF-16. `LanguageResult` carries server state, navigation locations, semantic
tokens and diagnostics with explicit freshness and coverage. `FileDocument.ReadOnly` marks exposed
language-server dependency files. Hosts without language support return unavailable rather than
pretending an empty diagnostic result is current.

The `servers` operation returns `LanguageServerState` rows without starting or provisioning any server.
Rows include the representative source path, state, details and managed version for workspace controls.

`LanguageRequest.Server` optionally selects a catalog server for path-free status, retry or stop.
Inventory rows with an empty path mean no source file was detected.

Diagnostic requests accept `Scope` (`files` or `workspace`) and `Paths` for an explicit file set.
`LanguageFileResult` rows in `LanguageResult.Files` report checked/pending/skipped/error state,
freshness and detail per selected file. Empty or partial coverage never establishes a clean scan.

`HostSettings.LanguageServers` carries host-wide launcher configuration as JSON.
`LanguageServerOptions` describes next-start enablement, managed preference, command and arguments.
The host-wide `catalog` language operation requires no workspace and starts no processes.

Host settings include host-wide LSP enablement, defaulting to enabled. Disabling it stops
active language sessions while preserving per-server configuration.

Language catalog server details identify their default implementation/provider; workspace inventory details continue to describe runtime state.

Language requests carry navigation offset/limit. Results carry total/truncated pagination,
and locations may carry bounded source previews, selected symbol bodies and per-item truncation.
Hover and symbolBody are read-only operations; body ranges use zero-based UTF-16 coordinates.

Language requests carry optional workspace configuration for save/restart. Results and server
rows distinguish saved configuration from the active process snapshot. Workspace language options
include start choices, server arguments, initialization and workspace settings, plus clangd build
configuration. Host state exposes each workspace's independent configuration map.


The language request operation logs returns bounded per-workspace server output in LanguageResult.Detail with Truncated indicating omitted history; it requires an explicit server id and never starts a process.


The dedicated IntelliJ terms-review/acknowledgement operation is a user-interface host contract, separate from agent language operations. Review can prepare the managed distribution on the host without execution. It returns exact terms, artifact/version/digest identity and recorded status; acknowledgement is not entitlement.

Language source retrieval reuses LanguageRequest/LanguageResult without a wire-shape change.
Path may be an exact virtual URI returned by the selected workspace session; Server identifies its
source provider. Source Offset and Total count UTF-16 characters, Locations contains one bounded
Body and Truncated indicates continuation. Symbol-selected navigation uses Query as an exact
qualified outline name; ambiguous results retain candidates rather than guessing a cursor.


ContextConsentAsync is a user-interface-only review/accept-and-download contract. Results carry the host-selected artifact identity, reviewed text/hash and acknowledgement timestamp. It grants no indexing enablement or CLI execution; see the host core Context consent boundary.


The read-only projects language operation returns LanguageProject roots with manifest, provider, kind, detected state and provenance. Pagination uses existing total/truncated fields. Detected roots do not assert imported module membership. Context consent also supports local-only status, independent of legal acknowledgement.


ContextRequest/ContextResult carry workspace-scoped lifecycle/search operations and bounded output. Enable/disable/login are user controls; agent tools expose only search/status/index. HostState.ContextWorkspaces persists explicit cloud enablement, independently of language configuration and without inheritance. Credentials and user-control commands never enter shared state.


LanguageProject directory metadata records its provenance: detected build definitions carry a
manifest, while provider-reported directory roles may have no build file. Consumers preserve
that distinction and do not infer package ownership or complete imports from either form.

Language results advertise full semantic-color capability through SemanticColoring. Tokens preserve space-separated modifier names from the server legend; empty defaults keep providers without semantic tokens compatible. The editor can distinguish lexical-only and semantic-capable running providers without retrieving tokens first.

AirContextHidden is a host-shared presentation preference; it does not change
workspace Context enablement, consent or process lifetime. Its wire field is additive.
