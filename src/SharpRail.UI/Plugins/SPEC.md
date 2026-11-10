---
id: submodule-ui-plugins
type: submodule-design
status: active
title: Plugins — the app-side plugin runtime
depends-on: [module-plugin-api, module-plugin-ui-kit, module-host-abstractions]
references: [submodule-ui-panels, submodule-host-plugins]
parent: module-ui
---

# Plugins — the app-side plugin runtime

Upstream: apps/web/src/plugins/SPEC.md @ 4737df6d (CommanderTvis fork)
Upstream: apps/web/src/plugins/loader/SPEC.md @ 4737df6d (CommanderTvis fork)
Upstream: apps/web/src/plugins/registry/SPEC.md @ 4737df6d (CommanderTvis fork)

## Responsibility

Owns the edge between two parts. The registry is the state a plugin's UI half fills in and the workbench,
panels and Settings read back from; the loader is the composition root that turns the host's roster into
activated (or dormant) UI halves. See [the plugin API](../../SharpRail.Plugins.Api/SPEC.md) for the W1–W21
capability contract this implements. The runtime is app-owned: one per `Workbench`, created from
`Workbench.Plugins` (the host's `IPluginService`), shared by every window. Window-scoped capabilities (the
active workspace and editor, opening, revealing, notifying) act on the app's active window.

## Dependency graph

- The registry (`PluginRegistry.cs`, `PluginIcons.cs`) references the API assemblies and the UI kit. It is a
  pure leaf: no `Workbench`, `WorkbenchWindow`, `SharedState`, docking or host reference, so any of them can
  read it back without a cycle.
- The loader (`PluginLoader.cs`, `BuiltinPlugins.cs`, `PluginUIContext.cs`, `EditorEvents.cs`) references the registry, the API assemblies, `IPluginService`, `SharedState`, the
  profile, and the workbench's own operations it binds context members to. Only `Workbench` creates it, with
  the builtin array unless its composer hands it another list (the checks add their fixture this way).

## Boundary

State subscriptions hydrate a scoped snapshot before consuming pushes. A snapshot may return a single
payload or a list of payloads; list rows are individually filtered by the subscription's key fields.
An unscoped subscription to a keyed channel consumes pushes without requesting a snapshot whose
required scope is absent. Payloads that are themselves collections retain their collection shape.

- Owns: the registry (manifests and roster, the active set, one append-only, plugin-tagged list per
  contribution kind, and the read selectors the workbench consumes) and the loader (the builtin array, the
  `IPluginUIContext` binding, the editor-event emitter, and the roster-to-activation reconciler).
- The workbench reads registered contributions and icons and controls plugin loading through
  the runtime boundary; it does not reach into individual plugins.
- Forbidden: the registry reaching into the loader, `SharedState`, the host, or any window. Every live host or
  window edge is the loader's job, so the registry stays checkable with a fake roster and no app.

## The registry

- Tables are `Manifests`/`Roster`, `Active` (the loader's per-plugin activation flag), and one list per kind:
  `SideTools`, `SettingsSections`, `Companions`, `FileViewers`, `TabDecorators`, `Launchers`,
  `WorkspaceActions`, `ProjectActions`, `TerminalAccessories`, `FileIconSlots`, `DocumentLinkSlots`,
  `FileActions`, `SlotSections`, `HeaderItems`, `TerminalTabActions`. Every
  row is tagged with its plugin id. `RemovePlugin(id)` drops every row tagged with that id, across every
  table, in one write. Workspace and project actions are separate tables, so a reader never hands a
  project-scoped factory a workspace's arguments.
- Every table is an append-only list, not a dictionary per kind, so removal is one filter per table and
  "registration order, first eligible wins" (file viewers, tab decorators, slots) falls out without a
  second ordering index.
- Core registers its own image and Markdown viewers into the file-viewer table under the synthetic id
  `core`, after every plugin's, always with an explicit predicate, so the open path has one dispatcher.
- `ToolCatalog` reads `Roster[].Contributes.SideTools`, not the runtime side-tool table: a dormant plugin's
  tool tab must render (as a placeholder) even though its UI half never registered a control. It lists only
  plugin-declared entries in roster order; core's five tools stay owned by `DockState`, and the docking layer
  composes the two. A declared tool stays in the catalog, marked dormant, whether or not its UI half is
  active: `Active` and the row's `Status` both gate it, so a tool never blinks out of a layout. `SideTool(id)`
  is the other lookup, over the runtime table, for the control a non-dormant tab renders.
- `FileViewer(path)` resolves in registration order, first eligible entry wins: eligibility is the
  registration's own `Matches` when it supplied one, else the manifest's declared extensions and names for
  that plugin, read off the roster.
- `PluginIcons.Resolve(name, entry)` maps a Remix Icon name to a bundled icon (the kit's icon set carries the
  Remix glyphs core uses plus `puzzle-2-line`), draws `asset:<path>` from the plugin's assets through the kit's
  `SvgAsset`, and falls back to the puzzle glyph for anything unknown or an asset name on a plugin with no
  assets.

### Rebuild only on change

The registry raises one `Changed` notification per write, naming the tables it touched, and derived results
(the tool catalog, companions by availability, the launcher list, slot resolver lists) are computed once in
the write and stored, so reading them is a property read. A consumer refreshes only the region whose table
changed, and only when its projection differs from what it shows: rebuilding unchanged rows closes open menus,
drops focus, and sends clicks to detached controls (see `gotchas.md`). The same rule holds one level up:
`IPluginUIContext.WatchHost` compares each selection with `EqualityComparer<T>.Default` and calls back only when
it changes, never for the initial value.

## The loader

UI reconciliation is queued at background dispatcher priority, including when startup schedules it
from the UI thread. Activating UI halves must not run inline inside workbench construction. Manifest
and roster registration still precede reconciliation, preserving dormant tools and eventual activation.

- Builtin UI halves are a literal array in `BuiltinPlugins.cs`: a manifest and a factory for its
  `PluginUIModule` per plugin, including Spec Dialect, Blueprint, Claude Code and Discord. The manifests are registered before any roster arrives,
  so a persisted `plugin:<id>:<tool>` tab renders its placeholder with the right label and icon. Beside it,
  `BuiltinPlugins.Contracts` lists their contracts, whose JSON contexts the loader registers with `PluginJson`
  when it is built: a UI half may read its settings or call a method before anything has created its contract,
  as in an app connected only to a remote host.
- No assembly loads into the app at runtime. An external plugin's UI half is never loaded: the host refuses an
  enabled external plugin, and an external row a host reports active anyway stays dormant and logged, unless the
  workbench has `IsolatedPlugins` (null in `App.cs`, set only by the checks), which opens the UI half running in its
  own process on the host. Then the row's activation is pending at once, so the next pass sees it, while the
  session opens off the UI thread; on the UI thread, if the activation is still current, the module the session
  gives (`RemotePluginUIModule`) activates with the guard open, replaying what the plugin registered. An unmount
  cancels an opening session, and a session that opens after its activation ended is closed. A session that fails
  to open leaves the plugin dormant and logged until its row changes. A session the host ends (the process ended
  or was replaced) unmounts its activation and schedules a pass, so a row that is still or again active opens a new
  session even when the app never saw it leave `active`. The plugin's views are `RemoteSurface`s
  (shared by desktop, Android and Browser): each decodes a surface's tiles off the UI thread into a bitmap drawn 1:1
  at device pixels, applies moves before tiles, acknowledges each frame once drawn, pushes its size, scale and the
  kit theme, forwards pointer, wheel, key and text input, moves focus in on Tab or a click and on when the plugin's
  last element is tabbed past, sizes itself to what the plugin's control asks for, closes its surface after 2
  seconds detached and opens a new one when shown again, since the workbench keeps a tool's control while it is
  hidden, and clears its pixels at a keyframe. A window moved to a monitor of another scale pushes the new scale,
  and the plugin renders again at it. The plugin's popups (menus, flyouts, tooltips) are controls of their own in a
  canvas the surface adds to the panel that holds the app's overlays, over the cell of the child holding the surface
  and clipped to it, at that child's z-order and after it: they reach past the surface's slot but not past that
  cell, and stay under Settings, dialogs and toasts raised above that child. A popup never takes focus, so keys
  still go to the surface; a tooltip lets the pointer through. A popup shows only when it opens within 2 seconds of
  input to the surface or its popups; a dialog shows whenever it opens, since its frame names the plugin. A press anywhere else in the window, focus leaving the surface, the window
  deactivating or the surface detaching hides them all at once and tells the plugin; Escape while one that takes
  input shows stays with the plugin and hides the newest such one. A hidden popup never shows again under its
  number. A window the plugin opens shows in the same layer as a dialog, centered, in a frame the app draws that
  names the plugin (the seam passes its label) and the dialog, never larger than the layer, with a close button that
  removes it at once and tells the plugin, after which that number never shows again;
  the dialog takes focus and its own keys, a press elsewhere does not close it, and the rest of the app stays usable,
  since only the plugin's views wait for it. The accessibility tree the plugin reports for the surface, a popup or a
  dialog shows as peers under the control that shows it (a pane whose children are the plugin's nodes, at the places
  the plugin gave them), and the actions a client asks of them go to the plugin. The cursor the plugin reports over its surface or a popup shows there.
  While a text field of the plugin has focus (in the surface, a popup or a dialog) the surface or dialog offers the
  app's input method a `TextInputMethodClient` at the caret the plugin reported, asks the input method to look
  again whenever that changes, and sends the composition to the plugin; committed text arrives as text input. A surface the plugin
  breaks or the host ends shows why. On a remote
  host the session is the client adapter's (`RemoteIsolatedUIHost`), which keeps it across a dropped connection. This keeps
  the app free of dynamic code loading, so it can be published as NativeAOT, until external plugins can run
  isolated from SharpRail's processes. Activation runs on the UI thread.
- A plugin declares its contributions synchronously, during `Activate`. An activation guard is open only while
  `Activate` runs (closed in a `finally`, so a throwing activation still closes it), and every registration
  member checks it first and throws otherwise. This is what makes `RemovePlugin(id)` a complete cleanup: a
  plugin's whole surface exists when `Activate` returns. Observers a plugin starts through the context
  (`WatchHost`, `OnSettings`, `Subscribe`, `OnReconnect`, `OnWorkspaceRemoved`, `ObserveFileRevision`,
  `Editors.OnEvent`, `OnLaunchersChanged`) may be started later, and are also recorded against the activation and
  disposed when it ends, alongside whatever `Activate` returned.
- The reconciler runs from one serialized queue fed by every roster change (each `SharedState.Changed` whose
  `Plugins` differ, and every reconnect), so an unmount still running a plugin's disposer can never race a mount
  for the same id that a rapid toggle would otherwise trigger.
- A failed activation, or a wire-version mismatch, leaves the plugin out of the active set: dormant, logged, not
  thrown at the caller. For a builtin plugin the mismatch check compares its array manifest's `WireVersion` with
  the roster row's before anything loads. A broken plugin degrades to inert placeholders instead
  of taking the reconciler down and blocking every other plugin.
- `Settings<T>()` and `Host().AppSettings` read from the latest `HostState` snapshot through one projection;
  `UpdateSettingsAsync` sends a `plugin-settings` change and completes when a snapshot carrying the new value
  arrives. It refuses settings that name `enabled`: only the user turns a plugin on or off, and `Settings<T>()`
  never shows that key.
- Shown terminal keys include terminals displayed beside the selected editor in a pane, with the last-focused
  centre group first. Hidden tabs outside that selected pane do not become the configuration context.
- The workspace projection includes host-published catalogs and each mounted window's host-resolved
  project/workspace identity. A directly opened worktree is routable by plugin actions before a catalog
  refresh; this requires no Git scan or extra persisted workspace list.
- `OpenTerminalAsync` with a tab key already open in the target workspace selects that tab without changing it;
  an unknown key places a new terminal tab with that key in the requested or last-focused centre group and
  has the host run the command as the new shell's program (then an interactive shell in the same directory), so
  nothing is typed; the program runs once, and restarting the tab later opens a plain shell. Plugin-opened terminals are centre resources unless the caller names
  another group.
- Terminal accessory mounts survive shared-state updates while their registration remains the same;
  removing or replacing a registration unmounts only its control. This preserves an active picker,
  draft and session-only UI state while agent records and other projections change.
- `Subscribe` on a state channel is snapshot-then-stream: it calls the snapshot method the roster names with
  the scope as params on subscribe and after every reconnect, delivers the result, then delivers pushes; a push
  whose key fields disagree with the scope is dropped even if the host sent it.
- A `PluginCallException` with `Disabled` arriving while its plugin is being unmounted is expected and dropped
  rather than reported.
- The client key every call and subscription carries is one id per app run and host, so a publish addressed to
  a call's client reaches this app.
- `Invalidate()` re-evaluates the calling plugin's predicates and refreshes only the regions whose result
  changed.

## Where contributions render

- Side tools (W5): the tool body switch in `WorkbenchWindow` falls through to the registry for a `plugin:` id:
  the registered control inside a per-plugin host control, or a dormant placeholder ("*label* is off", with
  the manifest icon and a button to open Settings › Plugins). `LayoutSession.IsValid` accepts `plugin:` ids;
  name, icon, default side and the reveal menus read the composed catalog. Git-required tools follow the
  workspace capability policy in `submodule-ui-panels`, including restored tabs and direct reveal requests.
  When deferred Git discovery changes the
  catalog, rebuilding the dock retains keyboard focus on the equivalent named control. An open dock
  menu defers roster catalog refresh until it closes, then the window applies the latest catalog. Repository
  capability changes apply immediately, so an already-open menu cannot activate a newly unavailable tool.
- File actions (W21) are read from the registry as a context menu opens, so labels and availability are current:
  after a separator in a Files row's menu, as the code editor's whole menu (it opens only when one is offered),
  and beside Copy in the Markdown preview's. The editor and the preview pass the one-based source lines of the
  selection; without one the action is for the file.
- `Editors.OpenAsync` opens in the window showing the file's workspace, falling back to the active window.
- Attention notifications (W20) go to the workbench's `AttentionNotifications`, which decides whether and
  how they leave the app ([Away notifications](../SPEC.md)); the context adds nothing of its own.
- Settings sections (W4) are grouped under Settings › Plugins in `SettingsView`, indented behind a rule
  directly below it, as the fork's navigation nests them; Settings › Plugins is core's own section.
- Companions (W6) open beside a terminal tab in an embedded split inside the terminal's body, never as a tab of
  their own; the open companion per terminal is window view state. The pane has a top-right close
  button that clears that selection, leaving its terminal and its strip available to reopen it.
  A companion shows as a slim strip at the very top of the tab body, full width, above the transcript or terminal: its icon, the
  drawing's title and "· N visualizations", in muted text on the tab's own background with a divider below. Pressing the strip
  opens or closes the pane; with several items a chevron lists them and choosing one shows it in the pane. The strip exists only
  while the tab has something to show (no strip, no gap), and is the same on terminals and chat tabs; there is no chip.
  The pane's width is saved per tab with the open state, and below 560 px the pane covers the body instead of squeezing it. A pane belongs to its tab: it can be closed but not moved elsewhere (a drawing the agent detaches opens as its own tab instead).
  The companion frame clips its content to keep oversized plugin drawings inside its split.
  A plugin's focus request selects that companion in every window holding the named terminal, including when another window is active.
  Selecting a companion changes the embedded pane, preserving keyboard focus in the terminal or
  active window, as the fork's embedded-pane state action does.
  A plugin document registered with `HostsCompanions` (the chat tabs) is a companion host too: its body is wrapped in
  the same split (frame, splitter, close button, saved width, overlay and strip) and the selection is keyed by workspace plus the
  document id, so it survives restarts when the id does. The companions registered for terminals are offered there
  through the same `IsAvailable`/`Create` calls with the document id as the tab key; each registration's optional
  `HostChanged` hears when such a body mounts and when its tab goes away (terminals are never reported).
- File viewers (W7) are consulted by the document open path and by tab restoration; a viewer with read
  strategy `None` gets no text read, and a tab whose viewer has gone shows a placeholder offering to open the
  file as text.
- Tab decorations (W8) apply to every tab strip's tabs; the first non-null decoration wins.
- Launchers (W9) are offered in the create-workspace flow as "Start in a terminal with …" and by
  `OpenTerminalAsync`'s callers; an unavailable launcher shows its reason.
- Workspace-scoped actions (W10) render in the workspace's start-actions row beside New terminal; project-scoped
  ones on Project Home.
- Side-tool bodies and workspace actions are retained per window/workspace rather than recreated on
  navigation or unchanged host updates. Tools may opt into `Retarget` to serve every workspace with
  one control: repository history across worktrees of a project (Git Graph), and the Claude Code, Codex and omp panes across
  projects too. A retargeted control shows only the new workspace's data: work in flight for the old one is rejected by a
  generation check, drafts, search text, expanded rows and the surface selection are dropped (never written into the new
  workspace), the account surface (not workspace data) stays, and host watches are registered once, re-registered only for
  the new workspace. Pending routing retains the previous tool body; unregistering a
  contribution or closing its workspace/project releases its cache, as do appearance and window changes.
- Terminal accessories (W11) render as rows below the terminal surface, inside `TerminalView`; `Write` types
  into the Ghostty surface, `Focus` gives it the keyboard, and `HoldInput` holds the user's keys out meanwhile.
- Editor events (W12) are fed by the Scintilla editor (open, close, activate, save, selection) and the Markdown
  preview (selection).
- File icons (W17) are consulted for tabs, Files rows, Changes rows and the diff header's path chip; core's
  bundled file-type icon or generic glyph is the fallback.

## Settings › Plugins

`Panels/PluginsSettings.cs` is the core Settings section that manages the roster: one row per roster entry,
with icon, label, version (external plugins only; a builtin's version is the app's), origin, status, a reason
line for failed and refused rows, a contribution summary, a switch, Retry on failed rows
(`IPluginService.RetryAsync`), a Rescan button (`RescanAsync`), and an editor for the extra plugin roots (add
and remove, each change a `plugin-paths` host change). Rows sit under one heading per manifest
category (`PluginCategories.Order` first: the languages alphabetically, then the tool groups; other names alphabetically;
`Other` for plugins without one, last), by label inside a group, so a language's server and its environment plugin are
neighbours. Each row is its
own control keyed by plugin id, and a roster change updates rows and headings in place.

Enabling and disabling walk the roster's `DependsOn` edges rather than touching one row:

- Enable: the transitive `DependsOn` closure of the plugin, filtered to disabled entries. Empty: one
  `plugin-settings` change enabling it, at once. Otherwise a confirmation ("Also turn on X, Y?") first, and
  accepting sends one batch enabling the plugin and every listed dependency.
- Disable: at once (the host's reconciler cascades dependents off); the row names the enabled plugins that
  depend on it, the same walk in reverse, before the switch is used, so the cascade is never a surprise.

Both walks are pure functions over the roster, and the checks call them directly on constructed rosters.

## Checks

`tests/SharpRail.Checks/PluginUiChecks.cs`: the registry's removal and ordering rules on a fake roster; the two
dependency walks; the loader against builtin modules on a stand-in host (`E2E/FakePluginHost.cs`), which alone can
list builtin UI halves with another wire version or a throwing activation and push off-scope channel values;
activation outside `Activate` refused. The fixture plugin, composed as a check-only builtin on both sides, then
runs end to end through the real host, once in process and once remote over WebSocket RPC, beside two external
plugins in the host's state directory: one built for another generation, and one listed as external and
disabled, refused with its plain reason when switched on with nothing mounted, its switch still on, and back to
disabled once switched off from its card.
The fixture's dormant tool placeholder turns live with the tab instance kept; its side tool, Settings section
(with a settings update reaching the host half) and file viewer mount; a call and a state subscription
round-trip; every contribution goes after disabling it from Settings › Plugins; and Rescan drops an external
plugin deleted from disk.

## Implementation notes

- Contribution mounts: `WorkbenchWindow.Mount` is the per-plugin host control (`PluginMount_<id>`); a throwing
  factory shows its failure in place.
- Viewer tabs use the kind `viewer` (`viewer:<path>`). The open path consults `FileViewer` before core's kinds, and
  a registration's `Open` may take the open over; core's image and Markdown handling stays in the open path rather
  than registering under `core`, which keeps the existing behaviour for every file no plugin claims.
- File icons (W17) override core's bundled file-type icons in tabs and Files rows. Changes rows gain an icon when either a plugin or the enabled core set answers.
- Workspace actions follow New terminal in every center group's tab strip, as the fork's `renderCenterActions`
  does; project actions render beside Create workspace on Project Home.
- Terminal projections include filesystem workspaces only. Synthetic Project Home layout keys do not
  describe host workspaces and must not trigger plugin state or spec subscriptions.
  Terminal tab decorations use the layout's active workspace during switches, before the window's
  asynchronous project open completes, so existing agent marks survive without another host update.
- `Workbench.ActiveWindow` is the last activated window; `ProjectionChanged` is raised on shared-state snapshots,
  window activation, layout and selection changes and revision bumps, and `WatchHost` filters it.
- Local and remote file revisions advance from the host's workspace change stream. Reconnecting or a
  rescan invalidates open document paths; workspace switches cancel the old stream and reject stale events.
  Git-only batches advance workspace revisions while leaving file revisions unchanged, so Graph follows
  branch/ref changes without making a PDF reread identical bytes.
- `WatchWorkspaceAsync` keeps a workspace watched for the calling plugin's activation, like the fork's
  `watchWorkspaceForLiveContent`. The app owns one watch per workspace on its own project session from the
  workbench's session factory, never a mounted window's host; concurrent and repeated calls share it, and it
  stops when every activation holding it has ended. The call completes once the host's first, ready batch
  has arrived; changes that happened before that cannot be told apart, so readiness, like a restored stream
  after a dropped connection, advances the workspace revision and every file revision known for it. The
  Workbench revision dictionaries stay the only record: while a window has the workspace mounted, that
  window's watcher delivers its revisions and the plugin watch delivers nothing. A failure before readiness
  faults the call and a later call retries. A workbench composed without project sessions (headless
  fixtures) watches only mounted workspaces and completes at once.
- `DeleteBranchAsync` runs the branch list's own delete flow for a local branch of the active window's project and reports
  whether it was deleted, declined, refused or failed; remote-tracking branches are not handled.
- `OpenCommitChanges` opens a kept, commit-keyed continuous review in the active workspace without
  changing the Changes sidebar scope; mismatched workspaces are ignored.
- Branch Graph exercises `SetDiffScope` with explicit commit ids. Changes uses the host's independent
  commit lookup rather than requiring the id to occur in its current comparison menu.

Visualize registers a terminal companion through the same contribution slot as Blueprint. It subscribes
to keyed workspace snapshots, derives availability and title from the terminal's drawing, focuses new
revisions and reports the exact rendered revision through the plugin adapter. Its frames and shared
drawing controls are compiled XAML; no app dependency is introduced into the plugin.

## Not yet ported

- Chat companion hosts, chat tool renderers, `openChat` and the `writtenPathGroup` slot: chat is excluded.
- Editor selections from the Scintilla editor: the control exposes no selection event yet. The Markdown preview and
  `ReportSelection` report selections; a preview selection's lines are counted within the selected text.

The bundled file-type icon set is [core UI functionality](../../SharpRail.Plugins.UI.Kit/FileIcons/SPEC.md), not a builtin plugin. External file-icon slots still override it; plugin asset icons retain their fallback glyph until a valid SVG is available.

## Builtins disabled by default

Builtin plugins whose manifest sets `EnabledByDefault = false` (Claude Code, Codex, Discord, Air Gateway, Air
Context, Tailscale, [Specs](../../SharpRail.Plugins.SpecDialect/SPEC.md), [Blueprint](../../SharpRail.Plugins.Blueprint/SPEC.md)
and the [EPUB Reader](../../SharpRail.Plugins.Epub/SPEC.md)) are listed in Settings › Plugins but
contribute nothing until enabled there. The EPUB Reader is the builtin file viewer for `.epub`: while it is
disabled no viewer claims the extension and the open path is unchanged. Enabling Blueprint while Specs is off
asks to turn Specs on with it (the dependency prompt above).

## Tailscale before connection

The builtin [Tailscale plugin](../../SharpRail.Plugins.Tailscale/SPEC.md) contributes approval Settings
through ordinary activation, and exposes a client-local picker for discovery and bootstrap before any
host projection exists. Desktop and Android composition mount that picker directly; discovery never
uses an unconnected host's RPC. Other plugin activation remains roster-driven.

Diff and continuous Changes selections participate in W12 through the owning document tab. The active editor reference carries the selected file and original/modified source path; closing the owner drops that reference. Only the active workspace and selected document may publish selections.
