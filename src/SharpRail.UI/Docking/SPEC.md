# Docking — window-local workbench frame

Upstream: apps/web/src/shell/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/layout/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/layoutIntents/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/layoutState/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/navigation/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))

## Responsibility

The workbench engine of one window: the frame and per-workspace view model (`LayoutState.cs`), legal
atomic transitions over it (`LayoutSession`), and its rendering, resize, drag and keyboard arrangement
(`DockSurface` and its partial files). It renders containers; panels, documents and terminals are
supplied by the window through a content callback and never learn where they are placed.

The surface can defer its first build until the composing window has configured tab projection and
callbacks. The workbench uses that path to avoid constructing and discarding a preliminary dock;
independent callers keep the default immediate build.

## Boundary

- Owns: `DockState`, `DockGroup`, `CenterNode`, `WorkspaceView` and `DockTab`; validation
  (`LayoutSession.IsValid`); built-in presets; transitions and their atomic installation; the projection
  from frame plus active view to rendered groups and tabs; side geometry; drop targeting; tab-strip
  overflow and search; group and tab menus; the gesture-cancellation signal.
- Forbidden: host calls, profile I/O, panel internals, domain resource lifetime, colour literals. Persisting
  the state belongs to the window's `WindowProfile` (see [State/SPEC.md](../State/SPEC.md)); ending a
  terminal's shell belongs to the window when a terminal tab leaves every view.

## Tool catalog

The tools a layout can name are an input, not a closed list: `LayoutSession.Tools` is core's five
(`LayoutSession.CoreTools`) followed by whatever the workbench composes in with `SetExtraTools`, today the plugin
tools the roster declares. Names, icons, default side, the reveal menus (`ShowTool_<id>`), preset restore and
`RestoreTool` read it; a change re-renders the surface only when the composed list differs. `IsValid` accepts
core's ids and any `plugin:<id>:<tool>`, so a persisted tab from a plugin that is off, or since removed, keeps its
slot and renders whatever the window supplies for it (a dormant placeholder). `DockSurface.TabIcon` and
`TabAdornment` let the window decorate a tab without the docking layer knowing why, `CenterActions` appends the
window's strip actions after New terminal in every center group, and `RefreshEmptyContents` re-renders only empty
groups' content, keeping every tab's chrome.

## State contract

One frame belongs to a window, not to a workspace. `DockState` carries stable group ids, the center binary
tree, left/right groups with weights and folds, side widths, region
visibility, singleton-tool placement, tool restore targets and the window's side group limits.

`DockState.Workspaces` holds one `WorkspaceView` per visited workspace (keyed by workspace root, with
Project Home keyed per project). A view carries document and terminal membership and order per frame
group, preview identity, selection per group, last-focused center/auxiliary/any group, and resource
positions relative to tools. Tools are frame state: a group that shows Specs shows it in every workspace.
When center tabs live in Projects, only the active workspace's selected tab in its last-focused center
group carries the accent marker. Per-group selected boxes and pane bubbles remain visible; focus changes
move the marker without rebuilding tabs, and inactive workspace previews have no active marker.
The Projects accent sits inside the tab row, clear of its rounded corners, rather than beside its edge.
That same focused tab has a theme-derived accent wash, distinct from the neutral workspace and
per-group selection fills. Pane members retain individual active-tab emphasis inside their shared bubble.
Switching workspace carries the previous view's tool selections into the next one, never its selected
resource.
The surface keeps its chrome mounted across a transition that leaves the frame unchanged, such as a
workspace switch: only groups whose tabs or panes differ are rebuilt, and content already shown stays attached.

Every transition runs on a copy and installs only if the result validates, so a failed or illegal command
changes nothing. A frame command (split, new/remove group, preset, tool move) rewrites every retained
workspace view in the same step, so no view can reference a removed group. A resource command changes only
the active view. `Epoch` advances with each installed transition and invalidates drag drafts and delayed
preview-keep work captured against an older base. Pointer and resize drafts stay runtime-local until they
commit once.

Empty groups are valid in any workspace; closing the final tab keeps the group. Only explicit remove or
merge deletes a group, and at least one center leaf always remains. Before a transition removes documents
from any view, the window may veto it (`CanRemoveDocument`); `RemovalBlocked` hands it the blocked tabs and
a retry, which is how closing a busy terminal asks first.

`DropWorkspace` removes the view of a workspace that no longer exists. It asks nothing, since the
workspace's documents went with it, and it never drops the active view: the window leaves a removed
workspace first and the view goes once another is active.
`PruneWorkspaces` does the same for every view whose workspace the host's registry no longer lists (removed by
another client or while the app was closed, which `DropWorkspace` never saw). The window calls it on each
registry broadcast and after mounting a workspace; it keeps the active view, and does nothing while the
registry is empty (not yet synced). A `WorkspaceView` holds only layout models (tab and pane records and
ids), no live controls, so a stale entry costs memory and profile size rather than UI trees. `ProjectPanes`
(keyed by project, a few strings each) is not pruned. `--workspace-views` checks a create/remove loop.

## Layout grammar

- Center: exactly one group. The main column has no custom splits (no split menu items, drop targets or
  `NewGroup`/edge moves there); tabs combine only as tab groups ([panes](#tabs-in-projects-and-tab-groups)).
  `CenterNode` stays a tree for persistence: `DockState.MergeCenter` folds a legacy split, on profile, host
  preset and session load and when a preset applies, into its first leaf. Every view keeps each tab once in
  tree order with its tab groups; the focused split's selection and focus move to the survivor, and a second
  preview stays open as an ordinary tab.
- Auxiliary eligibility: Projects, Specs, Files and Review are singleton tools, auxiliary-only. Changes is
  Files' Changed mode (ThinkRail #779); a persisted layout or preset naming the retired `changes` tool loses that
  tab on load and keeps the rest of its arrangement (upstream resets such a layout instead).
  Documents are center-only. Terminals may sit in the center or any auxiliary region. Hiding a tool keeps its
  restore group and position; Show restores it there and selects it.
- Tools and terminals never share a pane ([Panes](#tool-and-terminal-panes)).
- Left/right: ordered vertical stacks beside persistent 40px edge rails ([Rails](#edge-rails)). Expanded
  bodies keep a 120px minimum; a folded pane occupies nothing inside the stack, keeps its weight and lives
  only on its rail. A side with no expanded pane takes no width. A side is at least 220px wide (at least 8%
  and at most 35% of the frame, `SideGeometry.MinPaneWidth`), by the separator and on restoring saved widths.
  Dragging an outer separator below half that minimum hides the side and keeps its last expanded width; the rail brings it back. Saved widths stay
  ratios of the frame between the rails.
- There is no bottom zone, alignment, resize handle or toggle shortcut. Legacy frames migrate nonterminal bottom documents to center and discard bottom terminal placements.
- Side resize projects through left, center and right groups. Only the dragged side ratio is committed;
  compressing an untouched neighbour and viewport constraint are local to the projection.
- Limits: sides default to six groups each, 1–32 and window-local. Existing overages survive;
  creation is unavailable until below the limit, while reorder and joins stay legal.
- Small viewports compress and clip locally; ratios and topology are never rewritten because the window is
  small.

## Opening and attention

Ordinary opens target the active view's last-focused surviving center group. Reopening a resource selects
its existing placement and refreshes its metadata instead of duplicating it. Each center group has one
preview slot: a preview replaces it in place, Keep promotes one way. A single click waits the 250ms
double-click window, and the upgraded gesture emits only the final keep while still claiming the preview
slot. With the app's preview preference off, the window reads every open as a keep where the intent enters
(`BrowseDocumentAsync`, `OpenDocumentAsync`), so no slot is claimed and nothing waits.

Each center group has a navigation clock kept by the window (`DocumentNavigation.cs`). A user open advances
it at request time and carries the stamp to completion; a completion whose stamp was overtaken places its
tab without stealing focus, and one whose group vanished reroutes to the current last focus. Passive
restoration does not advance clocks.

## Body isolation

A group's body host (`DockPanel`) is its region's error boundary. A body that throws while the window
builds it, or during its measure or arrange, is replaced there by a notice naming the error; sibling
groups, chrome and the window carry on, and the region shows the next body that works. Exceptions raised
while painting are outside it.

Gesture projections are local: a tab drag, its drop preview, its cancellation and a resize preview detach
no body and replace no tab strip. Selecting a tab swaps only that group's body; focusing touches nothing.
A committed transition rebuilds chrome and re-mounts the same body instances the window caches.

## Arrangement and accessibility

A tab drag paints exactly one result: strip insertion, side before/after
group or restoring a hidden side. Touch dragging requires a 500 ms hold
before movement; moving earlier cancels the draft so the Projects rail can scroll. Mouse dragging retains
its immediate movement threshold. Only the pointer that pressed the tab may move or release its draft.
Within a strip the tabs are the only visible
targets: each half inserts before or after its tab with a 2px line. The rest of the strip still appends
(and catches a disabled self-insertion), showing the same line after the last tab; only a strip without
tabs is framed. Unlike upstream there is no separate append block or strip-wide frame, which read as three
overlapping regions. Overlapping targets are ranked by the mean distance from the pointer to their four
corners. Illegal domains, limits and no-op positions paint nothing and commit nothing. Every valid
target shows a subtle accent hint and the hovered one the real destination; decoration never alters
hit-testing. Escape, lost capture, an outside drop or a replacing transition cancels; a cancellation
caused by a layout change mid-gesture is announced once (`GestureNotification.cs`). A drag moves one
resource or one tool and never copies or crosses workspaces. A terminal tab moves only within its own
workspace: over another workspace's tab list or row, or a project row, in Projects a terminal drag offers no
destination (not even the Projects pane's own before/after targets), shows the not-allowed pointer and,
released there, leaves the tab in place (`DockSurface.ForeignWorkspace`); the pointer also says not-allowed
anywhere else a terminal has no destination. The only way a terminal changes workspace is the
user-approved `workspace_emigrate` ([Terminal/SPEC.md](../Terminal/SPEC.md#tab-lifecycle)).

Visual refreshes caused by a plugin catalog or decoration change keep the pressed tab alive until
its pointer release; after the click selects it, apply the latest chrome and retain keyboard focus
on the same control or its equivalent named tab. A real layout mutation still cancels an incompatible
active gesture.

Adding or removing entries in the tool catalogue leaves existing tabs and their focus intact when
their displayed metadata is unchanged. Side group header menus (right-click) lead with Show <tool> entries for hidden tools and, on a tool pane,
New terminal pane below; opening the menu reads the current catalogue and excludes tools already placed anywhere
in the frame. A change to metadata of a displayed tool refreshes its chrome.

Pointer is never the only path. Tab, group and header menus cover keep, close/hide, reorder, move to
another group, side above/below groups, new pane before/after, remove group, fold, region visibility and tool restore; Alt+Shift+Left/Right reorders, Left/Right/Home/End rove, Delete or Mod+W
closes, separators resize from the keyboard and Escape restores their origin. Ctrl+F6 cycles visible
groups. Tab strips are 32px with an accent underline on the active tab, bounded widths, hidden scrollbars
active reveal and edge fades only where clipped; a folded group's restore control is its focus endpoint; overflow search appears only while the strip is clipped, as a 288px dropdown under its
button with path-aware filtering and keyboard selection (`TabSearch.axaml`). Singleton tools have no inline
close glyph. An unfolded strip shows a terminal button (tooltip "New terminal in this group") when empty or when a resource tab is selected. Pane headers have no add button; side tool panes use the plain header of [Edge rails](#edge-rails).

## Tool and terminal panes

A group holding tools is a tool pane; terminals live only in tool-free groups (`DockState.CanJoin`). Joining a
tool into a pane that shows terminals, or a terminal into a tool pane, is unavailable, so the drop paints
nothing and the menu does not offer it; creating a new pane is always fine. Removing a center group returns keyboard focus to the surviving center group, including when its action
was invoked from the strip nested in Projects. Removing a pane merges its tabs
into the nearest sibling (previous, then next) that can take them all, else Remove is unavailable.

Every transition normalizes retained workspace views with the same rule (`DockState.SeparatePanes`): a
terminal whose pane became a tool pane (a tool joined it from another workspace, or a preset placed tools
there) moves to its region's nearest tool-free pane, then the last-focused centre group (`TerminalTarget`),
selecting and unfolding nothing. Passive catalog placement and preset remaps use the same compatibility
predicate; a workspace opens without an implicit terminal. A persisted layout that still
has a mixed pane migrates the same way when it is loaded (`MigrateLegacyTools`), and the mixed-order anchors
(`WorkspaceView.BeforeToolByTabId`) are accepted and discarded.

New terminal from a tool pane ("New terminal pane below" in its add menu) creates a tool-free pane directly
below it and places the terminal there; a terminal or empty pane adds to itself. Showing an empty region only
offers creation: nothing starts a shell until a terminal tab is selected in a visible pane.

## Edge rails

The left and right sides have persistent 40px rails (`DockRails.cs`), present whether the side is shown,
hidden or all folded. A rail mirrors its region's panes: one cluster per group in order, separated by a
hairline; a tool pane has one entry per tool, a terminal or empty pane one entry; tools placed nowhere form a
trailing dimmed cluster, and activating one restores it to its recorded target. Exactly one entry per expanded
pane is pressed (its frame carries the selected fill and an accent edge) and none in a folded or hidden one.
Activating an unpressed entry reveals the region, unfolds its pane and selects it in one transition
(`LayoutSession.ActivateEntry`); activating the pressed one folds its pane. Entries are pressed (toggle)
buttons in a single-tab-stop toolbar: arrows and Home/End rove, Enter/Space activates, Delete removes a tool
from its pane as its menu's Close does; a terminal pane's entry navigates and never closes a session. An
unchanged projection keeps the rail controls mounted.

A side tool pane shows a plain 32px header instead of a tab strip: the selected tool's icon and title, which is
a drag handle for that tool, then an add menu (hidden tools of that side, New terminal pane below) and Hide.
Terminal and empty side panes keep their strip with Hide in place of the fold button. Hide folds the pane onto
its rail and moves focus to its entry; unfolding from the rail returns focus to the pane. A folded pane's rail
entry is its focus endpoint for Ctrl+F6. Mod+B hides or shows the left side the same way.

On a rail, an entry's upper and lower halves insert before or after it in its pane (reorder, or join from
anywhere), and each cluster boundary (rail start, between clusters, rail end) creates a pane at that index
(`MoveToNewGroupAt`). Both paint only when legal: a boundary needs room under the limit and is refused as an
exact-position no-op for a pane's only tab, and a join obeys the pane rule. A hidden or folded side's rail is a
broad drop target that reveals the side. Explicit drops reveal and unfold their destination in the same
transition; passive reconciliation keeps folds. Menus name a pane by its contents (`Move to right pane
(Changes, Review)`, at most three names then +N, `(empty)`) and offer only destinations that can take the tab.

## Project Home

A window at a project's home (`home:` view) replaces the frame's left side and drops its bottom: its left
rail holds one Projects entry, the pane beside Welcome shows the Projects tool under a plain header with Hide,
and the left separator resizes it. Its visibility and width are `DockState.HomeProjectsVisible` and
`HomeProjectsWidth`, independent of the frame's left side; Mod+B, the entry and Hide toggle it, and the side
geometry reads them through `GeometryState`. The frame's right side and its rail stay, since the Specs tool
works at Project Home (upstream shows no right rail there). Project Home places no initial terminal. Moving
between Project Home and a workspace reparents the one Projects panel between the two hosts without
rebuilding it.

## Presets

Balanced, Focus and Review are built-in frame definitions (`DockState.Preset`). Balanced: left 18%,
right 28% with Specs/Files over Review at 1.25:1. A workspace
whose spec graph comes back empty opens that rail on Files instead: once per workspace, when the graph
arrives, a group still on its seeded first tool Specs moves its selection to the next tool without taking
focus. Specs stays docked, and anything the user selects afterwards is theirs. Focus hides
every auxiliary region; Review swaps the right stacks. Custom presets use
the same grammar, capture geometry, topology, tools, folds and empty slots but never workspace resources,
and are shared through host state. Applying a preset replaces the frame and redistributes each view's
documents and explicit terminals into its center group; tools a preset omits receive
restore targets. The window's default preset is the Reset frame target and is never reapplied on
workspace switch.

## Workspace switching

Switching workspace changes resource contents and view attention, never frame topology, tool placement,
geometry, folds or visibility. The dock chrome keeps its controls; only bodies swap, and bodies
are keyed by workspace and tab id so one workspace's terminal or editor never appears in another. A
first visit creates an empty view without placing, reserving or starting a terminal.
Terminals and agents start only through explicit launches.
Asking for the workspace already shown and mounted (a rail row, a location-bar entry, a tab under it) is a
no-op: nothing unmounts, reloads or redraws. A switch still in flight or a failed mount opens it again.


Previously mounted tool bodies remain visible while routing resolves. Leaving a mounted workspace also
keeps its center bodies and tab strips until routing resolves, unless that takes longer than 100 ms, when
the target's strips and loading bodies replace them. Workspace-scoped plugin tools and
center actions return with their existing controls. Paired panes retain their frame and splitters when
member tab ids and orientation are unchanged; refreshing a member replaces only changed content.

## Location

Refreshing empty groups uses the same region-aware content factory as initial layout: center groups
show the workbench receipt or Welcome; auxiliary groups keep their empty-group placeholder. Plugin
action updates must not introduce extra Welcome pages in the rails.

A window's location is Welcome, a project's Home, or a workspace. It is window-local, persisted in the
window's profile entry as the last project, workspace root and whether Home was shown, and never stored by
the host as an active location. Restore validates it against the host: an unreachable project falls back
to Welcome, a vanished workspace to its Project Home, and a restore that fails because the host is
unreachable keeps the remembered location and retries after reconnect. Removing a workspace from this
window returns to the previously selected surviving workspace; a removal observed from another client
returns to Project Home with a notice.

## Terminal placement

Terminal tabs are workspace-view resources. The tab id names a host session derived from workspace root
and tab id, so the session survives view rebuilds and other windows showing the same tab reattach to it. A
terminal body is created only when its tab is selected in a visible, unfolded group; hidden tabs keep
their session without a body
until shown. New terminal from a group lands in that group and reveals its region; from a tool pane it lands in a new tool-free pane directly below.

File-backed tab menus (editor, Markdown, PDF, image, diff) offer Copy path (workspace-relative) and Copy absolute path, matching
the Files tree; terminal and tool tabs offer neither. They copy the host-side path through the window clipboard.

Terminal tab context menus include Rename in Projects and side tool strips. The name prompt
starts with the current title; cancellation, blank and unchanged names leave it alone. Accepted names
persist in host state and broadcast to other windows, surviving terminal-catalog reconciliation.
Enabled plugins add items after Rename, in the tab strips, rails and Projects (Plugin API W26): the items are rebuilt as
the menu opens, and a plugin that names none for a terminal leaves nothing. A phone has no tab menu.

Terminal membership is the host's catalog, not a window's ([Terminal/SPEC.md](../Terminal/SPEC.md#shared-terminals)).
Explicit terminals have unique tab keys shared through the host catalog; the legacy key
`terminal:initial` remains readable. `LayoutSession.ReconcileTerminals` makes a workspace view's terminal
tabs those of the catalog, for the active view or a retained one: a tab the catalog lacks is removed without the
removal veto (its shell was ended by whoever closed it), its group selecting the neighbour as a close does; a tab
the view lacks is appended to the last-focused centre group,
without selecting it, revealing or unfolding its region, changing frame geometry or taking focus; titles follow
the host's saved agent title when present, otherwise the catalog. Catalog refreshes cannot replace an agent
title with its original numbered title, including retained inactive views and newly placed tabs. The view's next
terminal number moves past every numbered title a peer used. A tab created here
is exempt until the host has recorded it. `WorkspaceView.TerminalsShared` records that a view has met the catalog,
so a view saved before the catalog existed brings its tabs once instead of losing them. Upstream places only the
initial terminal passively and leaves other peer-created terminals out of a surface's layout; here every catalogued
terminal is placed, because a second device is expected to show the first one's terminals.

Android's nested tab strips in Projects suppress pointer-hover fill changes, including
pane bubbles. Active selection and focus markers remain visible, and touch close
buttons remain available; terminal closing asks for confirmation through the window.

Every client lists shared terminal tabs under their workspace in Projects, including
workspaces the window has not opened. Choosing a listed terminal opens its workspace
and reveals its group without taking the shell from its holder. Desktop clients keep
their normal tab-strip placement; this list also exposes terminals in hidden regions.

Every client keeps centre tabs under their workspace in Projects. Catalog terminals arrive in the
last-focused centre group; terminals in hidden side regions move there too. Other workspace rows also list shared terminals this window has not placed;
choosing one opens its workspace and selects the terminal once reconciliation has placed it.

A close request from the window (`LayoutSession.RequestClose`, driven by the app command owner) closes the
selected (else first) tab of the keyboard-focused group, otherwise of the last-focused center group, through
the normal close path including busy-terminal confirmation. A tool tab, folded group or hidden region yields
no target and no further fallback. The tab button no longer handles Mod+W itself; Delete still closes it.
When a tab button holds keyboard focus, the close returns focus to the group's newly selected tab, as Delete
does, so reordering and roving continue from the keyboard.

`LayoutSession.Close` raises `TabClosing` with a `ClosedTab` (workspace, group, index, tab, pane) once a close
of a document or terminal tab is certain, before `Changed`, including a close retried after the removal veto.
`Reopen(ClosedTab)` puts it back in the active workspace: its group (a side group only for a terminal and
never beside tools; else the focused centre group), its index clamped, and its pane when the other members
remain and belong to no other pane; it selects and reveals it. Undo close builds on these
([Terminal/SPEC.md](../Terminal/SPEC.md#undo-close)).

Changes review tabs use the ordinary resource preview/keep placement, persisting only identity and
scope. Body reads start when the mounted review requests them, outside layout hydration. Review progress
belongs to the window state owner; see [State/SPEC.md](../State/SPEC.md).
Returning to a continuous review keeps its loaded sections and scroll position visible while a pending
refresh rereads changes. Identical diffs keep their editor controls and selection; changed rendered
content uses the diff's existing in-place refresh. Lazy offscreen eviction waits for a valid laid-out
viewport rather than treating a newly attached tab's transient geometry as offscreen.

## Not yet ported

- Keeping panel groups and mounted bodies in place through fold, visibility and topology changes,
  applying committed geometry without rebuilding surviving chrome. Workspace identity must fence
  pending focus, drag and resize work even when both workspaces use the same tab id; an invalidated
  resize restores committed geometry before paint and ignores the remaining gesture until release.
- Dropping normalized no-op focus/selection writes before notifying observers, and committing document,
  attention and singleton-tool fan-out together. Body-local review state must invalidate only that body,
  preserving sibling chrome and bodies during review progress writes.
- Dropping a removed workspace's view from the frame and ending its terminals.
- Resize handles that paint a one-pixel line at their leading edge and extend their hit area only into the
  following panel, so the preceding panel's trailing scrollbar stays usable.
- Back/Forward navigation history over locations, and serializable deep links to a project, workspace or
  resource.

## Tabs in Projects and tab groups

On touch clients, including Android phones and tablets, tab labels leave gestures to the Projects scroller.
Each live or retained tab has a 44-pixel Open button that selects its workspace and tab and returns
to the current content page, including when that tab was already selected. Close actions and tab
context menus remain available; normal desktop rows retain whole-row selection and dragging.

Projects is the only home for centre tabs on every client. Restored tabs appear there while host
routing is pending, using the saved active view until the host supplies its project/workspace rows.
The live tab controls survive that transition. Hiding, folding or covering Projects
never adds a strip to the editor. Tab-mode and column-width settings are retired; legacy host
and profile fields have no effect on placement. `DefaultPaneDirection` remains client-local.
The centre is one group and offers no splits from its menus or by dragging; side-by-side viewing is a tab
group (pane). Its tabs stay in Projects.

Nested strips retain their centre group as keyboard owner; their containing tool panel cannot take
Close Tab or group-navigation commands. Rows use an active marker, top/bottom insertion quarters and
start actions below their tabs.
Ambiguous basenames show their folder on a dimmed second line.

- Panes: `DockPane` is metadata on a centre group in the workspace view (`WorkspaceView.Panes`), never a
  centre-tree node: two to four tabs of one group shown together, in columns or rows, with one weight each.
  `LayoutSession` re-derives panes after every mutation: a member closed or moved away leaves no membership,
  a pane below two members dissolves, and members are pulled into one contiguous run anchored where the first
  member sat, in the pane's order. A preview opened over a member takes its place. Joining an existing pane
  keeps its direction and its members' proportions, the newcomer taking an equal share; membership is
  exclusive. A member dropped inside its own run reorders the pane, a drop past it takes the member out, and
  a drop that changes nothing is refused. Making a pane is a vertical-strip gesture: the band between a
  row's insertion quarters, or "Show beside/under <neighbour>" for the immediate neighbours in the tab menu.
  Managing one works in either orientation ("Move up/left in this group", "Stack this group" / "Put this
  group in columns", "Show on its own"). Selecting any member shows the whole pane; its dividers commit weights when a drag ends.
- Each centre group's strip renders nested under the
  active workspace's row in Projects and the centre keeps only its editor. Plain directories place
  those tabs directly beneath the directory name, without a workspace row. Nested rows highlight on hover,
  the selected one draws a rounded bordered box, and a pane is one bubble with a continuous left accent.
  Other workspaces list their retained tabs read-only under their rows; choosing one selects it there
  (`SelectIn`) and switches to that workspace.

Inactive workspace previews refresh their plugin icons and badges when host agent identity
changes, even when the active workspace's decorations are unchanged. Preview caching includes
decoration results, so unchanged rows stay mounted and Codex/Claude marks follow workspace switches.
Active and inactive Projects tabs use the same row heights, title typography and duplicate-name
folder subtitles. While routing a workspace switch, Projects retains its outgoing rows until the
destination identity and strips are ready; intermediate centre loading views never replace its tabs.
Workspace selection backgrounds transition over 120 ms without animating row geometry.

## Phone pages

A compact workbench (`Workbench.Compact`, set by the Android client on phone-sized screens) sets
`DockSurface.Drawers`. The frame model is unchanged; only its projection differs:

- The centre keeps the whole width. There are no collapsed side rails or side separators.
- A visible left region is the Projects page: the Projects tool alone, over the whole workbench, with no
  tool tab strip. A visible right region is the Tools page: `DockDrawers.cs` lists every other side tool,
  whichever side group holds it, as a section header and shows one of them in the height the headers
  leave; choosing a header shows that tool and stays on the page. The list is every tool the workbench
  has, seated in a group or not, so plugins' tools are there before any group holds them.
- The window (`Drawers.cs`) shows a bar at the bottom with Projects, the current tab (Terminal or Editor,
  by what the centre shows), Tools and Settings; the header keeps the location and the connection status
  and loses its Settings button. It starts on the current page, returns to it when the centre's selected
  tab or the workspace changes and on `BackRequested`, and hides the bar while the soft keyboard is open.
  The three workbench pages lie side by side and move together: a finger travelling sideways drags the
  neighbour in beside the page it leaves, which stays when carried a third of the way or flicked and goes back
  otherwise; a page chosen from the bar slides in the same way, Projects to Tools straight across. A touch that
  sets out vertically is left alone.
- The three pages are persistent hosts (the centre region and the two side regions, siblings in the shell):
  a page switch changes their visibility and position, never their contents, so the centre's controls, scroll
  positions and focus survive a visit to Projects or Tools, and focus inside a page returns to it when it shows
  again. A side page is built the first time it shows (never at startup) and again only after a structural
  rebuild or, for Tools, when its sections change. Only the page on screen, and the one sliding with it, is
  visible: hidden pages take no touch, focus, frames or automation. The slide is Avalonia's `PageSlide` driven
  through `IProgressPageTransition.Update` (finger progress while held, a 260 ms cubic ease-out to the end or
  back on release, then `Reset`). Each frame gives both pages a new `TranslateTransform` before `Update` sets
  it: a transform changed in place left the pages unmoved on Android, so the slide showed only its end. The
  page on its way out of an unheld slide is not hit-testable, and a held drag stops gesture recognisers under
  the finger from taking the touch. A switch that interrupts a slide
  turns it round when it goes back, and otherwise starts from the page most on screen.
  Settings is the page after Tools but a window of its own, so nothing is dragged: a sweep past Tools opens it
  when the finger lifts, and a sweep back over it closes it.
- A tab group (pane) on the current page shows one member over the whole centre under a strip of chips
  (`PhonePaneView`), one per member by tab title, the shown one lit. Tapping a chip, or a finger travelling
  sideways over the member, brings another member in with the pages' slide (`PageSlide`, held under the finger,
  260 ms cubic ease-out on release, kept past a third or on a flick); a drag with no member that way is the
  pages' drag as before. Which member shows is the phone's own (`DockPhonePanes.cs`): the shared members,
  direction, weights and group selection are not changed or sent, the phone follows the group's selection only
  when it changes, and only the shown member's content is in the tree, so a hidden terminal is neither attached
  again nor resized by the phone and a watched one stays watched. `DockSurface.NoteActivity` marks a hidden
  member's chip with a dot for new terminal output (renderers implementing `ITerminalOutput`; a member never shown
  on this phone has no view to print), an agent's attention request or a Visualize revision for that tab; showing
  the member clears it. Panes exist only in the centre, so the Tools page has no chips; ungrouped tabs are unchanged.
- A touch workbench (`Workbench.Touch`) always draws a tab's close button, which a pointer's hover reveals
  elsewhere.
- Settings in a compact workbench opens on a list of its sections with a line about each; choosing one
  gives it the whole page under its name, and Back or the header's arrow returns to the list. The page
  stops above the page bar (its `MaxHeight`), is not dimmed, lights the bar's Settings item, and closes
  when another item is chosen.

`DrawerChecks` (in `-- --shell`, and alone in `-- --drawers`) covers this headlessly, including control and
focus persistence, finger-following, revert, interruption, Projects to Tools and hit-testing of hidden pages;
`DrawerChecks.TabGroups` covers a three-member group's chips, chip and swipe switching, the untouched shared layout,
hidden terminals and the activity dot. Dragging tabs between regions is not offered here.

Core includes the Code Intelligence tool (`lsp`, right by default), restored and moved through the same tool catalog
and persisted frame as Files and Review. Existing presets retain their current selected tools.
