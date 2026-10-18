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
with direct local and streaming WebSocket RPC adapters. The UI consumes the same stream for either host.

## Boundary

- Owns: path containment (`ProjectServices.Contain`), directory listing with single-directory compaction,
  typed reads bounded by `FileLimits`, and the conflict-checked atomic save (`ProjectFileSaving.cs`).
- Forbidden: following a symbolic link anywhere below the workspace root; writing outside it.

## Reads

Windows workspace containment rejects parent traversal using either accepted
separator, NTFS alternate streams, and components with trailing dots or spaces.
Workspace-relative file/search/spec paths use `/` on the wire and in the UI.
File removal on Windows uses the system Recycle Bin with recycle-on-delete and
early-failure flags; an unavailable recycle operation fails instead of falling
back to a permanent delete. Shell recycling runs on a background STA thread.

Listings mark Git-ignored entries without hiding them, using a NUL-delimited `git check-ignore` batch for each listed directory. Git decides nested patterns, negation, repository and global excludes, and tracked-file exceptions. Non-Git folders or Git failures remain readable without ignore marks; local and remote listings carry the same flag.

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
  `FileLimits.PreviewBytes`, every read to `FileLimits.EditableBytes`, and a WebSocket RPC message is sized for one
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
- `copy` copies a file or directory inside the workspace, preserves Unix modes and empty directories,
  rejects links anywhere in the source and refuses an existing target or a descendant of the source.
  It stages beside the destination before installing the completed copy. Transfer staging entries are
  hidden from listings and watcher invalidations, so partial transfers never enter the file tree.
- `rename` moves within the workspace and refuses an existing target unless it differs only in case (a
  case-only rename on a case-insensitive disk).
- `trash` moves the entry to the OS trash (`NSFileManager` on macOS, `gio trash` on Linux), so a mis-click
  is recoverable; `reveal` selects the entry in the file manager (`open -R`, `explorer /select,`, or the
  containing folder where no select verb exists). Both run on the host's computer.
- None of them touches the workspace folder itself.

## Client file transfers

`ImportFilesAsync` accepts a workspace root, an optional destination folder and relative file/directory
entries. The root must still be this session's workspace. A null destination stores prompt attachments
in a unique private directory under host state; the response returns absolute host paths. Workspace
imports refuse existing root names before staging and never follow symbolic links. Validated paths
reject traversal, Git metadata, duplicates and ambiguous separators. Empty folders and arbitrary bytes
are preserved. A transfer is bounded to 4096 entries and 64 MB in total. Cancellation or a refused
installation removes staged and newly installed entries. The same operation serves local and remote clients.
File actions may also pin their expected workspace to reject a switch before the mutation acquires its gate.

## External configuration files

`ProjectServices` takes an `allowsExternalFile(workspaceRoot, path)` delegate from its composer (the plugin
runtime's `AllowsExternalFile`). An absolute path it admits bypasses workspace containment and goes through
the ordinary text read and compare-and-swap save; the delegate is asked again just before the save's move, so a
plugin disabled mid-save withdraws the file. Any other absolute path is still refused as outside the workspace.
Core knows no plugin id.

A read of a file that is gone throws `FileNotFoundException` (a remote client gets it back from WebSocket RPC's
`NotFound`), the one read failure a client acts on: an open tab marks its file deleted on disk.

## Change notification

- `IProjectServices.WatchFilesAsync` owns watchers for the workspace captured when enumeration starts.
  Its first empty batch confirms registration and requests a rescan. Each subscriber disposes its watchers on cancellation.
  Local and remote windows consume the same stream; no initial scan or Git snapshot is required.
- Host batches settle for 50 ms, cap pending paths at 4096 and request a rescan on overflow or watcher error.
  Reconnection invalidates open documents to recover changes missed while disconnected.
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
  the subscribers and sends them a rescan. Watcher errors, including synchronous startup errors,
  defer recovery off the native callback stack. One pending retry per workspace backs off from 250 ms
  to 30 seconds; duplicate errors cannot multiply retries, and a stable 30 seconds resets the backoff.
  Retries preserve subscriptions and request a rescan, while last-unsubscribe, forgetting and pool
  eviction cancel recovery. Stale callbacks cannot recreate a removed or replaced watcher set.
- `PrewarmWorkspaceAsync` starts the watchers of a workspace a client is about to open, which the
  workbench asks for when a workspace row is pointed at or focused. Watchers without a subscriber form a
  pool of at most eight, the least recently warmed evicted first; a subscription takes one over instead
  of starting another, and one with subscribers is never evicted. A pre-warmed watcher whose folder is
  gone is reaped on the next subscription or pre-warm, and removing a worktree drops its own.
- A watcher that cannot start degrades to read-on-demand with a logged reason; Git failure never blocks
  opening files.

## Watcher hygiene and diagnostics

All host-owned `FileSystemWatcher` instances are admitted through `WorkspaceWatches`; plugin activations
hold named logical leases on that same pool instead of creating independent recursive watchers. A
canonical absolute root (including trailing separator and filesystem-resolved aliases) identifies a
single watcher set. Repeated/concurrent Specs graph and Blueprint reads acquire at most one lease per
activation and workspace. Activation teardown, workspace removal and project close release those leases;
a stale admission completing after teardown or removal must not revive a removed watch. An open plugin
activation may intentionally retain a workspace lease until one of those boundaries, independently of
whether a window currently displays it. Registry removal uses the host's lossless removal event rather
than relying only on the coalescing shared-state snapshots; adding/removing a workspace within one
snapshot interval must still release its lease.

The opt-in `SharpRail-WorkspaceWatchers` EventSource records watcher allocation/enabling/failure/disposal
with root, watched path and generation, and logical lease acquisition/release with activation owner and
lease ID. Enabling it emits catch-up snapshots of retained roots, paths and owners. Its polling counters
are `allocated-watchers`, `enabled-watchers`, `logical-subscriptions`, `idle-roots`, `pending-retries`,
`created-watchers` and `disposed-watchers`. These count managed instances in this host process, not native
FSEventStreams or the system-wide FSEvents pool. Failed instances remain allocated until deferred recovery
or last-release. Successful starts are tracked per instance: a failed set may still contain other enabled
watchers. Every snapshot satisfies
`created - disposed == allocated`. Counts from separate host processes must be collected separately.
The framework-owned configuration reload watchers created by `RemoteServer.Builder` and
`LoopbackServer` (`McpRoute.cs`) are outside this registry; the embedded listener already disables
configuration reload. Browser serving also owns a `PhysicalFileProvider` (static serving makes no
explicit `Watch` request). Registry gauges therefore do not represent every managed watcher in the
process. The native probe below includes intercepted framework streams too.

From the repository root, use the performance toolkit's pinned local tools:

```sh
watcher_pid=12345 # replace with the host process ID
mkdir -p artifacts
(cd performance && ../.tools/dotnet/dotnet tool restore)
(cd performance && ../.tools/dotnet/dotnet tool run dotnet-counters -- monitor --process-id "$watcher_pid" --counters SharpRail-WorkspaceWatchers)
(cd performance && ../.tools/dotnet/dotnet tool run dotnet-trace -- collect --process-id "$watcher_pid" --providers SharpRail-WorkspaceWatchers --output ../artifacts/watchers.nettrace)
scripts/mise run check:fsevents
```

The last command builds Release and compiles a test-only macOS dyld interposer, injecting it only into
its own checks subprocess. `--no-build` reuses a previously built checks executable. Its scratch tree
and generated dylib are removed on exit. It first proves interception with a known enabled watcher,
then compares actual live/running FSEventStreams across repeated reads, client reconnects and three
real Git-host shutdown/disposal cycles, including metadata/refs watchers. The separate gitless
managed plateau case still asserts one workspace watcher. Create/retain/release accounting is separate from start/stop/
invalidate state; a failed start still owns an allocated stream until release. Unknown operations or
a probe capacity overflow fail the test instead of silently undercounting. The fixed probe table has
4096 slots and retains no released stream history. These are intercepted public API streams in the
owned subprocess, not a measurement of the kernel's global pool or other host processes. The probe
is neither linked into production nor an app setting.

Lease events and catch-up snapshots identify activation owners and exact lease IDs; watcher events
and snapshots identify instance IDs, paths and generations. Counters retain scalar lifetime totals,
not historical paths/owners; polling does not allocate whole registry snapshots. A legitimate idle
pre-warm contributes an allocated instance with zero subscriptions; a failed set can retain instances
and one retry until recovery/disposal. Inspect all these dimensions before diagnosing a leak.

`--watcher-hygiene` verifies repeated/concurrent real Specs and Blueprint reads, shared client leases,
activation owners, disable, project close/reopen and host-runtime teardown against baseline counts.
`--watchers` covers failed synchronous starts, replacement, backoff and cancellation of pending recovery.
The native check on macOS also covers framework-host start/stop/dispose and RPC reconnection; its control
watcher must prove actual API interception before native counts are accepted.
The pre-warm cap remains eight idle roots; logical subscription count never substitutes for underlying
watcher count when checking resource return.

## Not yet ported

- A watcher-start notification for the workspace activity lifecycle, before repository-metadata events
  can arrive, including pre-warm admission and recreation. It seeds or compares the retained HEAD SHA
  baseline (see [Workspaces.SPEC.md](Workspaces.SPEC.md#not-yet-ported)); registration failure must
  dispose the new watcher set rather than leave a partially admitted entry.
- A periodic identity check for a root replaced while its only subscribers stay attached and no client
  subscribes or pre-warms; today such a watcher heals on the next of those or on a watcher error.
- A readiness answer for a subscription (upstream's `startupNudge`); the registration rescan covers it.

Terminal file references additionally admit exact existing paths resolved by a host-owned terminal
for its workspace. This uses the same external-file read/save lifecycle as plugin configuration
files; it does not relax containment for arbitrary client paths. See [Terminals.SPEC.md](Terminals.SPEC.md#file-references-in-terminal-output).

Prompt attachments use `SavePromptImageAsync`: bounded PNG bytes are saved under the host state's
clipboard directory, returning an absolute host path. Local calls run off the dispatcher; remote calls
carry the bytes and return the same host path, never a client-local file path.

Verified on macOS arm64: the test-only native probe observed 22 created/22 released streams, peak six,
and returned to zero live/running streams after the separate gitless plateau/removal case and three
real Git-host lifecycles with 24 RPC reconnects. Each Git host admitted four enabled managed instances
(workspace, Git metadata and refs) and four additional live/running native streams; repeated reads and
reconnects did not allocate more. A known enabled FileSystemWatcher proved interception; there were
zero failed native starts and zero untracked operations. The earlier 13-created/13-released, peak-three
fixture was gitless and did not prove native metadata watcher lifetime. These scoped subprocess checks
include framework-created streams, not global pool availability or resources of a running user host.

Language-server navigation exposes exact dependency files for reading and marks them read-only in
the returned document. This does not authorize writes or arbitrary external reads. The exposure is
scoped to the workspace that requested navigation and ends when that workspace closes.