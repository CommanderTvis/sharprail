---
id: module-host-abstractions
type: module-design
status: active
title: Host abstractions — the public host surface
parent: architecture
---

# Host abstractions — the public host surface

Upstream: packages/shared/SPEC.md @ c44534ea
Upstream: packages/server/src/host/SPEC.md @ c44534ea

## Responsibility

The transport-independent contract between the workbench and a host: service interfaces and the domain
records they exchange. The UI programs only against these types, so an embedded host and a remote one are
interchangeable adapter choices. Wire DTOs live in `SharpRail.Host.Protocol`, implementations in
`SharpRail.Host.Core`, adapters in `SharpRail.Host.Client` and `SharpRail.Host.Remote`.

## Boundary

- Owns: `IWorkspaceHost` (`IWorkspaceHost.cs`), `IProjectServices`
  (`ProjectServices.cs`), `IHostStateService` with `HostState`, `HostSettings`, `LayoutPreset` and
  `HostStateChange` (`HostState.cs`), `ITerminalService` / `ITerminalSession` with
  `TerminalAttachRequest` (`TerminalServices.cs`), and `FileLimits`.
- Allowed deps: the .NET base library only.
- Forbidden: serialization attributes, gRPC, Avalonia, filesystem or process access, and any AI or agent
  concept.

## Surfaces

- `IProjectServices` is one client's project session: open a project root, list and read files, save
  with a conflict check, list specs, search the workspace, Git snapshot/commits/diffs/diff sides, Git actions
  (stage, unstage, init, create and remove worktree, delete branch, fetch), branch catalog, and editor
  detection/launch. Every call is
  cancellable. Paths are workspace-relative.
- `IHostStateService` is the shared state of one host. `GetStateAsync` reads, `ChangeAsync` applies a
  batch atomically and returns the published snapshot, and `WatchAsync` yields the current snapshot and
  then every later one. Snapshots are complete and carry a `Revision`, so a client that missed events
  rehydrates from the next.
- `ITerminalService` owns sessions; `ITerminalSession` is one client's attachment. The client names the
  session and identifies itself; `Offset = -1` asks for a fresh replay, a non-negative offset resumes.
  Disposing an attachment detaches without ending the shell; `CloseAsync` is the only way a client ends
  one.
- `IWorkspaceHost` is the minimal probe of the host's root workspace: its identity and top-level entries.

## Invariants

- Hydrate, then stream: every piece of shared domain state is readable in full and observable as
  complete snapshots; no surface relies on a client having witnessed an event.
- One definition per concept: a diff scope is a string understood identically by status, commit listing
  and diff reads (`all`, `uncommitted`, `staged`, `commit`, plus `branch` and `working` for diff reads).
- A record added here gets a Protocol DTO and both adapters in the same change, with local/remote
  parity verified in `tests/SharpRail.Checks`.
- Size limits that both sides must agree on (`FileLimits`) are defined once here.

## Not yet ported

- A protocol version exchanged on connect so a client can detect host drift.
- Named error codes that survive transport, so a client reacts to a specific failure rather than text.
- Workspace records with stable ids, kinds and lifecycle events, a workspace diff-base setter, a
  change-notification stream, and a terminal catalog with reservation separate from attachment.
- Change revert and undo operations, and content classification (media type, hash) on file and diff-side
  reads, including a byte read of a path at one commit.
