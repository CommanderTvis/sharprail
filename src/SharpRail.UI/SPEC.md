# SharpRail.UI — application and windows

Upstream: apps/web/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: apps/desktop/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: apps/web/src/resources/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))
Upstream: apps/web/src/shell/locationBar/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The Avalonia desktop client over the host abstractions. It composes one app-owned `Workbench`, opens
every `WorkbenchWindow` from it, and renders layout-agnostic panels (Projects, Specs, Files, Changes,
Review, documents, diffs, terminals) inside each window's docking frame. The same UI serves an in-process
host and a remote one; which one is an adapter choice made once at composition, never a branch inside a
panel or feature flow.

## Boundary

- Owns: application startup and shutdown (`Program.cs`, `App.cs`), the app-owned composition
  (`Workbench.cs`), window chrome and the window title bar, global shortcuts, theme application, and the
  integration of the docking engine with profile persistence, host state and panels.
- Allowed dependencies: `SharpRail.Host.Abstractions`; `SharpRail.Host.Core` and `SharpRail.Host.Client`
  to compose direct local calls or remote proxies; `SharpRail.Scintilla`; `Ghostty.Avalonia`; Avalonia.
- Forbidden: host projects depending on the UI; panels reading the frame or knowing where they are placed;
  a UI-private copy of host-owned state; a background daemon.

## Internal modules

| Directory | Owns | Spec |
| --- | --- | --- |
| `Docking` | Frame/workspace-view model, pure transitions, rendering, drag/resize, tab chrome | [Docking/SPEC.md](Docking/SPEC.md) |
| `State` | Profile persistence and the app's shared host-state subscription | [State/SPEC.md](State/SPEC.md) |
| `Terminal` | Terminal tab body, Ghostty bridge, the relay mode | [Terminal/SPEC.md](Terminal/SPEC.md) |
| `Panels` | Settings and shared dialogs | — |
| `Rendering` | Markdown, diffs, Mermaid, themes and shared brushes | — |
| `Editor` | The Scintilla-backed code document view | — |
| `Resources` | The resource-renderer registry, the file pane and the format views | below |

Dependency rules: `Docking` never references panels, host services or persistence; the window injects
content through a render callback. `State` references `Docking` only for the `DockState` type it persists.
Nothing references `WorkbenchWindow` except the composition root and checks.

## Startup

`Program.Main` dispatches `--terminal-relay` before Avalonia starts, so a relay child never builds a UI.
Otherwise `App` resolves the endpoint (`SHARPRAIL_REMOTE`), the profile directory (`SHARPRAIL_PROFILE`,
default `~/.sharprail`) and the initial root, opens the profile, migrates and opens the local host state,
and composes one `Workbench`: the shared-state subscription, the terminal factory and a factory for
per-window project sessions. It then opens one window per profile `Windows` entry, the first at the
initial root and the rest at their own last location. Startup follows the root AGENTS.md performance
rules: routing identity first, deferred Git and indexing, panels filled progressively.

## Windows

Every window comes from the one `Workbench`; there is no second process and no daemon. New window (header
menu, Mod+Shift+N) opens another window at the source window's Project Home with a fresh frame and the
source's size. Each window is its own terminal client and its own project session, while windows share
host state, the terminal host and the profile. Closing a window while others remain forgets its profile
entry; quitting keeps every entry so the next launch restores the same set.

## Window chrome

The 40px header is the window's title bar. `WorkbenchWindow.axaml` extends the client area into the
decorations so the native controls stay while the header provides the colour; on macOS the header leaves
80px for the traffic lights. The header shows the brand mark, the location bar, the connection state and
Settings. Pressing plain header content drags the window; buttons and text inputs in the header never
start a drag. In macOS full screen the traffic lights hide, so the header drops their inset and keeps only
its ordinary 12px margin.

The header covers the native strip, so the window handles a double-click on it itself (`WindowChrome.cs`).
On macOS it reads `AppleActionOnDoubleClick` afresh on every double-click (`defaults read -g`, off the UI
thread), so a changed System Settings choice applies without a restart: unset, `Maximize` and `Fill` zoom
the window (a second double-click restores it), `Minimize` minimizes, anything else does nothing. A
double-click arriving while the previous one is still resolving is dropped, and full screen ignores it.
Other platforms, and windows with no native handle, zoom.

## Application menu

On macOS `ApplicationMenu` installs the native menu bar before the first window opens. The application
menu keeps the platform's own items (Services, Hide, Hide Others, Show All, Quit); every window that opens,
dialogs included, gets Edit (Undo, Redo, Cut, Copy, Paste, Delete, Select All) and Window (Minimize, Zoom,
Close, Bring All to Front) with their conventional chords. Items call what their chords already reach, so
a menu pick and a key press are one path and dispatch once: Close asks `AppCommands.Close` in a workbench
window and closes any other window; an editing command acts on a focused text box directly and reaches
every other focused control as its chord, because editors, terminals and selectable text implement those
chords themselves. Delete has no chord and is never forwarded. The menu's Quit is the platform item and
ends the app through the lifetime's shutdown directly; only the keyboard chord, which the window sees
first, runs the confirmation gesture. Other platforms have no menu bar and keep their chords.

## Location bar

`LocationBar.cs` renders the header's captioned segments — PROJECT, WORKSPACE, BRANCH — each an uppercase
10px caption above a 22px value pill, with a hairline before every segment but the first. It owns no host
state: it shows what the window already knows and calls the same methods as the panels.

- PROJECT lists every open project with the current one checked, Project home (disabled while there) and
  Add project…, which is the Projects panel's picker. Choosing a project opens its Project Home. With no
  project the segment reads `SharpRail` and the other two are absent.
- WORKSPACE shows the active workspace's actions above Switch to (its siblings from the host's workspace
  registry, the rows of the Projects rail, with their branch when it differs from the name) and New
  workspace with the Mod+N label. The actions are the Projects row's own (`AddWorkspaceActions`): Open in,
  Copy path, Reveal in file manager, Rename for a managed workspace, and Remove worktree… or, for an
  attached one, Remove from SharpRail. At Project Home the pill reads `Project home` and offers only the
  switcher and creation.
- Rename replaces the pill with the same inline input and the same single rename state the Projects rail
  uses; a flag says which of the two shows the input, so there is never a second request or a second
  draft. A header rename is bound to the workspace it started on: landing anywhere else abandons it,
  including a commit left pending while the host was unreachable.
- Remove asks through the shared confirmation, which names the workspace it was opened for. When that was
  the active workspace and the window lands elsewhere before the answer, the dialog dismisses itself, so
  confirming can never remove a different workspace.
- BRANCH is present in a workspace of a Git repository. Outside the project folder the caption adds
  `· from <target>`, the workspace's current review target (the host's when it holds one), as the ready
  placeholder does; the Default workspace's caption is plain. The pill opens the branch card, which takes focus itself: the branch with Copy (a
  transient notice confirms), and Compare to, which is the Changes panel's own picker (`ComparisonPicker`)
  writing the same per-workspace selection and the same host review target, so the two can never disagree.
- Pills and the rename input never start a window drag; captions, hairlines and the space between remain
  part of the title bar.
- `LocationStrip` lays the segments out. As width shrinks the branch yields first, then the project; a
  segment that would fall under 72px is dropped whole, and the workspace keeps what is left, so workspace
  identity stays visible at the minimum window width.

## Resource renderers

`Resources/ResourceRegistry.cs` decides what draws a file or a diff body. A renderer declares what it
matches (file-name globs, MIME type with a `type/*` wildcard, and whether the content is text) and its rank;
a resource is described by its path and the host's content metadata, with the MIME type falling back to the
extension when the host names none. `Resolve` returns the matching renderers that support the intent (view
or diff) by descending rank, always ending in the required fallback: `sharprail/code` for text and
`sharprail/binary` for bytes. A registry without the fallback for an intent throws rather than showing
nothing. That ordered list is both the dispatch order and the document's view toggle; a single candidate
shows no toggle.

The registry holds no content and no tab state. `ResourceContent` keeps text, bytes that can be fetched
(a loader that checks the SHA-256 it was promised) and an absent diff side as three distinct cases, so
absence is never an empty byte payload. `ResourcePane` is the file body: it builds a renderer's view on
first use, keeps it while the pane lives, and reloads the views that can take new content in place.
`DiffView` is the diff body and takes its toggle from the same resolution; the source diff is the pane's own
drawing, so the text fallback supports diffs without a factory. A diff renderer that returns nothing cannot
draw that content: its toggle is disabled and the next candidate takes over without changing the tab's
choice.

`BundledRenderers` registers the format views that need nothing from the window (image, vector, table,
JSON tree, notebook, Git LFS pointer, byte card); `WorkbenchWindow` registers code and Markdown, which need
its editing and navigation wiring (`ResourceDocuments.cs`, `RenderedDiffs.cs`). The views themselves are
listed in [Panels.SPEC.md](Panels.SPEC.md). A text diff is described by its path; a diff Git reports as
binary, or one that may hold an LFS pointer, waits for the host's side metadata before choosing.

The branch is a control, not a caption: it carries the branch glyph and a chevron and opens the project's
local branches (`BranchList.cs`), each with the worktree path occupying it, re-read on every open. A branch
can be deleted from there after a confirmation, since a branch may be the only copy of what it points at,
except the one currently checked out and a workspace's branch; both say why on the disabled control, and
the host refuses them again. Disabled delete icons stay dimmed on transparent backgrounds, matching idle
enabled icons. The list's Fetch brings every remote up to date without moving a local branch.

## Global shortcuts

The window handles app-wide chords before panels: Mod+O opens a project, Mod+N (and Mod+Alt+N) opens
Start work, Mod+Shift+N opens a window, Mod+B toggles left, Mod+J toggles right, Mod+Shift+J
toggles bottom (including from a focused terminal), Mod+Shift+F searches the workspace, and F5 refreshes.
The search is a dialog (`Panels/SearchDialog.cs`) rather than a panel, because it is a question asked and
dismissed: one query over the active worktree, results grouped by file with one row per matching line, and
a hit keeps its file open scrolled to that line. With no active workspace the chord does nothing. On macOS alone, Cmd+, (no other
modifier) opens Settings, the Preferences chord every Mac app has; Windows and Linux have no such convention,
so Ctrl+, stays free for whatever else wants it. A modal dialog already open receives the chord instead.
Arrangement commands beyond these belong to the docking menus and keyboard handling in `Docking`.

Mod+= (or Mod++), Mod+- and Mod+0 zoom the whole interface like a browser's page zoom: they step one
app-wide, persisted factor to the adjacent of 50, 67, 80, 90, 100, 110, 125, 150, 175 and 200%, stopping at
either bound, and reset it to 100%. Every window root (workbench, Settings, dialogs) sits in a layout
transform bound to that factor and popups inherit their owner's transform, so layout, icons and text scale
together and windows reflow rather than resample. Surfaces that rasterize themselves (the Ghostty terminals)
derive their pixel density from the ancestor transform as well as the render scaling, so zoomed terminals
stay sharp. The native title bar does not zoom: the title-bar height hint follows the zoomed header while
the traffic-light inset stays physical. Zoom is independent of the Settings interface font size. The chords
bubble, so a focused control that claims them (an editor or terminal) keeps them.

A trackpad pinch drives the same factor continuously. Avalonia reports magnification as deltas with no
phases, so a gesture starts at the first delta after 250ms of quiet: the factor is captured there and the
accumulated scale is applied against that baseline, bounded to 50%–200%, so updates never compound. The
factor may rest between the steps (a persisted factor is clamped, not snapped) and the chords step to the
adjacent factor from wherever it rests. The profile is saved once, when the gesture ends. The handler
bubbles, so content that claims the gesture keeps it, and pinch writes the one zoom preference.

## Locations, history and links

A window's location (`WindowNavigation.cs`) is Welcome, a project's home, a workspace, or a file selected in
the focused center group of a workspace; diff and terminal tabs are workspace-level. It is window-local and
never host state. `WindowLocation` serializes it to a versioned, host-relative link — `#/v1`,
`#/v1/projects/<project>`, `…/workspaces/<workspace>`, `…/resources/<file>` — where each id is one
percent-encoded segment: the project and workspace are their paths on the host and the file is
worktree-relative. Links carry no credential and no host address. Unknown versions, empty ids, extra
segments (a chat link included) and malformed encoding are invalid and mean Welcome.

Each window keeps a Back/Forward list of up to 100 locations, not tab state, and never persists it. Every
move to a different location adds an entry and drops the entries ahead; Mod+[ and Mod+] on macOS, Alt+Left
and Alt+Right elsewhere, and the mouse's back and forward buttons step through it. A step or an opened
link is intent checked against what exists now: a project the host no longer lists falls back to Welcome,
a workspace whose folder is gone to its Project Home, and a file that will not open to its workspace. While
a step is applied, the moves it causes rewrite its own entry instead of adding one, so the entry ends as
where the window actually landed. Stepping onto a workspace-level entry while a file of that workspace is
selected leaves the file selected, one dead press, as upstream accepts.

Links are produced by Copy link among the header's workspace actions and consumed by `--link <link>` at launch,
which the first window opens once it has restored its own location, and by `NavigateAsync`.

## Styling and theming

Static layouts, styles and templates live in compiled `.axaml`; dynamic docking and host wiring stay in
C#. Colours come from the shared brushes that `Ui.Apply` writes from the active theme manifest, never from
literals at call sites. Bundled `*.theme.json` manifests are the theme catalogue (`Rendering/Themes.cs`);
adding a theme is adding a manifest. The theme preference is host state: the fixed id, the fixed/system
mode and the optional light/dark pair. Each client resolves system mode against its own operating-system
appearance; an appearance change is a local effect and never a host write, so two clients may render
different themes at once. Surfaces that bake colours (Mermaid, Scintilla, Ghostty) listen to
`Ui.ThemeChanged` and update without reopening documents. Fonts ship inside the app under
`Assets/Fonts`; nothing is fetched at runtime.

Icons are sized by context: compact chrome (tabs, panel headers, menus, rails) uses 14px glyphs and the
header's prominent actions use larger ones. Icon-only controls carry a tooltip and an automation name.

## Keyboard commands

One app-owned command owner (`AppCommands`, held by `Workbench`) serves every window, so a native menu
added later calls the same methods and each press dispatches once. Windows feed it from a tunnel key
handler, so a focused terminal or editor never dispatches twice.

Keyboard quit is Command-Q on macOS and Ctrl+Q on Linux; Windows keeps its ordinary Alt+F4 close. It runs
the pure `QuitConfirmation` gesture: a 1200ms hold, or a second press within 500ms of the first, arms a
quit that completes on key release. A single tap expires silently and key repeat never confirms. The key
state comes from tracked key-down and key-up events, and a window losing activation cancels an unconfirmed
gesture while an already-confirmed release still completes. The active window shows a non-interactive,
accessible hint (armed, release, quitting) drawn with the shared brushes; with no active window the gesture
skips the hint and quits at once on a tap or on release after a hold. A quit calls the application
lifetime's normal shutdown, so any window can drive it. Explicit menu or operating-system quit stays direct
(`QuitNow`) and never enters the gesture.

Close is Command-W on macOS and Ctrl+W or Ctrl+F4 elsewhere, with Ctrl+W kept by a focused terminal. It
dismisses an open popup first, and a modal handles the chord itself, so the workbench behind it never sees
it; otherwise it asks the layout to close the selected (else first) tab of the keyboard-focused group, or
the last-focused center group, through the normal close path including the busy-terminal confirmation. A
tool, folded group or hidden region is no target. It never closes a window. Quit and close letter chords
follow the typed Latin letter and fall back to the key's physical position when the layout types none.

## Lifecycle

Quitting marks the workbench as shutting down so closing windows keep their profile entries, disposes the
state subscription and remote adapters, then ends every local shell off the UI
thread, because ending a shell awaits its exit. Each window saves its profile entry and Git selections as it
closes. Keyboard quit ends the app through the application lifetime's shutdown, the same path as the
menu. Abrupt death relies on operating-system process cleanup; remote shells belong to their host.

## Get right

- Remoteness is an adapter choice at composition; no panel or flow checks whether the host is remote.
- Host-owned values change through the host and apply when the broadcast arrives, never by writing a
  local copy first.
- Panels stay arrangement-agnostic so another shell can project them differently without a rewrite.

## Not yet ported

- Capability-gated Settle / Keep active in the shared workspace actions, a `Settled · N` submenu with
  reason chips after live siblings, and a warning-tone `WORKSPACE · settled` caption for a settled
  selection. The rail and switcher must consume the same partition, sort and 30-second clock; hosts
  without settling keep the legacy list. Default never settles. See [State/SPEC.md](State/SPEC.md) and
  [Panels.SPEC.md](Panels.SPEC.md) for the selection latch and shelf behaviour. Upstream's merged/closed
  pull-request chips and host review-snapshot arbitration are excluded from this sync.
- The location bar's REMOTE and PULL REQUEST chips and the branch card's Remote and review rows; the pull
  request surface lives in the Review panel only. The card has no separate Based on row.
- A registered URL scheme for location links (they are opened only by `--link` at launch), and a remote
  host cannot confirm that a workspace folder still exists before a history step opens it.
- Native role items in the Edit menu. Avalonia's menu model has no AppKit role selectors, so the items
  are app commands: editing in native panels (the folder picker's path field) is not served by them, and
  there is no About item.
- Renderer capabilities beyond dispatch: review anchor geometry, phone support and the per-renderer copy,
  layout and whitespace flags. The diff pane decides its own split, whitespace and copy controls from
  whether the source diff is showing, and there is no review surface to anchor to.
