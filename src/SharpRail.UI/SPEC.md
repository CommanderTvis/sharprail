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
80px for the traffic lights. The header shows the brand mark, `project › workspace` with the branch, the
connection state and Settings. Pressing plain header content drags the window and double-clicking it
toggles maximize; buttons in the header never start a drag.

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

## Global shortcuts

The window handles app-wide chords before panels: Mod+O opens a project, Mod+N (and Mod+Alt+N) opens
Create workspace, Mod+Shift+N opens a window, Mod+, opens Settings, Mod+B toggles left, Mod+J toggles
right, Mod+Shift+J toggles bottom (including from a focused terminal), and F5 refreshes. Arrangement
commands beyond these belong to the docking menus and keyboard handling in `Docking`.

Mod+= (or Mod++), Mod+- and Mod+0 zoom the whole interface like a browser's page zoom: they step one
app-wide, persisted factor to the adjacent of 50, 67, 80, 90, 100, 110, 125, 150, 175 and 200%, stopping at
either bound, and reset it to 100%. Every window root (workbench, Settings, dialogs) sits in a layout
transform bound to that factor and popups inherit their owner's transform, so layout, icons and text scale
together and windows reflow rather than resample. Surfaces that rasterize themselves (the Ghostty terminals)
derive their pixel density from the ancestor transform as well as the render scaling, so zoomed terminals
stay sharp. The native title bar does not zoom: the title-bar height hint follows the zoomed header while
the traffic-light inset stays physical. Zoom is independent of the Settings interface font size. The chords
bubble, so a focused control that claims them (an editor or terminal) keeps them.

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

- Captioned Project, Workspace and Branch segments in the 40px header, with project and sibling-workspace
  switchers, shared workspace actions and a branch card. The card's comparison picker must use the same
  workspace target as Changes; managed workspaces also show their base branch. Controls and inline rename
  inputs must never start window dragging, while captions and separators remain draggable. As width shrinks,
  branch yields before project and workspace; workspace identity always stays visible. Review/remote chips
  remain outside the current GitHub/PR scope.
- Sharing workspace actions between the header and Projects without duplicating host requests or rename
  state. A header rename is bound to its starting workspace and abandoned on a workspace switch, including
  a pending offline commit. A Remove dialog names its captured workspace and dismisses if the active
  workspace changes before confirmation.
- A native application menu (application, Edit, Window roles on macOS) so standard editing commands route
  through the platform responder chain.
- Removing the traffic-light inset in macOS full screen.
- Honouring the macOS title-bar double-click preference (`AppleActionOnDoubleClick`: zoom, fill,
  minimize or nothing) on the custom header; SharpRail always zooms the window.
- Per-region error isolation: a failing panel or document body shows an error in its own region rather
  than affecting sibling groups or the window.
- Trackpad pinch on macOS driving the same page zoom continuously. The factor is captured when the gesture
  starts and each reported scale is applied against that baseline, bounded to 50%–200%, so updates never
  compound; content that claims the gesture keeps it, and pinch adds no second zoom owner. Zoom currently
  snaps every value to the fixed steps, so a pinch needs the factor to hold values between them.
- Renderer capabilities beyond dispatch: review anchor geometry, phone support and the per-renderer copy,
  layout and whitespace flags. The diff pane decides its own split, whitespace and copy controls from
  whether the source diff is showing, and there is no review surface to anchor to.
