---
id: submodule-host-workspaces
type: submodule-design
status: active
title: Workspaces — Git worktrees
parent: module-host-core
depends-on: [submodule-host-git, submodule-host-state]
---

# Workspaces — Git worktrees

Upstream: packages/server/src/workspaces/SPEC.md @ be804a56

## Responsibility

A workspace is a Git worktree on its own branch — the anchor for files, changes and terminals. Its
identity is its absolute root path. Its display label is decoupled from its branch: the label lives in
host state (`HostState.WorkspaceLabels`), the branch in Git, and renaming one never touches the other.

Every project has exactly one Default workspace whose root is the project folder itself (Git's main
working tree). It is user-owned: SharpRail uses it as a working directory but never renames its branch,
removes it or writes into it on its own.

## Boundary

- Owns: opening a project or workspace root (`ProjectServices.OpenProjectAsync`), creating and removing
  worktrees (`ApplyGitActionAsync` with `create-worktree` / `remove-worktree`), suggesting the next
  workspace (`ListBranchesAsync`), and publishing a project's worktree list into host state after either
  mutation (`HostStateStore.PublishWorkspaces`).
- Forbidden: moving a worktree directory, renaming a branch, or mutating the Default workspace's
  checkout.

## Behavior

- Opening a path resolves it to its repository top level; for a linked worktree the project root is the
  main worktree from `git worktree list`. A plain folder opens as itself. `WorkspaceInfo.ProjectRoot`
  carries the project identity so every window groups the workspace under the same project.
- Create cuts a new branch with `worktree add -b <branch> -- <path> <base>` from a base chosen in a
  searchable list grouped by Local and each remote. The branch passes `check-ref-format --branch` and the
  base must resolve to a commit before Git mutates anything. A remote base is fetched first; a fetch that
  fails fails the create with Git's own message. A repository whose `HEAD` is unborn is refused first, by
  name ("This repository has no commits yet…"), instead of surfacing `invalid reference: HEAD`.
- The suggested location is `<main worktree>-worktrees/workspace-N` with branch `workspace-N`, where N is
  the first number whose directory and local branch are both free — removal keeps the branch, so both
  must be checked.
- Remove refuses the main worktree, the active worktree and a locked worktree before calling
  `git worktree remove`, which in turn refuses a dirty one. The UI asks for explicit confirmation first;
  removing the active workspace in a window returns it to the previously selected one.
- After a create or remove, the complete worktree list for the project is published through host state,
  so every window's rail converges on the broadcast rather than on per-window optimism. A removed
  workspace's label is dropped with it.
- Labels are display-only and may repeat; the branch beneath disambiguates. An empty label, or one equal
  to the directory name, restores the directory name. The Default workspace's label is fixed.
- `OpenInEditorAsync` launches a detected editor only for a path that is one of this project's
  worktrees.

## Get right

- Persist-then-publish: host state is saved before the snapshot is broadcast.
- The workspace list published by the host is not persisted; worktree membership is always re-read from
  Git.
- A missing restored workspace falls back to its Project Home, a missing project to the Welcome, and a
  workspace removed elsewhere returns a window showing it to Project Home with a notice.

## Not yet ported

- A persisted workspace registry with stable ids, kinds (`default`, `managed`, `external`) and
  lifecycle events (`created` / `updated` / `removed`) independent of Git's worktree list.
- Attaching an existing worktree in place as a user-owned external workspace, which may be forgotten but
  is never mutated.
- Human-readable names given at creation that derive a kebab branch, with uniqueness against refs and
  worktree directories. A later rename changes only the display label and never moves the branch.
- Worktree creation with `--no-track`, a fully qualified `refs/remotes/<base>` handed to `worktree add`,
  and a re-check of that ref before failing when a concurrent prefetch may have landed it.
- A durable pending marker that reserves the first terminal on the host at creation, instead of in the
  window's layout.
- Folder-truth refresh of the Default workspace's branch and default base when a terminal checkout moves
  it, published to every client.
- Forced reclaim (`worktree remove --force`, then remove and prune) for an archived managed worktree.
- A gitignored per-workspace scratch directory.
