---
id: submodule-host-workspaces
type: submodule-design
status: active
title: Workspaces — Git worktrees
parent: module-host-core
depends-on: [submodule-host-git, submodule-host-state]
---

# Workspaces — Git worktrees

Upstream: packages/server/src/workspaces/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

A workspace is a Git worktree on its own branch — the anchor for files, changes and terminals. The host
keeps a registry of them (`WorkspaceRecord`): a stable id, the project root, a kind, the worktree path, its
branch and the base it is measured from. Its display label is decoupled from its branch: the label lives
in host state (`HostState.WorkspaceLabels`), the branch in Git, and renaming one never touches the other.

Two kinds are user-owned — SharpRail uses their folder but never renames, removes or reclaims them. Every
project has exactly one `default` workspace whose root is the project folder itself (Git's main working
tree). A worktree the user attaches is recorded in place as `external`. Only a `managed` worktree, which
SharpRail created, may be removed by it.

## Boundary

- Owns: opening a project or workspace root (`ProjectServices.OpenProjectAsync`), the registry
  (`WorkspaceRegistry.cs`: `ListWorkspacesAsync`, `ApplyWorkspaceActionAsync` with `create`, `attach`,
  `forget`, `remove`, `reclaim`, `reserve-terminal` and `reveal`), the older worktree Git actions
  (`ApplyGitActionAsync` with `create-worktree` / `remove-worktree`, which keep the registry in step) and
  suggesting the next workspace (`ListBranchesAsync`).
- The records are host state: `HostStateStore.ChangeWorkspaces` saves them in `state.json`, publishes the
  snapshot and then the lifecycle events of the difference ([HostState.SPEC.md](HostState.SPEC.md)).
- Forbidden: moving a worktree directory, renaming a branch, mutating the Default workspace's checkout,
  and any Git or filesystem write into an external worktree.

## Behavior

- Opening a path resolves it to its repository top level; for a linked worktree the project root is the
  main worktree from `git worktree list`. A plain folder opens as itself. `WorkspaceInfo.ProjectRoot`
  carries the project identity so every window groups the workspace under the same project.
- Listing a project ensures its Default workspace (find or create, collapsing duplicates) and returns the
  project's records only for a project the host already knows through its project list, recents or registry.
  Standalone sessions without a shared host store may list any project folder. Listing returns the
  project's records with the Default first. It is a deliberate query-with-write: any caller heals a
  registry that predates the project. The first time a project is listed, the branch-backed worktrees Git
  already has are adopted so an upgraded rail keeps its rows: those under `<main worktree>-worktrees` as
  `managed`, any other as `external`. Later worktrees wait to be attached.
- Listing also re-reads folder truth: the Default workspace's branch and the repository's default base,
  every other record's branch, and membership — a record whose worktree Git no longer lists on disk is
  dropped. A list that finds no drift saves and publishes nothing. When Git cannot list worktrees, no
  record is dropped.
- Drift is not only found at list time. Every Git snapshot of a checkout (`GetGitAsync`) compares its
  branch with the record and, when a terminal checkout moved it, publishes the new branch (and, for the
  Default workspace, the default base) to every client. An unreadable checkout is never recorded as a
  detached HEAD.
- The same list call returns the worktrees that could still be attached: every linked worktree on disk
  that no record or open project represents; a detached one is listed without a branch.
- Attach revalidates the path against Git's worktree list, refuses a detached HEAD, a path that is an
  open project and one recorded under another project, and is idempotent for the same project. It records
  an `external` workspace measured against the repository default and runs no Git or checkout mutation.
- Create cuts a new branch with `worktree add --no-track -b <branch> -- <path> <base>`: a remote base
  must not become the branch's upstream, which would aim the workspace's pushes at the base. The branch
  passes `check-ref-format --branch` and the base must resolve to a commit before Git mutates anything.
- A remote base (the longest configured remote that prefixes the ref) is fetched first and handed to Git
  as the fully qualified `refs/remotes/<base>`, since the shorthand could also resolve a local branch of
  that name. The guard is the ref, re-checked after the fetch whatever its outcome: a failed fetch does
  not fail the create once the ref is there (a concurrent prefetch may have landed it), and a fetch that
  never lands it fails the create — with Git's own message when the fetch failed, or naming the remote's
  refspec when it succeeded without mapping the branch.
- A name given at creation is the display label (whitespace collapsed, 60 characters) and derives the
  branch as a lower-case kebab slug, made unique against local branches and worktree directories with a
  numeric suffix; without a name the workspace takes the next `workspace-N`. The location is
  `<main worktree>-worktrees/<branch>`. Removal keeps the branch, so both must be free. The record's base
  is creation provenance — the picked ref, or the project's current branch when none was picked.
- A created worktree is seeded with a scratch directory, `.sharprail/context`, holding a self-ignoring
  `.gitignore` so it has no Git footprint. Seeding refuses a missing workspace root and symlinked path
  components, and never overwrites an existing ignore file. User-owned workspaces are not seeded.
- Every record first written by create, attach or the Default ensure carries `InitialTerminalPending`;
  `reserve-terminal` clears it once, persists and publishes. Adopted records and ones that predate the
  marker count as complete, so an upgrade cannot bring back a terminal the user closed.
- Forget drops the record of a managed or external workspace and keeps its worktree; the Default
  workspace refuses. Remove runs `git worktree remove` for a managed worktree, which refuses a dirty or
  locked one, and drops the record only when Git succeeded. Reclaim drops the record first and then runs
  `worktree remove --force`, falling back to deleting the directory and pruning; it keeps the branch.
  Remove and reclaim refuse both user-owned kinds and any record whose path is the project folder.
- `remove-worktree` additionally refuses the main, the active and a locked worktree, and an external
  record. The UI asks for explicit confirmation first; removing the active workspace in a window returns
  it to the previously selected one.
- A record that leaves the registry loses its label in the same publish, so every window's rail converges
  on the broadcast rather than on per-window optimism.
- Labels are display-only and may repeat; the branch beneath disambiguates. An empty label, or one equal
  to the directory name, restores the directory name. The Default workspace's label is fixed.
- `OpenInEditorAsync` launches a detected editor only for a worktree of this project or a registered
  workspace; `reveal` opens a registered workspace's folder in the file manager of the host's machine.

## Get right

- Persist-then-publish: host state is saved before the snapshot is broadcast, and lifecycle events
  follow the snapshot that carries them.
- Work that reads worktrees from Git and then rewrites the registry (list, create from `worktree add`
  onwards, attach, remove, reclaim) runs one at a time per host (`HostStateStore.LockWorkspacesAsync`),
  across every session: a list that read Git before a create must not drop the record that create wrote.
  A list also waits for its own session's Git actions, so folder truth read before an `init` is never
  written after it. A slow base fetch stays outside the gate.
- A missing restored workspace falls back to its Project Home, a missing project to the Welcome, and a
  workspace removed elsewhere returns a window showing it to Project Home with a notice.

## Not yet ported

- Host-owned settled lifecycle facts: last real activity, an explicit settled/active override and its
  timestamp. The client derives the live rows and Settled shelf; the host must not persist the partition.
  Creation and attachment stamp activity; accepted terminal input and an observed HEAD SHA change clear
  either override. Selecting a workspace, reading files or viewing output never count as activity.
  Settle and unsettle save and publish the updated record without touching the worktree, and reject the
  Default workspace; removal remains a separate teardown operation.
- Activity writes coalesce to at most one per minute only when skipping a stamp cannot alter the
  partition; clearing an override must always persist. Watcher admission seeds the HEAD baseline once
  per host lifetime, and recreation compares against the retained baseline to detect work while
  unwatched. Backfill records with no activity stamp once from the worktree's `.git` gitfile mtime,
  falling back to now (including the Default workspace); it must preserve explicit overrides.
- Renaming a managed workspace's branch from an agent-supplied slug; labels are the only rename.
- A host terminal catalog that consumes `InitialTerminalPending`: the first terminal is still reserved in
  each window's layout, and the marker is only kept and cleared by the host.
- Seeding the scratch directory of a user-owned workspace when a session starts there.
- Prefetching a picked non-default base from the creation dialog, off the create's critical path.
