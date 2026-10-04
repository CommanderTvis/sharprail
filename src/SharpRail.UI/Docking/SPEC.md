# Docking — window-local workbench frame

Upstream: apps/web/src/shell/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/layout/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/layoutIntents/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/layoutState/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/shell/terminalReconciliation/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))
Upstream: apps/web/src/navigation/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

## Responsibility

The workbench engine of one window: the frame and per-workspace view model (`LayoutState.cs`), legal
atomic transitions over it (`LayoutSession`), and its rendering, resize, drag and keyboard arrangement
(`DockSurface` and its partial files). It renders containers; panels, documents and terminals are
supplied by the window through a content callback and never learn where they are placed.

## Boundary

- Owns: `DockState`, `DockGroup`, `CenterNode`, `WorkspaceView` and `DockTab`; validation
  (`LayoutSession.IsValid`); built-in presets; transitions and their atomic installation; the projection
  from frame plus active view to rendered groups and tabs; side/bottom geometry; drop targeting; tab-strip
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
tree, left/right/bottom groups with weights and folds, side widths, bottom height and alignment, region
visibility, singleton-tool placement, tool restore targets and the window's side/bottom group limits.

`DockState.Workspaces` holds one `WorkspaceView` per visited workspace (keyed by workspace root, with
Project Home keyed per project). A view carries document and terminal membership and order per frame
group, preview identity, selection per group, last-focused center/auxiliary/any group, and resource
positions relative to tools. Tools are frame state: a group that shows Specs shows it in every workspace.
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

## Layout grammar

- Center: a horizontal/vertical binary tree of at most four leaves; a split halves one leaf. Interactive
  split and resize keep each pane at least 320px wide and 180px high. Remove promotes the sibling and
  moves the removed leaf's documents into it in every view.
- Auxiliary eligibility: Projects, Specs, Files, Changes and Review are singleton tools, auxiliary-only.
  Documents are center-only. Terminals may sit in the center or any auxiliary region. Hiding a tool keeps its
  restore group and position; Show restores it there and selects it.
- Left/right: ordered vertical stacks. Expanded bodies keep a 120px minimum; folded groups take 27px and
  retain their weights. Dragging an outer separator below the 4% midpoint hides the side and keeps its
  last expanded width, which the full-height restore rail brings back.
- Bottom: ordered left-to-right groups. Height starts at 30%, caps at 70%, and snaps closed below half its
  minimum (120px body plus the 27px header). Hidden, it reserves no space and has no rail; while an
  eligible tab is dragged a 24px drop zone appears over the span that bottom alignment selects, and
  Mod+Shift+J restores it directly. Alignment is center, center+left, center+right or full; a side outside
  the span owns its lower corner.
- Side resize projects through the alignment's nested groups. Only the dragged side's ratio is committed;
  compressing an untouched neighbour and viewport constraint are local to the projection.
- Limits: sides default to six groups each and bottom to three, each 1–32 and window-local. Existing
  overages survive; creation is unavailable until below the limit, while reorder and joins stay legal.
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

A tab drag paints exactly one result: strip insertion, center half split, side before/after
group, bottom before/after group, or restoring a hidden region. Within a strip the tabs are the only visible
targets: each half inserts before or after its tab with a 2px line. The rest of the strip still appends
(and catches a disabled self-insertion), showing the same line after the last tab; only a strip without
tabs is framed. Unlike upstream there is no separate append block or strip-wide frame, which read as three
overlapping regions. Overlapping targets are ranked by the mean distance from the pointer to their four
corners. Illegal domains, limits and no-op positions paint nothing and commit nothing. Every valid
target shows a subtle accent hint and the hovered one the real destination; decoration never alters
hit-testing. Escape, lost capture, an outside drop or a replacing transition cancels; a cancellation
caused by a layout change mid-gesture is announced once (`GestureNotification.cs`). A drag moves one
resource or one tool and never copies or crosses workspaces.

Visual refreshes caused by a plugin catalog or decoration change keep the pressed tab alive until
its pointer release; after the click selects it, apply the latest chrome and retain keyboard focus
on the same control or its equivalent named tab. A real layout mutation still cancels an incompatible
active gesture.

Adding or removing entries in the tool catalogue leaves existing tabs and their focus intact when
their displayed metadata is unchanged. Side groups update the availability of Show a hidden tool
in place; opening its menu reads the current catalogue and excludes tools already placed anywhere
in the frame. A change to metadata of a displayed tool refreshes its chrome.

Pointer is never the only path. Tab, group and header menus cover keep, close/hide, reorder, move to
another group, directional splits, new pane before/after, remove group, fold, region visibility, bottom
alignment and tool restore; Alt+Shift+Left/Right reorders, Left/Right/Home/End rove, Delete or Mod+W
closes, separators resize from the keyboard and Escape restores their origin. Ctrl+F6 cycles visible
groups. Tab strips are 32px with an accent underline on the active tab, bounded widths, hidden scrollbars
active reveal and edge fades only where clipped; a folded group's restore control is its focus endpoint; overflow search appears only while the strip is clipped, as a 288px dropdown under its
button with path-aware filtering and keyboard selection (`TabSearch.axaml`). Singleton tools have no inline
close glyph. An unfolded strip shows a terminal button (tooltip "New terminal in this group") when empty or when a resource tab is selected. Selecting a tool such as Projects hides it, including in mixed groups. A side strip also shows an add menu of its hidden tools when any exist.

## Presets

Balanced, Focus and Review are built-in frame definitions (`DockState.Preset`). Balanced: left 18%,
right 28% with Specs/Files over Changes/Review at 1.25:1, bottom 30% with one empty group. A workspace
whose spec graph comes back empty opens that rail on Files instead: once per workspace, when the graph
arrives, a group still on its seeded first tool Specs moves its selection to the next tool without taking
focus. Specs stays docked, and anything the user selects afterwards is theirs. Focus hides
every auxiliary region; Review splits the center vertically and swaps the right stacks. Custom presets use
the same grammar, capture geometry, topology, tools, folds and empty slots but never workspace resources,
and are shared through host state. Applying a preset replaces the frame and redistributes each view's
documents across the new center leaves and terminals into the bottom group; tools a preset omits receive
restore targets. The window's default preset is the Reset frame target and is never reapplied on
workspace switch.

## Workspace switching

Switching workspace changes resource contents and view attention, never frame topology, tool placement,
geometry, folds, visibility or alignment. The dock chrome keeps its controls; only bodies swap, and bodies
are keyed by workspace and tab id so one workspace's terminal or editor never appears in another. A
first visit creates its view and places one terminal, "Terminal 1", into the first bottom group, creating
one when the limit allows; closing it never brings it back.

Previously mounted tool bodies remain visible while routing resolves. Workspace-scoped plugin tools and
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
their session without a body, and a hidden bottom reserves the initial terminal without starting a shell
until shown. New terminal from a group lands in that group and reveals its region.

A close request from the window (`LayoutSession.RequestClose`, driven by the app command owner) closes the
selected (else first) tab of the keyboard-focused group, otherwise of the last-focused center group, through
the normal close path including busy-terminal confirmation. A tool tab, folded group or hidden region yields
no target and no further fallback. The tab button no longer handles Mod+W itself; Delete still closes it.
When a tab button holds keyboard focus, the close returns focus to the group's newly selected tab, as Delete
does, so reordering and roving continue from the keyboard.

## Not yet ported

- Reconciling with the host terminal catalog: terminal membership is shared across windows, a
  peer-created terminal is placed passively into a compatible slot without taking focus, and a host-side
  close removes the tab from every window. The host side exists (`ITerminalCatalogService`, its adapters
  and broadcast); no window reserves its tabs or applies the broadcast yet.
