---
id: submodule-host-git
type: submodule-design
status: active
title: Git — runner, scopes, status and diffs
parent: module-host-core
---

# Git — runner, scopes, status and diffs

Upstream: packages/server/src/git/SPEC.md @ 12830b08

## Responsibility

Git plumbing for one workspace: the `git` runner, a worktree's changed files and per-file diff sides over
a diff scope, the commit catalog for the scope menu, the branch catalog for the Create workspace dialog,
and worktree listing. Exposed through `IProjectServices` (`GetGitAsync`, `ListCommitsAsync`,
`GetDiffAsync`, `GetDiffSidesAsync`, `ListBranchesAsync`, `ApplyGitActionAsync`).

## Boundary

- Owns: `GitRepository` (`RunAsync`, `SnapshotAsync`, `ListCommitsAsync`, `ComparisonBaseAsync`,
  `CommitRangeAsync`, `CommitDiffArgumentsAsync`, `ParseWorktrees`) and the scope handling in
  `ProjectServices`.
- Forbidden: UI types; interpreting a Git failure as "no changes".

## Runner

`RunAsync(root, ct, args)` starts `git` asynchronously with `--literal-pathspecs` and
`core.quotepath=false`, in the workspace root, with `LC_ALL=C` and `GIT_OPTIONAL_LOCKS=0` so background status refreshes never
hold `index.lock` against the user's own Git commands. It reads both streams to completion,
throws an `IOException` carrying Git's trimmed stderr and exit code on a nonzero exit, and kills the whole
process tree on cancellation. Semantic probes distinguish an expected exit (for example exit 1 from
`merge-base` or `symbolic-ref -q`) from any other failure by that exit code; nothing else is swallowed.
Every path argument is literal, never a pathspec, so a file named like pathspec magic or a glob matches
only itself. Revisions go after `--end-of-options` and before a trailing `--`, so neither an option-shaped
ref nor a ref that also names a path can be misread.

## Scopes

A scope is defined once, and the file list, counts and both diff sides use the same range:

- `all`: from `HEAD` (or the index on an unborn repository) to the working tree, plus untracked files.
  With a comparison target it measures from the merge base of the target and `HEAD`, so commits that
  exist only on the target never appear as local deletions; unrelated histories fall back to the resolved
  target, while an invalid ref remains an error.
- `uncommitted`: `HEAD` to the working tree plus untracked. Edits that cancel between index and working
  tree disappear here while remaining in `staged`.
- `staged`: `HEAD` to the index; untracked files never appear.
- `commit`: the selected commit against its first parent; a root commit shows its whole tree. Working
  edits and untracked files never enter. The id must be 4–64 lowercase hex characters before it reaches
  Git and must then resolve with `rev-parse --verify`; a commit that exists but is no longer reachable from
  the branch still shows its diff.
- `branch` and `working` serve diff reads only: the comparison baseline to the working tree, and index to
  working tree.

Untracked files count their whole content as added lines (skipping symbolic links, files over 8 MiB and
binary content). Line counts come from `--numstat`; binary rows keep zero counts.

## Commit and branch catalogs

- `ListCommitsAsync` lists at most 200 commits of `<target>..HEAD` (none without a target), newest first, one NUL-separated
  record per commit read at fixed arity, so no author or subject text can shift a field. Free text is
  sanitized for display the way the reference does. A range Git rejects with exit 128 (deleted target,
  unborn `HEAD`) degrades to an empty list so the other scopes stay available. Listing is independent of
  the working-tree snapshot and survives index failures.
- `ListBranchesAsync` returns local heads, remote-tracking branches grouped by their configured remote
  (longest matching remote name wins, because remote names may contain `/`; symbolic `HEAD` aliases are
  omitted), the default base and the suggested next workspace path and branch. The default base is
  `origin/HEAD` while `origin` exists, else the main worktree's current branch, else `HEAD`. With
  `fetchDefault` it first fetches a stale or missing remote default in the background of the dialog,
  logging rather than failing when the fetch does.
- Remote-ness is decided against `git remote`, never against the string's shape.

## Get right

- A failed read is an error, never "no changes". The Changes panel keeps its last list and offers Retry;
  a genuine non-repository folder (`rev-parse --git-dir` reporting "not a git repository") is the only
  case reported as `IsRepository = false`. Corrupt metadata and probe failures stay visible.
- A Git failure must not block opening accessible workspace files.
- Diff sides read blobs with `cat-file -p <rev>:./<path>`; only Git's path-absent diagnostics yield an
  empty side, so a broken read is never shown as an add or delete.
- The snapshot reports the live current branch (`symbolic-ref --short -q HEAD`, else `detached HEAD`),
  because a terminal checkout moves it out of band.
- Stale responses are rejected by the caller per workspace and scope; scope and target changes cancel
  the superseded read (`WorkspaceGit.cs` in the UI).
- Remote fetches run only for a ref whose remote is configured, pass the branch through
  `check-ref-format --branch` first and fetch `--no-tags` after `--end-of-options`, so a crafted ref cannot
  become a refspec. A failed fetch reports Git's own error.

## Not yet ported

- A wall-clock budget on every Git call (upstream 55 s) whose timeout message keeps what Git wrote and
  names only observed causes, with the child's process group killed on expiry and a hint about unloaded
  SSH keys only for network operations.
- `GIT_TERMINAL_PROMPT=0` and a detached session for Git children so a passphrase prompt fails fast
  instead of waiting on a terminal.
- A per-workspace review target (`diffBase`) persisted separately from creation provenance, and a
  `pinned` scope measuring an immutable commit against the working tree.
- An in-process ref-shape check (`check-ref-format` rules) at every door that accepts a ref, including a
  base read from the repository's own `HEAD`.
- Background prefetch reporting whether a remote-tracking ref moved, and a nudge to re-read workspaces
  whose comparison base it moved.
- Workspace diff-stat badges computed from the same branch-scope range.
