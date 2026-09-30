# Rendering: shared controls, document rendering and helpers

Upstream: apps/web/src/components/SPEC.md @ 12830b08
Upstream: apps/web/src/components/ui/SPEC.md @ 12830b08
Upstream: apps/web/src/lib/SPEC.md @ 12830b08

## Responsibility

The dependency-light shared layer every workbench surface builds on: the theme brushes and control
factories in `Ui`, the native Markdown document renderer, the diff viewer, Mermaid rendering and small
helpers. It references no workbench, docking or host-state code, so any panel, dialog or the Settings window
can use it without a cycle. Theme catalogue and colour roles have their own documents:
[Themes.SPEC.md](Themes.SPEC.md), [COLOR.md](COLOR.md) and [SPACING.md](SPACING.md).

## Boundary

- `Ui.cs` owns the semantic brushes, the interface and code fonts (Geist at weight 370, JetBrains Mono),
  the interface font size, and the primitive factories: `Icon`, `Text`, `Row`, `Button`, `IconButton`,
  `Frame`, `Menu` and `Place`. These play the role of upstream's shared primitives; a panel composes them
  rather than restyling Fluent controls ad hoc.
- `Icon` renders a bundled PNG from `Assets/Icons` as an opacity mask over a brush, so a glyph sizes with
  its box and colours with a brush exactly like text. This is the counterpart of upstream's `CustomIcon`
  mask and Remix icon set; the bitmaps are cached per name.
- `MarkdownPreview` / `MarkdownDocumentView` render Markdown natively through Markdig into Avalonia
  controls; `MarkdownDocumentView` adds the compiled `Preview | Source` header.
- `DiffView` renders a file diff with its slim header; `MarkdownDiff` merges two Markdown sources into one
  document with `<ins>`/`<del>` marks.
- `MermaidRenderer` / `MermaidDialog` render fenced Mermaid through Merman's native library and show the
  full-screen viewer.
- `LineWidths` converts symbol-count line widths to logical pixels, like CSS `ch` units.
- `MarkdownLink` is the inline link button that keeps the text baseline of the surrounding paragraph.
- Forbidden: workbench, docking or host-state types; per-call-site raw colours (see COLOR.md).

## Primitive rules

- Icon-only controls always carry both a tooltip and an automation name; `Ui.IconButton` sets both from one
  string. The tooltip is the affordance, the automation name is the accessible label.
- Menus are Avalonia `ContextMenu`s whose items come from `Ui.Menu`; the same menu serves the right-click
  gesture and the visible trigger button (the kebab, the change-row chevron, a toolbar pill). A menu opened
  from a trigger is placed against that trigger.
- Dialogs share one compiled `Panels/DialogWindow` card (see `Panels/SPEC.md`); the solid primary button is
  marked with the `primary` class and takes `PrimaryFill`/`OnPrimary`.
- Selected state is the `Ui.Hover` fill with `TextBrush` text, whether on a tab, a toggle segment
  (List | Tree, Split | Inline, Preview | Source) or a rail row, so "selected" reads the same everywhere.

## Markdown documents

- `.md` and `.markdown` files open rendered by default; the extension check is the Markdown gate shared by
  file tabs and diff tabs.
- A leading YAML front-matter block is not rendered, so spec metadata never shows as a stray heading; the
  source view still shows it.
- The document skin owns typography and the reading measure: headings at 24/20/18/16 against the interface
  body size, section spacing, bordered tables, blockquotes, task lists and GitHub-style alert callouts with
  their own icons. The column is capped at the Markdown line width (default 78 symbols, measured in the
  reading font) unless the unbounded preference is set.
- Links navigate. A `#` link scrolls to the heading anchor. An absolute `http`, `https` or `mailto` link opens
  in the system handler. A relative link resolves against the document's own path and is handed to the
  workbench's navigation callback, fragment included, so the workbench decides preview versus keep. Nothing
  else is followed.
- Images load asynchronously: relative images read through `IProjectServices.ReadFileAsync`, remote
  `http`/`https` images through a shared client with a timeout and an 8 MB cap. A failed image stays an empty
  holder whose tooltip carries the reason, never a crash.
- Parsing can happen off the UI thread (`MarkdownPreview.Parse`), and the control is built from the parsed
  document on the dispatcher.

## Mermaid

Fenced `mermaid` blocks render as themed diagrams. Rendering blocks on native parsing and layout, so it
runs off the UI thread; Svg.Skia displays the SVG. The base theme variables are captured from the current
brushes at render time, and because Mermaid bakes colour into the SVG, every diagram renders again on
`Ui.ThemeChanged`. A diagram opens in the full-screen viewer, which fits the viewer width at 100% and offers
drag panning and 25–500% zoom. Where Merman is unavailable (non-macOS builds) the source is shown with an
unavailability message. Rendered diffs pass `renderDiagrams: false` and keep the source-code degradation.

## Diffs

- `DiffView` shows a path chip, a hide-whitespace toggle, a copy button (copies the diff text; no clipboard
  is a silent no-op) and either `Split | Inline` (source diffs, split by default) or `Source | Rendered`
  (Markdown diffs, source by default). Source lines wrap at the file line width.
- The rendered Markdown diff is a real rich diff: `MarkdownDiff.Merge` aligns lines (Myers after trimming the
  common prefix and suffix, with a bounded edit budget before falling back to replacing the changed region),
  diffs only paired changed lines word by word, and keeps whitespace outside the marks so emphasis and line
  structure survive. The merged document renders through the same `MarkdownPreview` pipeline. The merge runs
  on a thread-pool thread; a newer merge or leaving the rendered view cancels the stale one, and a content
  update keeps the current rendering visible until the fresh merge lands.

## Loading vocabulary

Every asynchronous gap renders something shaped for the wait, never nothing:

- A content region that will fill shows a quiet placeholder in the region itself (`Loading document…`,
  `Rendering diff…`, `Loading Git…`), occupying the region's footprint so arriving content shifts nothing
  around it.
- A retry affordance appears only after a real failure (Changes' Retry, the terminal start failure's
  Retry), never beside an active loading indicator.
- Conditionally absent content is not a loading state: when blank is the correct steady state and the
  caller treats absence as absence, rendering nothing is right.

## Helpers

- `Ui.Alpha` and `Ui.Over` are the one place tints and native-surface compositing are computed.
- Shortcut labels come from one Apple-versus-other definition (`⌘N` / `Ctrl+N`) in the workbench;
  controls never compose modifier glyphs themselves.
- Clipboard writes go through the top level's clipboard and degrade silently when none exists; the text stays
  visible and selectable.

## Get right

- Theme-baked surfaces subscribe to `Ui.ThemeChanged` on attach and unsubscribe on detach, so a detached
  preview never re-renders diagrams for a window that no longer shows it.
- Links never become native navigation: a relative target is always routed through the callback.
- Nothing here catches workbench errors on behalf of a panel; failures surface through the workbench's own
  reporting.

## Not yet ported

- A per-region error boundary: a throwing control is not contained to its region.
- Content-shaped skeleton rows and the fade-in reveal of resolved content; SharpRail's placeholders are text.
- The quiet-scroll frame: intent-revealed 6px scrollbars and directional edge curtains on clipped scroll
  viewers; SharpRail uses Fluent's scrollbars.
- A tooltip provider with tuned delay and a `wrapTrigger` for disabled controls.
- Height-bounded, scrollable menus with horizontal overflow hidden for long commit lists.
- Syntax highlighting for Markdown code blocks beyond the minimal keyword/string/comment tinting, and a
  manifest-driven syntax palette.
- Toasts: SharpRail reports failures through the window's error line and transient notifications.
- A relative-time helper for commit rows (they show short SHA and author only).
- Rendering a link that escapes the worktree root or has malformed encoding as an inert control; today such
  a target reaches the host, whose path validation rejects it.
