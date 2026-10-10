---
id: submodule-ui-plugins
type: submodule-design
status: active
title: Plugins — the app-side plugin runtime
depends-on: [module-plugin-api, module-plugin-ui-kit, module-host-abstractions]
references: [submodule-ui-panels, submodule-host-plugins]
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
- The loader (`PluginLoader.cs`, `BuiltinPlugins.cs`, `ExternalPlugins.cs`, `PluginUIContext.cs`,
  `EditorEvents.cs`) references the registry, the API assemblies, `IPluginService`, `SharedState`, the
  profile, and the workbench's own operations it binds context members to. Only `Workbench` creates it.

## Boundary

State subscriptions hydrate a scoped snapshot before consuming pushes. A snapshot may return a single
payload or a list of payloads; list rows are individually filtered by the subscription's key fields.
An unscoped subscription to a keyed channel consumes pushes without requesting a snapshot whose
required scope is absent. Payloads that are themselves collections retain their collection shape.

- Owns: the registry (manifests and roster, the active set, one append-only, plugin-tagged list per
  contribution kind, and the read selectors the workbench consumes) and the loader (the builtin array, the
  external assembly load and refusal path, the `IPluginUIContext` binding, the editor-event emitter, and the
  roster-to-activation reconciler).
- Public surface: `PluginRegistry` and its selectors, `PluginIcons`, and `PluginLoader` (start, stop) for
  `Workbench`; nothing else needs a plugin's internals.
- Forbidden: the registry reaching into the loader, `SharedState`, the host, or any window. Every live host or
  window edge is the loader's job, so the registry stays checkable with a fake roster and no app.

## The registry

- Tables are `Manifests`/`Roster`, `Active` (the loader's per-plugin activation flag), and one list per kind:
  `SideTools`, `SettingsSections`, `Companions`, `FileViewers`, `TabDecorators`, `Launchers`,
  `WorkspaceActions`, `ProjectActions`, `TerminalAccessories`, `FileIconSlots`, `DocumentLinkSlots`. Every
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

- Builtin UI halves are a literal array in `BuiltinPlugins.cs`: a manifest and a factory for its
  `PluginUIModule` per plugin, including Spec Dialect, Blueprint, Claude Code, Discord and PDF Preview. The manifests are registered before any roster arrives,
  so a persisted `plugin:<id>:<tool>` tab renders its placeholder with the right label and icon.
- An external UI half loads through the host: `IPluginService.ReadFileAsync(id, entry.Ui)` returns the entry
  assembly's bytes, which load into a `PluginLoadContext` keyed by plugin id and content hash. The context
  resolves the shared assemblies (the framework, Avalonia and its dependencies, SkiaSharp, the API assemblies,
  the kit) from the default context and fetches any other assembly it asks for as
  `<directory of the entry>/<name>.dll` through the same read, off the UI thread. The assembly must expose
  exactly one public concrete `PluginUIModule` subclass with a public parameterless constructor. Loading
  never blocks the UI thread; activation runs on it.
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
  the roster row's before anything loads. An external plugin is trusted, since its assembly and the manifest
  behind the roster row come from the same directory. A broken plugin degrades to inert placeholders instead
  of taking the reconciler down and blocking every other plugin.
- `Settings<T>()` and `Host().AppSettings` read from the latest `HostState` snapshot through one projection;
  `UpdateSettingsAsync` sends a `plugin-settings` change and completes when a snapshot carrying the new value
  arrives.
- Shown terminal keys include terminals displayed beside the selected editor in a pane, with the last-focused
  centre group first. Hidden tabs outside that selected pane do not become the configuration context.
- The workspace projection includes host-published catalogs and each mounted window's host-resolved
  project/workspace identity. A directly opened worktree is routable by plugin actions before a catalog
  refresh; this requires no Git scan or extra persisted workspace list.
- `OpenTerminalAsync` with a tab key already open in the target workspace selects that tab without changing it;
  an unknown key places a new terminal tab with that key in the requested or last-focused centre group and
  types the command once the shell starts. Plugin-opened terminals are centre resources unless the caller names
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
  name, icon, default side and the reveal menus read the composed catalog. A `RequiresGit` tool is withheld in a
  workspace without Git history, like Changes and Review. When deferred Git discovery changes the
  catalog, rebuilding the dock retains keyboard focus on the equivalent named control. An open dock
  menu defers that catalog refresh until it closes, then the window applies the latest catalog.
- File actions (W21) are read from the registry as a context menu opens, so labels and availability are current:
  after a separator in a Files row's menu, as the code editor's whole menu (it opens only when one is offered),
  and beside Copy in the Markdown preview's. The editor and the preview pass the one-based source lines of the
  selection; without one the action is for the file.
- `Editors.OpenAsync` opens in the window showing the file's workspace, falling back to the active window.
- Attention notifications (W20) go to the workbench's `AttentionNotifications`, which decides whether and
  how they leave the app ([Away notifications](../SPEC.md)); the context adds nothing of its own.
- Settings sections (W4) are grouped under Settings › Plugins in `SettingsWindow`, indented behind a rule
  directly below it, as the fork's navigation nests them; Settings › Plugins is core's own section.
- Companions (W6) open beside a terminal tab in an embedded split inside the terminal's body, never as a tab of
  their own; the open companion per terminal is window view state. A plugin's focus request selects
  that companion in every window holding the named terminal, including when another window is active.
  Selecting a companion changes the embedded pane, preserving keyboard focus in the terminal or
  active window, as the fork's embedded-pane state action does.
- File viewers (W7) are consulted by the document open path and by tab restoration; a viewer with read
  strategy `None` gets no text read, and a tab whose viewer has gone shows a placeholder offering to open the
  file as text.
- Tab decorations (W8) apply to every tab strip's tabs; the first non-null decoration wins.
- Launchers (W9) are offered in the create-workspace flow as "Start in a terminal with …" and by
  `OpenTerminalAsync`'s callers; an unavailable launcher shows its reason.
- Workspace-scoped actions (W10) render in the workspace's start-actions row beside New terminal; project-scoped
  ones on Project Home.
- Side-tool bodies and workspace actions are retained per window/workspace rather than recreated on
  navigation or unchanged host updates. Tools may opt into `Retarget` for a shared data scope, such as
  repository history across worktrees. Pending routing retains the previous tool body; unregistering a
  contribution or closing its workspace/project releases its cache, as do appearance and window changes.
- Terminal accessories (W11) render as rows below the terminal surface, inside `TerminalView`; `Write` types
  into the Ghostty surface, `BufferTail` reads its screen and scrollback, and the headless terminal backend
  implements both over its plain-text view.
- Editor events (W12) are fed by the Scintilla editor (open, close, activate, save, selection) and the Markdown
  preview (selection).
- File icons (W17) are consulted for tabs, Files rows, Changes rows and the diff header's path chip; core's
  generic glyph is the fallback.

## Settings › Plugins

`Panels/PluginsSettings.cs` is the core Settings section that manages the roster: one row per roster entry,
with icon, label, version (external plugins only; a builtin's version is the app's), origin, status, a reason
line for failed and refused rows, a contribution summary, a switch, Retry on failed rows
(`IPluginService.RetryAsync`), a Rescan button (`RescanAsync`), and an editor for the extra plugin roots (add
and remove, each change a `plugin-paths` host change). Each row is its own control keyed by plugin id, and a
roster change updates rows in place.

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
activation outside `Activate` refused. The fixture plugin then runs end to end through the real host, once in
process and once remote over gRPC: installed from disk beside a refused and a removable copy, a dormant tool
placeholder turning live with the tab instance kept, its UI half loaded through the host, its side tool,
Settings section (with a settings update reaching the host half) and file viewer mounted, a call and a state
subscription round-tripping, every contribution gone after disabling it from Settings › Plugins, Rescan dropping
a plugin deleted from disk, and a shared assembly in the plugin directory not loaded twice.

## Implementation notes

- Contribution mounts: `WorkbenchWindow.Mount` is the per-plugin host control (`PluginMount_<id>`); a throwing
  factory shows its failure in place.
- Viewer tabs use the kind `viewer` (`viewer:<path>`). The open path consults `FileViewer` before core's kinds, and
  a registration's `Open` may take the open over; core's image and Markdown handling stays in the open path rather
  than registering under `core`, which keeps the existing behaviour for every file no plugin claims.
- File icons (W17) replace core's glyph in tabs and Files rows; Changes rows and the diff path chip gain an icon only
  when a plugin answers, so their layout is unchanged otherwise.
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

File Icons occupies the file icon slot only for files. Its cached asset bytes feed a compiled icon frame
that retains a plain file glyph until a valid SVG is available. Missing and malformed assets preserve
that fallback. Theme changes recolour existing controls; resizing retains tab icon controls.
