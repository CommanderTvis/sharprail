---
id: submodule-host-files
type: submodule-design
status: active
title: Files — workspace reads, saves and change notification
parent: module-host-core
---

# Files — workspace reads, saves and change notification

Upstream: packages/server/src/fs/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/watch/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: packages/server/src/trash/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

Read directories and files inside a workspace root, save edited text back safely, and tell the
workbench when the worktree changed so it re-reads. Reads and saves are host operations
(`IProjectServices.ListFilesAsync`, `ReadFileAsync`, `SaveFileAsync`; `IWorkspaceHost.ListRootFilesAsync`).
Change notification is host-owned (`IProjectServices.WatchFilesAsync`, `ProjectFileWatching.cs`, with the
shared watchers in `WorkspaceWatches.cs` and `PrewarmWorkspaceAsync` to start them early),
with direct local and streaming gRPC adapters. The UI consumes the same stream for either host.

## Boundary

- Owns: path containment (`ProjectServices.Contain`), directory listing with single-directory compaction,
  typed reads bounded by `FileLimits`, and the conflict-checked atomic save (`ProjectFileSaving.cs`).
- Forbidden: following a symbolic link anywhere below the workspace root; writing outside it.

## Reads

- Every path goes through one containment rule (`ProjectServices.Contain`), shared by listings, reads,
  saves, diffs and the change write path: resolved against the workspace root and rejected when it escapes
  it, names `.git` at any component in any letter case, or crosses a symbolic link. The leaf may be missing,
  which the operation then reports itself. A read also refuses a linked leaf; a change write leaves the leaf
  to its caller, which never follows it. Listings skip links entirely.
- Listings hide `.git`, `.sharprail` and `.tools`, sort directories first and then by name
  case-insensitively, and compact a run of directories that each hold exactly one directory into one
  `a/b/c` row.

- A read is decided by the shared classification (`ContentClassifier.Classify`, below) and always carries
  its metadata. Text is returned decoded. A byte-only file (a recognised magic number, NUL in the sniffed
  prefix or invalid UTF-8) answers empty text plus metadata instead of failing; a raster picture (PNG,
  JPEG, GIF, WebP, BMP, by its bytes rather than its extension) no larger than `FileLimits.PreviewBytes`
  also carries its bytes, and the workbench shows any other byte-only file as a card with its type, size
  and hash. Control characters other than NUL stay text. Markdown previews are limited to
  `FileLimits.PreviewBytes`, every read to `FileLimits.EditableBytes`, and a gRPC message is sized for one
  whole read or save.
- `ContentClassifier` is the one byte classification: media type from magic numbers (PNG, JPEG, GIF,
  WebP, AVIF, BMP, ICO, PDF, zip, gzip, WOFF/WOFF2), SVG from a text root element (after an optional
  XML prolog), a Git LFS pointer from its exact three-line form, and the filename only when the bytes
  say nothing. Text means no recognised magic number, no NUL in the first 8 KiB and a strict UTF-8
  decode, so an ASCII-only PDF is still byte-only. The metadata is `ContentMetadata` (SHA-256, byte
  length, textness, media type) and `IsActive` marks HTML, XHTML and SVG, which a client must show inert.
  Diff sides, file reads and the line counts of untracked files in a Git snapshot all use it, so a file
  is text everywhere or nowhere.
- Relative Markdown images resolve through the same contained read.

## Search

`SearchAsync` is a plain case-insensitive substring sweep of the workspace, with no index, query language
or external tool. In a Git worktree it reads the files `ls-files --cached --others --exclude-standard`
names, so ignored files are skipped; a folder without Git has nothing ignored and skips only the hidden
`.git`, `.sharprail` and `.tools` directories. Links, files over 512 KB and files carrying a NUL byte are
skipped. Each hit is a path, a 1-based line and that line's text cut at 400 characters; the sweep stops at
200 hits and says it was truncated.

## Saves

A save names the workspace root it was edited in and the text it started from. It is refused when the
window's workspace changed, when the file on disk no longer matches that original text (the editor keeps
the user's edits and reports the conflict), and when the text exceeds the editable limit. The new text is
written to a sibling temporary file with the original's Unix mode, the path and content are checked again,
and the temporary file is moved over the original. Saves are serialized with the session's other
mutations.

## Path actions

`ApplyFileActionAsync(FileAction)` changes one workspace path through the same containment, serialized with
the session's other mutations:

- `create-file` / `create-folder` make an empty file or folder, plus any folders on the way, and refuse an
  existing path.
- `rename` moves within the workspace and refuses an existing target unless it differs only in case (a
  case-only rename on a case-insensitive disk).
- `trash` moves the entry to the OS trash (`NSFileManager` on macOS, `gio trash` on Linux), so a mis-click
  is recoverable; `reveal` selects the entry in the file manager (`open -R`, `explorer /select,`, or the
  containing folder where no select verb exists). Both run on the host's computer.
- None of them touches the workspace folder itself.

A read of a file that is gone throws `FileNotFoundException` (a remote client gets it back from gRPC's
`NotFound`), the one read failure a client acts on: an open tab marks its file deleted on disk.

## Change notification

- The notification is an invalidation nudge, not data: the workbench re-reads through the same host
  reads, so a duplicate or coalesced event costs one extra read and never produces wrong state.
- One set of watchers serves every subscription to a workspace root (`WorkspaceWatches`); each
  subscription keeps its own coalescing window and path set, and the set is released when its last
  subscriber cancels. Switching workspace or closing a window cancels its subscription. Frames cap paths at 100 and request a full rescan on
  overflow, watcher errors, Git metadata changes and initial registration. The UI retries a failed
  stream after one second, with a fresh registration rescan covering disconnected changes.
- One recursive watcher covers the workspace root, ignoring `.git`, `.sharprail`, `.tools`,
  `node_modules` and `.DS_Store` events. Git metadata changes
  (commit, checkout, branch switch) that leave the working tree unchanged are seen through a second,
  non-recursive watcher on the worktree's Git directory (`HEAD`, `index`), the common directory's
  `packed-refs`, and a recursive one on the common directory's `refs`. External staging and unstaging
  therefore refresh Changes even without a file-content change. A linked worktree's Git directory is resolved by reading its `.git` file, never by
  running Git.
- Events coalesce into one refresh after 250 ms of quiet, and a storm still refreshes at least once a
  second. A refresh re-lists loaded folders (leaving the tree untouched when nothing moved), refreshes
  Specs when a Markdown file changed, reloads clean open documents in place, keeps unsaved editor text,
  and refreshes Changes and open diffs. Document, Git and folder reads start independently so folder
  enumeration cannot delay editor or Git updates. Directory invalidations include open descendants.
  Markdown reloads preserve the selected source/preview mode and its controls. Unsaved editor buffers
  stay intact, and conflict-checked saves prevent overwriting an agent's version on disk.
- Watchers heal. A subscription or pre-warm that finds the root replaced by another directory (its
  creation time changed, the portable stand-in for upstream's inode) restarts the set in place, keeps
  the subscribers and sends them a rescan; a watcher error does the same while the root still exists.
- `PrewarmWorkspaceAsync` starts the watchers of a workspace a client is about to open, which the
  workbench asks for when a workspace row is pointed at or focused. Watchers without a subscriber form a
  pool of at most eight, the least recently warmed evicted first; a subscription takes one over instead
  of starting another, and one with subscribers is never evicted. A pre-warmed watcher whose folder is
  gone is reaped on the next subscription or pre-warm, and removing a worktree drops its own.
- A watcher that cannot start degrades to read-on-demand with a logged reason; Git failure never blocks
  opening files.

## Not yet ported

- A watcher-start notification for the workspace activity lifecycle, before repository-metadata events
  can arrive, including pre-warm admission and recreation. It seeds or compares the retained HEAD SHA
  baseline (see [Workspaces.SPEC.md](Workspaces.SPEC.md#not-yet-ported)); registration failure must
  dispose the new watcher set rather than leave a partially admitted entry.
- A periodic identity check for a root replaced while its only subscribers stay attached and no client
  subscribes or pre-warms; today such a watcher heals on the next of those or on a watcher error.
- A readiness answer for a subscription (upstream's `startupNudge`); the registration rescan covers it.
