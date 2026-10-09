---
id: submodule-host-git
type: submodule-design
status: active
title: Git — runner, scopes, status and diffs
parent: module-host-core
---

# Git — runner, scopes, status and diffs

Upstream: packages/server/src/git/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/changes/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

Git plumbing for one workspace: the `git` runner, a worktree's changed files and per-file diff sides over
a diff scope, the commit catalog for the scope menu, the branch catalog for the Create workspace dialog,
and worktree listing. Exposed through `IProjectServices` (`GetGitAsync`, `ListCommitsAsync`,
`GetDiffAsync`, `GetDiffSidesAsync`, `ListBranchesAsync`, `ApplyGitActionAsync`).

## Boundary

- Owns: `GitRefs` (the ref-shape check), `GitRepository` (`RunAsync`, `SnapshotAsync`, `ListCommitsAsync`, `ComparisonBaseAsync`,
  `CommitRangeAsync`, `CommitDiffArgumentsAsync`, `ParseWorktrees`) and the scope handling in
  `ProjectServices`.
- Forbidden: UI types; interpreting a Git failure as "no changes".

## Runner

`RunAsync(root, ct, args)` (and `RunBytesAsync` for undecoded output) starts `git` through the bounded
child runner ([Terminals.SPEC.md](Terminals.SPEC.md)) with `--literal-pathspecs` and
`core.quotepath=false`, in the workspace root, with `LC_ALL=C` and `GIT_OPTIONAL_LOCKS=0` so background status refreshes never
hold `index.lock` against the user's own Git commands. Every call also runs with `GIT_TERMINAL_PROMPT=0`
in a session of its own: with no terminal to ask on, a credential or passphrase prompt fails at once. It
throws an `IOException` carrying Git's trimmed stderr (bounded to 2,000 characters, head and tail kept)
and exit code on a nonzero exit, and kills the child's process group on cancellation. Every call has a
55 s wall-clock budget (`ExecuteAsync` takes another); on expiry the group is killed and the error reads
`timed out after Ns — ` followed by what Git had written, else `git did not exit`, or for `fetch`, `push`,
`pull`, `clone` and `ls-remote` alone the hint that an unloaded SSH key is the usual cause. A timeout
carries exit code -1, so no semantic probe mistakes it for an expected exit. Semantic probes distinguish an expected exit (for example exit 1 from
`merge-base` or `symbolic-ref -q`) from any other failure by that exit code; nothing else is swallowed.
Every path argument is literal, never a pathspec, so a file named like pathspec magic or a glob matches
only itself. Revisions go after `--end-of-options` and before a trailing `--`, so neither an option-shaped
ref nor a ref that also names a path can be misread.

Every ref passes `GitRefs.IsSafe` before any Git call, at each door that accepts one: the comparison
target of a snapshot, commit list, diff or diff sides, a new workspace's branch and base, a fetched
reference, the branch a pull request pushes, and the default base the repository itself names
(`origin/HEAD` or the main worktree's branch; an unusable one is skipped, not trusted). The check runs in
process with `check-ref-format`'s rules and costs no child: it refuses an empty or option-shaped name,
`..`, `@{`, a lone `@`, a trailing `.` or `/`, control characters and space, `~ ^ : ? * [ \`, and an
empty, dot-led or `.lock` component. A refusal is `Not a usable git ref: <ref>`. A commit scope keeps its
own stricter hexadecimal rule.

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
  the branch still shows its diff. One that does not resolve fails as `HostErrorCode.UnknownCommit`, as
  does a `ReadContentBytesAsync` revision, so the client resets its scope instead of showing an error.
- `pinned`: one immutable commit to the working tree, plus untracked files. The id follows the commit
  scope's hexadecimal rule and is used as resolved, never through a merge base, so the range holds still
  while the branch and its target move. An id that names no commit fails as `HostErrorCode.UnknownCommit`. The
  modified side is the worktree, so the scope is mutable. Its snapshot lists no commits. No panel selects
  it yet; it is the range a "since I last looked" review measures.
- `branch` and `working` serve diff reads only: the comparison baseline to the working tree, and index to
  working tree.

Untracked files count their whole content as added lines (skipping symbolic links, files over 8 MiB and
binary content). Line counts come from `--numstat`; binary rows keep zero counts.

## Review target

What a workspace's changes are measured against is the host's, shared by every client
([HostState.SPEC.md](HostState.SPEC.md)): the ref the workspace was created from, recorded when the host
creates the worktree (never for `HEAD`), and a re-pointed target kept apart from it. A client sends the
effective one as the comparison target of its reads; the Changes panel's target picker writes it and
follows another client's choice. A target only has to be well formed: one that does not resolve is
stored, and the read against it fails visibly.

`GetDiffStatsAsync(workspacePath)` totals a workspace row's badge over the range its Changes panel opens
on: `diff --shortstat` from the merge base of the review target and the workspace's `HEAD` (the same
`ComparisonBaseAsync` the snapshot uses), or from `HEAD` without a target. Untracked files are not
counted. Only a worktree of the session's project is answered; a failed read is an error and the row
shows no badge. A client of a host without the call gets null.

Every fetch of a remote-tracking ref (`FetchRemoteAsync`: the default-base prefetch and workspace
creation) compares the ref's commit before and after and reports whether it moved, also when the fetch
failed after the ref had advanced. A move raises `BaseMoved` with the repository's common Git directory;
each file watcher of that repository whose workspace's review target is that ref emits a change, so its
clients re-read Git although no file in the workspace changed.

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
- `GetOpenReviewAsync` answers the open GitHub pull request of the workspace branch (number, https URL, unpushed
  and behind counts; -1 when unknown) from `gh pr list --head`, run with prompts disabled and an 8 s budget whose
  expiry kills the child's process group. Only a github.com `origin` is looked up; a missing or unauthenticated
  `gh`, a timeout or a non-https link degrade to no review. Successful answers, empty ones included, are cached
  for 60 s per worktree and branch; failures never are. Concurrent lookups share one `gh` call, and `fresh`
  skips the cache (joining a call already running) and fetches the branch so the behind count can be trusted.
- `PreviewPrAsync` proposes the branch name as title and the commit subjects since the base as body.
  `OpenPrAsync` guards in order: a branch shaped like an option or failing the ref-shape check, a missing `origin`,
  a base that is not on `origin`, and the base branch itself (nothing is pushed in these cases). It counts dirty
  files without blocking, re-reads the live branch, pushes `--set-upstream origin <branch>` with prompts
  disabled (`GIT_TERMINAL_PROMPT=0`, `LC_MESSAGES=C` with `LC_ALL` demoted to `LC_CTYPE`, a batch-mode SSH
  command only when the user configured none) and reports `authFailed` for rejected credentials. A non-GitHub
  origin ends as `pushed`. On GitHub an existing pull request is edited by number (the title only when edited),
  else one is created with an explicit base; a create that failed is re-checked first. Any `gh` failure ends as
  `compare`, a pre-filled compare link with the body capped at 4000 characters and a `ghProblem` of `missing` or
  `unauthenticated`. `SHARPRAIL_GH_OFFLINE=1` skips `gh`. The lookup cache is dropped after a push and a mutation.
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
- Remote fetches run only for a ref whose remote is configured, pass the ref-shape check first and fetch
  `--no-tags` after `--end-of-options`, so a crafted ref cannot
  become a refspec. A failed fetch reports Git's own error.

## Change write path

- `RevertChangeAsync` and `UndoChangeAsync` (`ChangeReverts.cs`, `TextSplice.cs`) revert one hunk or one
  file's whole change in the worktree. The request carries the scope, a line span per side and the SHA-256
  of each side the client saw, never content; under the workspace's mutation lock the host re-resolves the
  diff range (`GitRepository.ResolveDiffRangeAsync`, shared with `GetDiffSidesAsync`), re-reads both
  sides and refuses a hash mismatch, so a stale view writes nothing.
- Mutable scopes are those whose modified side is the worktree: `uncommitted`, `working`, `branch`, `all`,
  `pinned` and `untracked`. `commit` and `staged` are immutable.
- A hunk revert is text-only and never changes whether the file exists. Only `\n` ends a line and a
  restored line keeps its own ending. A whole-file revert writes the original bytes with the Git mode
  (executable or not), restores a deleted file, or moves an added or untracked file to the system trash.
  Symbolic links (worktree or Git), non-regular Git entries and mode-only changes are refused; a failed
  read of the worktree file is an error, never absence. Writes go through a temporary file beside the
  target and an atomic replace.
- Trash (`Trash.cs`): the file is first renamed to a claim beside it, then moved into `~/.Trash` on macOS or
  the freedesktop trash on Linux (`SHARPRAIL_TRASH_DIR` overrides the destination). If the trash refuses it
  the claim is renamed back, or kept as `.sharprail-recovery-*` with its path in the error.
  There is no permanent-delete fallback and no trash on other platforms.
- Receipts are opaque time-sortable ids held per workspace in host memory: at most 20 and 64 MiB of
  held bytes, oldest evicted, newest always kept, dropped when the session opens another workspace and on
  restart. Undo compares the current file (and mode) with the receipt's after-state, restores the held
  bytes or trashes a file the revert had created, consumes the receipt and returns an undo receipt that is
  itself undoable once.
- Failures are `ChangeException` with a `ChangeFailure` (stale view, immutable scope, invalid range,
  unknown receipt, unsupported change). The remote host carries the code in the `x-sharprail-change-code`
  trailer; this is the only coded failure so far (the general mechanism below remains unported).
- Diff sides are read as bytes (`cat-file blob`, so a tree, commit or gitlink is an error) and classified
  by `ContentClassifier` ([Files.SPEC.md](Files.SPEC.md)). A side is decoded only when it is text; a
  byte-only side travels empty with its `ContentMetadata`, and a byte-only working side no longer fails
  the read. Each side also names the revision it was read at (`OriginalRevision`, `ModifiedRevision`:
  null the working tree or an absent side, empty the index, otherwise a commit id), and an untracked
  byte-only file's diff is Git's binary notice.
- `ReadContentBytesAsync(path, revision)` returns one side's raw bytes plus metadata: the working tree
  for null, the index for empty, otherwise a full 40 or 64 digit commit id that must resolve to a commit
  (a ref name, abbreviation, tree or blob id is refused before Git runs). The path is contained like
  every other (escape and symbolic links refused), the size is capped at `FileLimits.EditableBytes`, and
  the bytes are never decoded, interpreted or written to disk. A commit id never moves, so reading the
  original side again after the branch advanced returns the same bytes; the hash is the identity a
  client compares. This is a gRPC call rather than an HTTP route, as the host serves no HTTP.
- `DiffSides` carries a SHA-256 per side (null when the side is absent) and the commit the original side
  was read at (null for the index, an unborn `HEAD` or an absent side). Hashes cover the raw bytes.

## Not yet ported

- Prefetching a base other than the default when it is picked in the Create workspace dialog (the
  default is prefetched as the dialog opens, and creation fetches whichever base was chosen).
- An immutable original for the index side: a `working` diff measures against the index, which has no
  commit id, so its bytes can change between two reads. Other scopes' originals are frozen to a commit.
- A streamed, HTTP-served byte route; the host returns one gRPC message per read, so the cap is the
  editable-file limit rather than a streamed bound.
- Untracked line counts only for content the shared classification calls text; invalid UTF-8 and
  magic-typed files omit counts as NUL-bearing ones already do.
