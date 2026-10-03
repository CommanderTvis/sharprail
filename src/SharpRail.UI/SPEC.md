# SharpRail.UI — application and windows

Upstream: apps/web/SPEC.md @ c44534ea
Upstream: apps/desktop/SPEC.md @ c44534ea
Upstream: apps/web/src/resources/SPEC.md @ c44534ea

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
  to compose direct local calls or remote proxies; `SharpRail.Host.Remote` only to serve the in-process
  terminal socket local relays attach to; `SharpRail.Scintilla`; `Ghostty.Avalonia`; Avalonia.
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

## Lifecycle

Quitting marks the workbench as shutting down so closing windows keep their profile entries, disposes the
state subscription and remote adapters, then ends the local relay socket and every local shell off the UI
thread, because ending a shell awaits its exit. Each window saves its profile entry and Git selections as it
closes. Abrupt death relies on operating-system process cleanup; remote shells belong to their host.

## Get right

- Remoteness is an adapter choice at composition; no panel or flow checks whether the host is remote.
- Host-owned values change through the host and apply when the broadcast arrives, never by writing a
  local copy first.
- Panels stay arrangement-agnostic so another shell can project them differently without a rewrite.

## Not yet ported

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
- A resource-renderer registry behind file and diff bodies. Renderers declare what they match (MIME type,
  falling back to the extension) and their capabilities; the ranked matches, ending in a required text or
  byte fallback, are both the dispatch order and the document's view toggle. The registry owns no content
  or tab state, and content keeps text, retrievable bytes and an absent diff side distinct. `WorkbenchWindow`
  picks the body from the tab kind instead (image, Markdown, diff or code); the views and diff actions this
  enables are listed in [Panels.SPEC.md](Panels.SPEC.md).
