---
id: submodule-ui-panels
type: submodule-design
status: active
title: Workbench panels
---

# Workbench panels: Projects, Files, Specs, Changes, Review and Welcome

Upstream: apps/web/src/panels/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Changes-row truncation and Markdown source contract: apps/web/src/panels/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

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
  and asks the surface to rebuild only the affected tool. A Git refresh drops Changes and Review only when
  what they show changed (scope, target, commit, view, loading or error state, branch, change rows, branch
  and commit catalogues; for Review the branch, change count and pull request), so a watcher refresh that
  changes nothing leaves an open menu and the control under the pointer in place.
- Forbidden: reaching into another window, writing host state directly, or drawing docking chrome.

## Projects

- Each project row is a compact 28px row: always-visible chevron, folder icon and name, a collapsed-only
  plain count of the project's workspaces other than Default, and, on the shown project, a Create workspace
  `+` in a fixed right-edge column, the same
  control shape as the header's Add project `+` so the glyphs line up. The Create `+` tooltip names the
  platform's `Mod+N` chord. Long names truncate before the count and action. There is no visible Close or
  overflow icon.
- The whole row is one rounded highlight: hover highlights it, and the selected project at Project Home stays
  highlighted.
- Right-click opens the project context menu at the row without selecting or navigating; with the name
  focused, the Context Menu key or Shift+F10 opens the same menu. The menu is Start work, Copy absolute path, Open existing
  worktree…, separator, Close project. Create is exactly the `+` flow (opening the project's home first if
  needed).
- Open existing worktree… opens a chooser (`Panels/ExistingWorktreeDialog.cs`) fed by the host's list of
  unattached worktrees: branch and absolute path per row, detached-HEAD rows visible but disabled, an empty
  note, and a load failure with Retry. Choosing a row attaches it through the host, keeps the dialog open
  while the host answers and shows a refusal inline; success expands the project and enters the workspace.
- Close asks “Close {name}?” with “Removes this project from the open projects list. Its repository and
  workspaces are kept. Reopen it from Add project → Recents.”, Cancel focused and a Close project action.
  Confirm sends the host change and waits for its result; a rejection keeps the row. Closing the shown project
  moves to the next project's home or the clean Welcome. A project whose folder no longer exists closes
  without asking, since there is nothing left to lose. Dismissal restores focus to the source project name;
  a successful close focuses the fallback project or the Add project control.
- Workspace rows come from the host's registry (`HostState.Workspaces`), so every expanded project lists its
  own, shown or not; a row of another project switches project and workspace in one step. Each is two lines
  when the workspace has a branch: display name on top, branch beneath in the hint tier. The Default
  workspace (the project folder itself) is pinned first with a home icon, an attached external worktree has
  an open-folder icon and a managed one the branch glyph. The active workspace's icon and name use the accent.
  Until the host has listed the shown project, its folder alone stands in as the Default row.
- The window asks the host to list a project (ensuring its Default workspace and re-reading branches) when
  it mounts a project it has not listed yet, on Refresh, and when the user expands a project row. The rail
  redraws from the broadcast, never from that call's result. The rail is rebuilt only when a row is added,
  removed or changes kind or lock; selection, branch text and what Git allows are restyled in place, so
  rows, focus and pointer targets survive a mount, a Git refresh and a checkout.
- Workspace rows carry no `+N −M` change badge: the rail is for navigation and identity; change detail
  belongs to Changes.
- A hover- and focus-revealed kebab and right-click open the same workspace menu: “Open in” (editors from
  `ListEditorsAsync`, fetched lazily on first open, with explicit “Looking for editors…” and “No editors
  found” rows), Copy absolute path, Copy name and Reveal in file manager (the host opens the folder on its own machine). A
  managed worktree adds Rename and Remove worktree…; an external one only Remove from SharpRail, whose
  confirmation promises the checkout and its branch stay untouched. The Default workspace gets neither
  mutation. A locked worktree's Remove is disabled.
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
- The Add project `+` and the Welcome “Open project” card share one menu: Create project…, Open project, Enter host path…
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
- Create project… asks for a parent folder and project name. Choose… uses a local folder picker;
  a remote host uses the typed parent path. Creating runs off the UI thread, rejects existing targets
  and traversal names, and opens the new empty folder at Project Home. A failure keeps the input and
  dialog available for retry; dismissing before submission creates nothing.
- A folder that is not a Git repository opens directly, with no offer to initialise one. Workspace
  creation stays disabled for it, since there is no repository for a worktree to attach to.

## Welcome and Project Home

- With no project shown, the clean Welcome shows the SharpRail wordmark, one line saying to open a folder on
  the computer running SharpRail, and an Open project card with the shared Add project menu.
- Project Home shows a `PROJECT HOME` eyebrow, the project's name as the heading, one explanatory line naming
  the Create workspace chord, and the mode fork as two actions: Create workspace (isolated worktree, primary)
  and Work in project folder (enters the Default workspace directly, no dialog).
- A project without durable specs leads with a spec-first Set up project card (primary), followed by Create
  workspace and Work in project folder. Whether it has specs is asked lazily through
  `HasDurableSpecsAsync` for the one project at its home, off the UI thread; the cards wait for the first
  answer, later answers arrive after Markdown changes and refill the mounted cards in place, and a host that
  cannot answer leaves the ordinary two cards. Upstream's card seeds an agent prompt; with AI excluded it
  opens the same Create workspace dialog, so specs are drafted on an isolated branch.
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
  `ListFilesAsync` when expanded; a not-yet-read directory shows a `Loading…` child. Dotfiles are listed like any
  other entry; the host leaves out only `.git`, `.sharprail` and `.tools`.
  A run of directories that each hold exactly one directory is one `a/b/c`
  row, compacted by the host listing; it splits in place when a sibling appears and keeps what was expanded.
- A file whose bytes are neither text nor a picture opens as the byte card of the resource renderers in its
  tab (media type, size and hash, see [Rendering/SPEC.md](Rendering/SPEC.md)) rather than a window error; a
  clean open file that turns byte-only on disk is replaced by the same card.
- Pointing at or focusing another workspace's row asks the host to pre-warm its watcher (see
  [Files.SPEC.md](../SharpRail.Host.Core/Files.SPEC.md)); a failure is ignored.
- A single click on a directory row toggles it; the built-in double-tap toggle is suppressed so a double
  click does not undo the first click. A file row single click previews and double click (or Enter) keeps.
- Expansion lives above the rows and is keyed by directory path, so a rebuild or a watcher refresh re-reads
  the root and every loaded folder and restores what was expanded; vanished directories drop out through
  their parent.
- Switching workspaces within one project retains the Files pane while listings load, including expanded
  folders and scroll position. Listings reconcile rows by relative path and entry kind; unchanged rows
  stay mounted and only added, removed or renamed entries change. File contents do not affect row identity.
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
- An empty graph shows “No specifications in this project”.
- A refresh re-reads into the mounted tree rather than rebuilding the panel. A read that fails keeps the
  tree it last showed and puts an inline hint above it (“Specs could not be loaded: …”) with Retry, which
  repeats the read; the hint goes when a read succeeds. It is never a window error.

## Changes

Opening the comparison menu reloads the host's branch catalogue off the UI thread, so a branch
created since the last snapshot is available without closing the menu or refreshing the panel.

Git refreshes retain an open scope or comparison menu and its trigger. The latest snapshot or error
renders when the menu closes, so filesystem and ref events do not interrupt a choice already in progress.
The pane, toolbar and scroller survive refreshes and same-project workspace switches. Identical results
leave controls untouched; changed results reconcile unchanged file rows and tree folders, preserving
scroll position and expansion. A loaded snapshot stays visible while its replacement is loading.

- A fixed 32px toolbar says what is being diffed: the scope pill (All changes, Uncommitted, Staged, Branch,
  or one commit from the branch), the comparison-branch pill (`vs <branch>`, with Refresh git), and the
  List | Tree toggle. A commit scope is labelled by its short SHA with the full subject as tooltip, so the
  branch pill is not squeezed. The scope, comparison and selected commit belong to the workspace and persist
  in the profile; the commit catalogue is reloaded from Git, capped at 200.
- The scope menu's contents load on each open, never when the panel mounts: `ListCommitsAsync` for the
  commit rows and an uncommitted-scope status probe, which lets the Uncommitted row read “No uncommitted
  changes”, disabled, instead of opening an unexplained empty list. Each degrades on its own: a failed
  commit read says “Commits could not be loaded” when there are no earlier rows to keep, and a failed probe
  leaves the row as it was. Until the first read lands the menu says `Loading commits…`; a reopened menu
  keeps the previous rows while it re-reads. A selected commit scope already read the catalogue to
  validate its commit, so its rows are present without opening. Changing the comparison target or the
  workspace drops the rows.
- A selected commit that no longer exists (rebase, reset) resets the scope to All changes with a notice.
  Existence is checked through the host's single-commit lookup, independently of the comparison menu;
  a plugin's graph can select a commit on another branch or beyond that menu's 200 rows.
  Other failures leave the chosen scope alone.
- “Never answered”, “failed” and “answered empty” are three states. Before a snapshot the panel shows
  `Loading Git…`; a failure shows the error with Retry; only a landed snapshot with no changes says the
  working tree is clean. A folder that is genuinely not a repository says so. A clean claim from a read that
  never landed would be this product's worst failure.
- The List shows the full worktree-relative path — muted directory prefix (which yields first when the
  row overflows) + the status-colored basename, so the name a user scans stays visible. Added or untracked
  names use success, deleted names danger, otherwise muted. Each row carries `+N −M`.
- Every path is rendered as two truncatable halves (dir + basename), so a long basename can never push
  the counts out of the box. The halves are not equally truncatable: the dir prefix yields completely
  before the basename gives up a pixel, because the name is what a user scans. In Avalonia, the directory
  occupies the shrinking star column; the basename occupies the Auto column and is capped at the path's
  available width. The whole path is capped and clipped before the 8px gap and reserved counts column.
  Short directory/name pairs remain adjacent. Resizing the panel recalculates the available width.
  Tree folder labels also truncate within their column, preserving the icon, counts and menu gutter.
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

- Markdown file tabs render, don't read. A `.md`/`.markdown` file tab (from the file tree or the Specs
  panel — same open path) opens rendered by default and shows a slim `Preview | Source` header.
  Source is the lazy read-only editor: SharpRail uses the shared Scintilla frame on macOS in place of
  upstream's `MonacoEditor`, with selectable text as the other-platform fallback. Source shows the
  complete file, including frontmatter and fenced code. The choice is per tab, survives tab switches,
  and is not persisted across reload. Renderer behavior and lifecycle live in [Rendering/SPEC.md](Rendering/SPEC.md).
- Format views are chosen by the host's content metadata through the resource registry
  ([SPEC.md](SPEC.md)), each with Source one toggle away where the file is text:
  - Pictures (`image/*` bytes: PNG, JPEG, GIF, WebP, BMP, ICO) on a transparency checkerboard, fitted to the
    pane, at natural size or zoomed in steps between 10% and 1600%, with pixel dimensions and byte size. A
    picture over the preview limit or one that does not decode says so. A picture diff is 2-up, a swipe
    line, an onion-skin blend or the per-channel absolute difference on a canvas that holds both pictures
    (a side-only pixel differs by its alpha); overlays need both sides.
  - SVG drawn inert (`VectorPictures`): the document is parsed without DTD or external entities, then
    scripts, event attributes, foreign content, animation, external `href`s, external `url()` references,
    `@import` styles and `xml:base` are removed before it is rasterised at up to twice its size. It then
    uses the picture view and diff. Text that is not well-formed SVG is not drawn.
  - CSV/TSV as a table (`TableModel`): `.tsv` is tab-delimited; otherwise the delimiter is the one of
    comma, semicolon, tab and pipe on which the first twenty records agree, preferring a comma. Quoted
    delimiters, doubled quotes and line breaks stay inside their cell. A diff aligns rows by their raw
    text; a run of removals replaced by as many additions is shown as changed rows with the changed cells
    marked old → new.
  - JSON and JSONC as a tree built as nodes open. Text that only parses with comments or trailing commas
    carries a JSONC notice; text that does not parse shows the parser's message instead of a tree. A diff
    is structural: members by key regardless of order, array elements by identity (`id`, `key` or `name`,
    otherwise the whole value), showing added, removed and changed nodes with a count of the unchanged
    rest, a notice when the sides differ only in formatting or member order, and a notice naming the side
    that is not valid JSON.
  - Notebooks (`.ipynb`, nbformat 4) as cells with kind, execution count, source and outputs. Stream,
    plain-text and error outputs are text, PNG/JPEG/GIF outputs are decoded as pixels, and markup outputs
    (HTML, SVG, JavaScript) are named, never drawn. A diff aligns cells by id, otherwise by source, and
    shows added, removed and edited cells with a count of the unchanged ones.
  - A Git LFS pointer as a card with the object's size and id prefix and the `git lfs pull` hint; a diff
    shows the old and new pointer.
  - Any other bytes, including PDF, as a card with media type, byte length and SHA-256 prefix and Save a
    copy…, which writes the bytes the card describes to a place the user picks. A byte diff shows one such
    card per side.
- Raw HTML in a rendered Markdown document is read, never run (`MarkdownPreview.Html.cs`). `<img>` loads
  an http(s) or worktree-relative source at its stated width and height, centred or sent to a side by its
  own `align` or an enclosing `<p>`, `<div>` or `<center>`; `<picture>` shows its `<img>`; `<details>`
  is a disclosure titled by its `<summary>`, open when it says so, whose body may be ordinary Markdown
  between the opening and closing tags; headings, emphasis, code, links, line breaks and rules map to
  their native forms. Script, style, embedded documents and scripted or `data:` sources are dropped with
  their content, and text inside any other tag shows as text.
- A rendered Markdown diff focuses on its changes (`DiffFocus`). A top-level block changed when the
  positional alignment of the two documents' blocks by source text finds it no counterpart, so an identical
  block elsewhere cannot vouch for it and a change that earns no mark (a ticked checkbox, a list's start,
  a code fence) still counts. Two unchanged blocks stay visible on each side of a change; a longer
  unchanged run collapses behind one button naming the count and the last heading it hides, and a run of
  one is never hidden. The same rule applies to the items of a changed list when an item changed; items
  keep the number they have in the whole list, and a list that differs only in its own attributes, like
  tables, quotes and nested lists, renders whole. Expanding is one-way; the expanded runs are the tab's
  view state for that renderer and survive a refresh only while the run still starts at the same
  position. A merge in which no block changed shows a notice pointing at Source and one expander instead
  of an unmarked document.
- A diff Git reports as binary (a "Binary files" notice and no hunks) is described by its sides' host
  metadata rather than by its path. Bytes are fetched off the UI thread and used only while their hash
  still matches. When the file changes under the open tab the pane re-reads the sides and redraws if their
  hashes moved, keeping the tab's view and its state. HTML is text and shows as a source diff, so
  repository script never runs.

- A file or diff tab picks its view from what the content is, not from a per-pane format switch: Markdown
  ranks its rendered view above source (`MarkdownPreviewBody`, and the rendered merge in `DiffView`), and
  every other text file falls back to the code view (`CodeDocumentView`, the source diff).
- The view toggle exists only when a resource has more than one candidate view. Split | Inline, hide
  whitespace and copy are shown only while the selected view supports them, so no control promises
  something the view cannot do. View choice, layout and whitespace are independent per-tab state.
- Unchanged context collapses around a change at Git's own three-line default, so a diff shows what
  `git diff` would.
- A commit-scope diff is historical: it never offers an action that mutates the working tree.
- Reverting from a diff tab. A diff whose modified side is the worktree (All changes, Uncommitted, Branch
  and untracked files; never Staged or a commit) shows Revert file in its header, and on macOS each change
  block of a source view carries its own Revert on the block's first row, on the modified side when split.
  A block is a run of added and removed lines with its line span on both sides; a file that is all added or
  all deleted has no block, only the file. Buttons follow the editor as it scrolls and are created when
  their row first comes into view. SharpRail has no Ask agent action beside them. The rendered Markdown view
  and the other-platform text fallback offer Revert file only. The controls show only while the connected
  host reports `HostProtocol.ChangeWritePath` (`SharedState.Supports`): they are absent until the handshake
  answers, go while the host is disconnected and return in place, and the Undo action is offered under the
  same gate.
- A revert is checked against the content the tab rendered: at the click the window reads both sides and
  the diff again, and only if that diff is the one drawn does it send the sides' hashes with the request
  (`RevertChangeAsync`); the host refuses if either side moved since. A stale view, found by either check,
  reloads with the toast “This file changed since you opened it — review the new diff” instead of reverting.
  Success shows “Reverted hunk in {name}”, “Reverted {name}” or, for a new file, “Moved {name} to the trash”
  for eight seconds with an Undo action (`UndoChangeAsync`, expecting the content the revert left). A
  receipt the host no longer holds says “This change can no longer be undone — the host no longer holds
  it”; any other refusal is an error toast titled with what failed. Git and open diffs refresh afterwards.

## Review

The tool summarises the changed-file count on the current branch, offers Show Changes (which reveals or
restores the Changes tool) and owns the branch's pull request, a deterministic host flow of push plus `gh`
(see Git.SPEC.md). Upstream hangs this flow on its plan pane; SharpRail has no plan, so it lives here and the
description comes from the host's draft (branch name and commit subjects).

- The open pull request is one state per workspace and branch, first looked up when the panel is drawn for
  that branch (the host's 60-second cache allowed), so startup never waits on `gh`. Window activation with
  the panel built and every submit re-read it fresh. A lookup superseded by a newer one, by a submit's answer
  or by a branch switch is dropped; a failed lookup reads as no pull request.
- A `PR #N` chip links out to the pull request's URL through the system browser.
- Without a pull request the panel offers Open PR and Open draft PR. Either fetches the draft (a failure
  toasts “Couldn't prepare the PR”) and opens the compose dialog with editable Title and Description; only
  its submit pushes. The submit reports whether the title was touched, is disabled for a blank title and
  reads `Pushing…` while it runs, during which the dialog cannot be closed. The dialog closes on success and
  stays open with its edits on a failure (“Open PR failed”); cancelling forgets them.
- With a pull request the action reads Push updates, `Push updates (N)` and primary-filled with “N new
  commits aren't in PR #N yet” when the lookup reports unpushed commits. Unlike upstream it goes through
  the same dialog, titled Push updates and warning that the description overwrites the open PR's: the only
  source for a silent refresh here would be the regenerated commit list, which must not replace a
  hand-written description unseen.
- A lookup reporting commits behind is a sync conflict, not a force-push cue, and takes precedence: the
  action reads Branch diverged, the panel explains that a plain push cannot land and force-pushing would
  drop the remote's commits, and the action copies `git pull --rebase origin <branch>`. The command is offered
  only for a branch name that is inert in every shell (`[A-Za-z0-9][A-Za-z0-9._/-]*`); any other name gets the
  instruction as text and a disabled action.
- Every outcome toasts: PR opened, PR updated, Branch pushed (a non-GitHub origin, or the compare page, which
  opens in the browser), and separately how many uncommitted files stayed local.
- A fixable setup gap opens guidance instead of a toast: a push that could not authenticate, or a compare
  result naming a missing or unauthenticated GitHub CLI, with copyable commands for the host's platform
  (generic ones for a remote host, whose platform the client does not know), the compare page as an action
  and Try again, which resubmits the last edited title and description rather than a regenerated draft.
  Taking the compare page ends the flow.

## Browsing: preview versus keep

Each center group has one preview slot; its tab title is italic. Single-clicking a file, spec or change row,
or following a rendered-document link, opens into the last-focused center group as preview. Double click
keeps; clicking an already selected preview keeps; the tab menu offers Keep open. A preview replaces only
that group's slot. Opens single-flight per workspace and path: a double click composes preview then keep into
one final placement. A per-group navigation clock captured at request time lets a slow read lose to later
navigation, while a deliberate keep still commits; if the destination group disappeared the open reroutes
to the current focus.

## Live refresh

For local and remote hosts the window subscribes to host-owned filesystem watching of the workspace
plus Git `HEAD` and refs. The host coalesces bursts (flushing at least once a second during a storm),
and the window refreshes the Files tree, the Specs tree when a
spec changed, open documents and the Git snapshot. Every refresh is stamped with the project request, so an answer that arrives
after a switch is dropped. Refreshes preserve view state (expansion, selected rows). A diff tab whose file
left the change set stays open: once its two sides are identical (an out-of-band commit, a revert) it says
“No differences between the two sides.” in place of the diff and withdraws Revert file, and a diff that can
no longer be read (an untracked file that was deleted) keeps its last content. The Changes list is where the
disappearance shows.

## Get right

- Startup restores routing and visible documents first; Git snapshots, spec indexing and folder reads load
  progressively and must never block opening accessible files (AGENTS.md startup rules).
- A panel never optimistically applies a host-state change: rename, close, recents and labels converge on the
  broadcast.
- Menus opened from a hidden trigger are placed against the trigger, not the pointer, so keyboard and pointer
  paths agree.

## Not yet ported

- The Settled shelf: a sort row above live workspaces, a collapsed `Settled · N` disclosure and slim
  name-only settled rows with reason chips and tooltips, initially ten rows with Show 25 more. Selecting
  a settled workspace expands and pages its shelf to reveal it. Settle and Keep active belong to the
  shared row/header actions, with hover actions and the same context menus; Default never settles.
  This is list organisation only: it never deletes files, and offers no bulk removal. The shelf and
  sorting must appear only when the host supports settling. AI activity guards are outside scope.
- A client-local, host-qualified first-automatic-move notice: after automatic settled counts remain
  stable for two seconds, announce “Moved N quiet workspaces to Settled”, explain the rules and offer
  Show to expand the affected projects and shelves. Manually settled rows do not count; remember the
  notice in the profile rather than browser storage. Activity-based settling is in scope; upstream's
  pull-request-driven automatic settling and shared review snapshot are excluded from this sync.
- Acceptance coverage for the shelf remains unported: Settle / Keep active roundtrips converge across
  windows, the first-move notice appears once and Show reveals affected rows, unsupported hosts retain
  their legacy order, the idle setting survives restart, and selection/reselection preserves the live
  latch without moving an opened settled row.
- Scroll-offset restoration across file/diff renderer detach and attach for delayed content. An
  offset that the new scroller cannot yet hold stays pending until content grows or the user scrolls;
  switching away first saves that pending offset rather than the temporary clamped value. The rendered
  Markdown view and its source save their position in the tab's renderer view state; the pending-offset
  rule and the other views are not ported.
- CI status beside the pull request chip; it has no upstream source.
- In pull request setup guidance: a Run action that executes a command in a new workspace terminal, git's
  own error text for a push that could not authenticate (the host reports only that it did) with the
  host-key hint derived from it, and host-platform commands for a remote host.
- Revealing the active workspace's project on mount: rail expansion is the persisted collapsed set, so
  every project starts expanded and a collapsed one stays collapsed when it is entered.
- A shared searchable branch combobox for the comparison target; the Changes pill uses a tree menu:
  Local contains branch path folders, Remote contains configured remote names and branch path folders.
  Leaves display the final path segment, retain the full ref for selection and tooltips, and mark the
  selected target. Empty groups and remote HEAD aliases are omitted using the host branch catalogue.
- The full review surface: per-file accordion, comment lifecycle, selection-triggered commenting in
  editors and rendered previews, tab review flags and send actions.
- Chat deep links into Changes and Specs.
- PDF pages and page-pair diffs: no PDF renderer is referenced, so a PDF shows the byte card.
- A sanitised HTML preview: HTML opens as source only.
- Moved array elements in a JSON diff (they show as removed and added), free picture zoom and panning by
  gesture, and review anchors on any format view.
- Text wrapping around a floated picture in rendered Markdown (a floated picture is placed at its side
  on its own line), and raw HTML tables, lists and inline styles, which show as their text.
- In a rendered Markdown diff, a `<details>` whose body Markdown splits across blocks is not regrouped
  into one disclosure, and a list is compared item by item only when both documents have the same number
  of top-level lists (otherwise an item counts as changed when it carries a mark).
- An explicit notice on a diff tab whose two sides became identical after its file left the change set;
  the tab keeps its last content.
- Live refresh for remote hosts.
