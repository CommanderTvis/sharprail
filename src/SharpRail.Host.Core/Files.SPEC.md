---
id: submodule-host-files
type: submodule-design
status: active
title: Files — workspace reads, saves and change notification
parent: module-host-core
---

# Files — workspace reads, saves and change notification

Upstream: packages/server/src/fs/SPEC.md @ c44534ea
Upstream: packages/server/src/watch/SPEC.md @ 4a65ed7f
Upstream: packages/server/src/trash/SPEC.md @ c44534ea

## Responsibility

Read directories and files inside a workspace root, save edited text back safely, and tell the
workbench when the worktree changed so it re-reads. Reads and saves are host operations
(`IProjectServices.ListFilesAsync`, `ReadFileAsync`, `SaveFileAsync`; `IWorkspaceHost.ListRootFilesAsync`).
Change notification currently runs in the UI (`SharpRail.UI/WorkspaceWatcher.cs`) for local workspaces.

## Boundary

- Owns: path containment (`ProjectServices.Resolve`), directory listing with single-directory compaction,
  typed reads bounded by `FileLimits`, and the conflict-checked atomic save (`ProjectFileSaving.cs`).
- Forbidden: following a symbolic link anywhere below the workspace root; writing outside it.

## Reads

- Every path is resolved against the workspace root and rejected when it escapes it or crosses a
  symbolic link at any component. Listings skip links entirely.
- Listings hide `.git`, `.sharprail` and `.tools`, sort directories first and then by name
  case-insensitively, and compact a run of directories that each hold exactly one directory into one
  `a/b/c` row.
- A read returns images (PNG, JPEG, GIF, WebP, BMP) as bytes and text as strict UTF-8; invalid UTF-8 is
  reported as binary, while NUL and other control characters in valid text are allowed. Markdown and
  image previews are limited to `FileLimits.PreviewBytes`, editable text to `FileLimits.EditableBytes`,
  and a gRPC message is sized for one whole read or save.
- Relative Markdown images resolve through the same contained read.

## Saves

A save names the workspace root it was edited in and the text it started from. It is refused when the
window's workspace changed, when the file on disk no longer matches that original text (the editor keeps
the user's edits and reports the conflict), and when the text exceeds the editable limit. The new text is
written to a sibling temporary file with the original's Unix mode, the path and content are checked again,
and the temporary file is moved over the original. Saves are serialized with the session's other
mutations.

## Change notification

- The notification is an invalidation nudge, not data: the workbench re-reads through the same host
  reads, so a duplicate or coalesced event costs one extra read and never produces wrong state.
- One recursive watcher covers the workspace root with `.git` paths excluded. Git metadata changes
  (commit, checkout, branch switch) that leave the working tree unchanged are seen through a second,
  non-recursive watcher on the worktree's Git directory (`HEAD`) and a recursive one on the common
  directory's `refs`; a linked worktree's Git directory is resolved by reading its `.git` file, never by
  running Git.
- Events coalesce into one refresh after 250 ms of quiet, and a storm still refreshes at least once a
  second. A refresh re-lists loaded folders (leaving the tree untouched when nothing moved), refreshes
  Specs when a Markdown file changed, reloads clean open documents in place, keeps unsaved editor text,
  and refreshes Changes and open diffs.
- A watcher that cannot start degrades to read-on-demand with a logged reason; Git failure never blocks
  opening files.

## Not yet ported

- Host-owned watching, so remote workspaces receive live refresh: a per-workspace watcher started lazily
  by the first read, pushing a pathless or path-capped (100 paths, `truncated`) change frame through the
  host to every client looking at that workspace.
- Self-healing watchers that re-create themselves when the root's inode changes and reap watchers for
  forgotten workspaces.
- A startup nudge covering the platform stream's registration window, and a bounded pre-warm pool for
  workspaces a client is about to open.
- Ignoring `node_modules` and `.DS_Store` churn in the watcher.
- One byte-level content classification shared by file reads, diff sides and untracked line counts:
  media type from magic numbers (PNG, JPEG, GIF, WebP, AVIF, BMP, ICO, PDF, zip, gzip, WOFF/WOFF2), SVG
  from a text root element, a Git LFS pointer from its exact three-line form, and the filename consulted
  only when the bytes say nothing. Text means no recognized magic number, no NUL in the first 8 KiB and a
  strict UTF-8 decode. Reads decide images by extension today and allow NUL in text.
- Content metadata on every read (SHA-256, byte length, textness, media type), with a byte-only file
  answered as empty text plus metadata rather than an error. The hash is the resource's identity for
  compare-and-swap writes.
- Containment that also refuses `.git` and allows a missing leaf, so a deleted file can be restored, with
  an option not to follow a leaf link.
- Moving a path to the system trash as the only way the host destroys a user's file: one literal path, a
  failure surfaced to the caller, and no permanent-delete fallback. Nothing in SharpRail deletes
  workspace files yet; the first consumer is the whole-file revert in [Git.SPEC.md](Git.SPEC.md).
