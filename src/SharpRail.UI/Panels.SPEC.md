---
id: submodule-ui-panels
type: submodule-design
status: active
title: Workbench panels
---

# Workbench panels: Projects, Files, Specs, Changes, Review and Welcome

Upstream: apps/web/src/panels/SPEC.md @ c44534ea

## Responsibility

The feature views a `WorkbenchWindow` builds for its docking surface, in the partial files
`ProjectPanels.cs` (Projects, Files, Specs), `ProjectHome.cs` (Welcome, Project Home, project and
workspace actions, Create workspace flow), `GitPanels.cs` / `ChangesTree.cs` (Changes and Review) and
`WorkbenchWindow.cs` (the empty-center receipt). A panel fills the container `DockSurface` gives it and
never knows its arrangement: tab strips, group headers, side stacks and center topology belong to
`Docking`, and a singleton tool renders the same wherever its group moves. Settings and dialogs are
specified in [Panels/SPEC.md](Panels/SPEC.md); shared controls and document rendering in
[Rendering/SPEC.md](Rendering/SPEC.md).

## Boundary

- Panels read host data through the window's `IProjectServices` / `IWorkspaceHost` and shared state through
  `SharedState`; they change shared state (project list, workspace labels, recents) only through the host
  and redraw when the broadcast arrives (`HostSync.cs`), never by writing a local copy.
- A panel is built on demand and cached in the window's tool-content map; a refresh drops the cached control
  and asks the surface to rebuild only the affected tool.
- Forbidden: reaching into another window, writing host state directly, or drawing docking chrome.

## Projects

- Each project row is a compact 28px row: always-visible chevron, folder icon and name, a collapsed-only
  plain count of the project's worktrees, and a Create workspace `+` in a fixed right-edge column, the same
  control shape as the header's Add project `+` so the glyphs line up. The Create `+` tooltip names the
  platform's `Mod+N` chord. Long names truncate before the count and action. There is no visible Close or
  overflow icon.
- The whole row is one rounded highlight: hover highlights it, and the selected project at Project Home stays
  highlighted.
- Right-click opens the project context menu at the row without selecting or navigating; with the name
  focused, the Context Menu key or Shift+F10 opens the same menu. The menu is Create workspace, separator,
  Copy absolute path, Close project. Create is exactly the `+` flow (opening the project's home first if
  needed). Copy writes the project's absolute host path to the clipboard without selecting the project or
  activating a workspace, plain folders included.
- Close asks “Close {name}?” with “Removes this project from the open projects list. Its repository and
  workspaces are kept. Reopen it from Add project → Recents.”, Cancel focused and a Close project action.
  Confirm sends the host change and waits for its result; a rejection keeps the row. Closing the shown project
  moves to the next project's home or the clean Welcome. A project whose folder no longer exists closes
  without asking, since there is nothing left to lose. Dismissal restores focus to the source project name;
  a successful close focuses the fallback project or the Add project control.
- Worktree rows appear under the shown project. Each is two lines when the worktree has a branch: display
  name on top, branch beneath in the hint tier. The Default workspace (the project folder itself) is pinned
  first with a home icon in place of the branch glyph. The active workspace's icon and name use the accent.
- Workspace rows carry no `+N −M` change badge: the rail is for navigation and identity; change detail
  belongs to Changes.
- A hover- and focus-revealed kebab and right-click open the same workspace menu: “Open in” (editors from
  `ListEditorsAsync`, fetched lazily on first open, with explicit “Looking for editors…” and “No editors
  found” rows), Copy absolute path, Copy name (the display name), and, for a non-Default worktree, Rename and Remove worktree…. The Default
  workspace gets neither mutation. A locked worktree's Remove is disabled.
- Rename replaces the row with an in-place single-line input, prefilled, focused and selected. Enter or
  focus leaving to another control in the same window commits; Escape cancels; blank text or text unchanged
  from the edit-start label exits without a request, so a peer's rename is never reverted by an untouched
  editor. A rebuild of the rail by a broadcast, or focus moving to another window, keeps the editor open.
  While the host is unreachable a commit stays pending and is sent after reconnecting; other rejections
  report the error and end editing. The label is host state; the branch and folder never change.
- Remove asks for confirmation (Git refuses a dirty worktree; the branch is kept). Removing the active
  workspace first moves to the previously selected workspace, or Project Home.
- Rail expansion is per-app profile state (`CollapsedProjects`), so it survives restarts and window
  rebuilds. The chevron toggles it and keeps focus on the chevron.
- Opening a project lands on its Project Home, never auto-entering a workspace. Selecting a project row goes
  to that project's home, deselecting the active workspace.
- The Add project `+` and the Welcome “Open project” card share one menu: Open project, Enter host path…
  (Enter path… locally, where the host is this computer), Clone repository…, and Recent (closed recent
  projects). Clone repository… asks for a URL, the parent folder (Choose… locally, typed on a remote host),
  an optional folder name whose placeholder is the repository's name and an optional depth (at least 1);
  the target path shows before anything runs, the clone runs off the UI thread, the dialog stays open with
  git's reason when it fails, and a successful clone opens as a project. Open project
  uses the native folder picker locally; on a remote host, or when the picker fails, it opens the path
  dialog (with the picker's error). The path entry is always present because a remote client cannot tell
  where a native picker would open. Every open gesture
  takes a new picker generation, so a later gesture supersedes an earlier one before it can open a project.
- Plugin file selection uses the same remote path dialog with file-specific wording
  and a Choose document action; project and directory selection retain folder wording.
- A folder that is not a Git repository opens directly, with no offer to initialise one. Workspace
  creation stays disabled for it, since there is no repository for a worktree to attach to.

## Welcome and Project Home

- With no project shown, the clean Welcome shows the SharpRail wordmark, one line saying to open a folder on
  the computer running SharpRail, and an Open project card with the shared Add project menu.
- Project Home shows a `PROJECT HOME` eyebrow, the project's name as the heading, one explanatory line naming
  the Create workspace chord, and the mode fork as two actions: Create workspace (isolated worktree, primary)
  and Work in project folder (enters the Default workspace directly, no dialog).
- Active plugins' project-scoped actions (W10) follow the two actions on Project Home.
- Welcome is the work-in-this-project surface once a project is shown; opening another project is the rail's
  `+`.

## Empty center

When a center group has no document, the workbench shows a persistent receipt rather than a placeholder:
`WORKSPACE READY`, the display name, `branch · from base`, and “Files, changes, and terminals are scoped to
this workspace.”; for the Default workspace `DEFAULT WORKSPACE`, the project name, `on branch`, and a line
saying work runs directly in the project folder. An Open file action reveals the Files tool. It is neither
one-time nor dismissible, so it also orients after the last tab closes. The branch line updates when the Git
snapshot lands. Active plugins' workspace-scoped actions (W10) render beside Open file, which makes that row the
workspace's start-actions row.

A plugin's side tool (`plugin:<id>:<tool>`) renders its registered control while the plugin is active and
otherwise a placeholder, “*label* is off” with the tool's icon and an Open Settings › Plugins action, in the same
tab; a `RequiresGit` tool is withheld from the catalog in a workspace without Git history. Files rows, Changes
rows and the diff path chip take a file icon from an active plugin's file-icon slot (W17), and the Changes scope
can be set by a plugin (W18): uncommitted, one commit, the comparison target, or a pinned base.

## Files

- A lazily populated tree of the workspace. Directories read one level at a time through
  `ListFilesAsync` when expanded; a not-yet-read directory shows a `Loading…` child. Hidden entries follow the
  Show hidden files preference.
- A single click on a directory row toggles it; the built-in double-tap toggle is suppressed so a double
  click does not undo the first click. A file row single click previews and double click (or Enter) keeps.
- Expansion lives above the rows and is keyed by directory path, so a rebuild or a watcher refresh re-reads
  the root and every loaded folder and restores what was expanded; vanished directories drop out through
  their parent.
- Each row has its own context menu: New file…, New folder…, separator, Reveal in Finder (Show in Explorer
  on Windows, Open containing folder elsewhere), Copy absolute path, Rename…, and Delete file / Delete
  folder. New creates inside a folder row and beside a file row (a compact chain's row stands for its
  deepest folder); a typed `a/b.md` is a path. Rename selects the stem, so typing keeps the extension.
  One name dialog (`Dialogs.PathName`) takes the name: a name already listed in the destination shows an
  inline collision as it is typed and cannot be confirmed; the dialog stays open while the host acts and
  shows a later rejection beside the input. A created file opens as a kept tab and a created child expands
  its folder. Delete asks first, then moves the entry to the OS trash. Every action is
  `IProjectServices.ApplyFileActionAsync`; the tree re-lists after it, and the local watcher does too.
- An open file whose file is deleted on disk keeps its buffer and says so: its tab shows a `deleted` mark
  and a Scintilla editor a banner over the text. Only a missing file marks it (a local
  `FileNotFoundException`, a remote `NotFound`); any other failed read leaves the tab as it was, and a later
  read that finds the file clears the mark. A tab open on a renamed file is left on the old path, and so
  marks itself deleted.

## Specs

- The builtin spec dialect owns this panel, built from its `graph` plugin method, loaded off the UI thread and discarded if the project
  changed meanwhile. Roots are specs with no, a dangling or a self parent; roots and siblings sort by title.
  A visited guard avoids repeated descendants. Parent cycles have no root and are reported by `spec_validate`.
  Rows start expanded and mark the active file without rebuilding the panel.
- Rows stay on one line: role icon, title (a ` — ` or ` – ` separator collapses to ` · `), then the role
  (`ARCH`, `MODULE`, `SUBMODULE`, `TASK`, `GOAL`, or the normalised type) revealed on hover or keyboard
  focus. The top-level `goal-and-requirements` spec reads `Main spec` in the accent. The automation help text
  carries path and role unconditionally.
- Row click previews the spec's document and double click or Enter keeps it, through the same flow as
  Files. The chevron alone expands. An inline failed-read notice provides Retry. There is no toolbar, lifecycle status or graph
  canvas, no editing.
- An empty graph shows “No specs”; an unresolved graph shows six loading rows.

## Changes

Git refreshes retain an open scope or comparison menu and its trigger. The latest snapshot or error
renders when the menu closes, so filesystem and ref events do not interrupt a choice already in progress.

- A fixed 32px toolbar says what is being diffed: the scope pill (All changes, Uncommitted, Staged, Branch,
  or one commit from the branch), the comparison-branch pill (`vs <branch>`, with Refresh git), and the
  List | Tree toggle. A commit scope is labelled by its short SHA with the full subject as tooltip, so the
  branch pill is not squeezed. The scope, comparison and selected commit belong to the workspace and persist
  in the profile; the commit catalogue is reloaded from Git, capped at 200.
- A selected commit that no longer exists (rebase, reset) resets the scope to All changes with a notice.
  Existence is checked through the host's single-commit lookup, independently of the comparison menu;
  a plugin's graph can select a commit on another branch or beyond that menu's 200 rows.
  Other failures leave the chosen scope alone.
- “Never answered”, “failed” and “answered empty” are three states. Before a snapshot the panel shows
  `Loading Git…`; a failure shows the error with Retry; only a landed snapshot with no changes says the
  working tree is clean. A folder that is genuinely not a repository says so. A clean claim from a read that
  never landed would be this product's worst failure.
- List shows the full worktree-relative path as two truncatable halves: muted directory prefix and the
  status-coloured basename (added or untracked in success, deleted in danger, otherwise muted). The
  directory yields before the basename. Each row carries `+N −M`.
- Tree builds folders from the changed paths only, default expanded, compacting single-directory runs into
  one slash-joined row, with summed `+N −M` per folder. Folder rows reserve the same trailing gutter as file
  rows so counts align.
- Every file row, in both views, is wrapped in one frame that owns the whole highlight (hover, active diff,
  menu open), including the trailing menu slot. A hover- or focus-revealed chevron and right-click open the
  same menu: View, Copy path, and Stage file / Unstage file where the scope allows it. Folder rows get no
  menu.
- Clicking a row opens its diff as a center document through the shared preview/keep navigation. A diff
  tab's identity includes its scope (and commit), so the same file in another scope is another tab and a
  tab's meaning never changes when the Changes scope flips. Changing the comparison branch re-points open
  branch-scope diff tabs. A row is highlighted while its diff is the focused center selection.
- Rendering of the diff itself is `Rendering/DiffView` (see Rendering/SPEC.md).

## File and diff views

- A file or diff tab picks its view from what the content is, not from a per-pane format switch: Markdown
  ranks its rendered view above source (`MarkdownDocumentView`, and the rendered merge in `DiffView`), and
  every other text file falls back to the code view (`CodeDocumentView`, the source diff).
- The view toggle exists only when a resource has more than one candidate view. Split | Inline, hide
  whitespace and copy are shown only while the selected view supports them, so no control promises
  something the view cannot do. View choice, layout and whitespace are independent per-tab state.
- Unchanged context collapses around a change at Git's own three-line default, so a diff shows what
  `git diff` would.
- A commit-scope diff is historical: it never offers an action that mutates the working tree.

## Review

A placeholder tool: it summarises the changed-file count on the current branch and offers Show Changes,
which reveals or restores the Changes tool.

## Browsing: preview versus keep

Each center group has one preview slot; its tab title is italic. Single-clicking a file, spec or change row,
or following a rendered-document link, opens into the last-focused center group as preview. Double click
keeps; clicking an already selected preview keeps; the tab menu offers Keep open. A preview replaces only
that group's slot. Opens single-flight per workspace and path: a double click composes preview then keep into
one final placement. A per-group navigation clock captured at request time lets a slow read lose to later
navigation, while a deliberate keep still commits; if the destination group disappeared the open reroutes
to the current focus.

## Live refresh

On a local host the window watches the workspace (excluding `.git`) plus the Git `HEAD` and refs, coalesces
bursts (flushing at least once a second during a storm), and refreshes the Files tree, the Specs tree when a
spec changed, open documents and the Git snapshot. Every refresh is stamped with the project request, so an answer that arrives
after a switch is dropped. Refreshes preserve view state (expansion, selected rows). A diff whose file left
the change set keeps its last content.

## Get right

- Startup restores routing and visible documents first; Git snapshots, spec indexing and folder reads load
  progressively and must never block opening accessible files (AGENTS.md startup rules).
- A panel never optimistically applies a host-state change: rename, close, recents and labels converge on the
  broadcast.
- Menus opened from a hidden trigger are placed against the trigger, not the pointer, so keyboard and pointer
  paths agree.

## Not yet ported

- Open existing worktree… in the project menu, an external-worktree row kind with Remove from SharpRail, and
  Reveal in file manager in the workspace menu.
- Project rows for non-shown projects expanding their own worktree lists (only the shown project lists
  worktrees), and revealing the active workspace's project on mount.
- The Welcome “Set up project” card and has-specs routing.
- A distinct inline Specs load-failure hint with Retry that keeps the previous tree; failures are reported
  through the window.
- Compact single-directory runs in the Files tree.
- A shared searchable, grouped branch combobox for the comparison target; the Changes pill is a flat menu.
- Lazy, open-triggered loading of the scope menu's commit rows and an “No uncommitted changes” probe.
- The full review surface: per-file accordion, comment lifecycle, selection-triggered commenting in
  editors and rendered previews, tab review flags and send actions.
- Chat deep links into Changes and Specs.
- Reverting from a diff tab: a Revert on each change block and Revert file in the header for scopes whose
  modified side is the worktree, checked against the content the tab rendered so a stale view reloads with
  “This file changed since you opened it — review the new diff” instead of reverting, and an Undo offered
  for a few seconds afterwards (a whole-file revert of a new file moves it to the trash). The host has no
  revert or undo operation yet.
- Format-specific views chosen by host content metadata, each with Source one toggle away where the file is
  text: images (fit, natural size, zoom, dimensions and byte size on a transparency checkerboard; diffs as
  2-up, swipe, onion skin or difference), SVG drawn inert, CSV/TSV tables with a sniffed delimiter and
  cell-level diff, JSON/JSONC trees with a structural diff and explicit invalid and dialect notices,
  notebooks, PDFs with page-pair diffs, sanitised HTML, a Git LFS pointer card, and a binary card with
  identity, sizes and download. SharpRail opens everything as text or Markdown.
- Sanitised raw HTML in rendered Markdown documents (centred or floated images, `<details>`,
  `<picture>`), with relative sources resolved against the worktree; only the diff marks are recognised.
- An explicit notice on a diff tab whose two sides became identical after its file left the change set;
  the tab keeps its last content.
- Live refresh for remote hosts.
