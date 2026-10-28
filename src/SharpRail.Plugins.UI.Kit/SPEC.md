---
id: module-plugin-ui-kit
type: module-design
status: active
title: Plugin UI kit — the shared controls
parent: module-plugin-api
tags: [ui, plugins]
---

# Plugin UI kit — the shared controls

Upstream: packages/plugin-ui/SPEC.md @ 4737df6d (CommanderTvis fork)

## Responsibility

The primitives, Markdown renderer, code editor frame and diagram view every plugin's UI half needs, moved out
of `SharpRail.UI/Rendering` so a plugin can render UI without reaching into the app's state, host, themes
catalogue, workbench or panels. The app uses the same controls from here; there is one copy. In this commit
the project is created empty with this spec, and the controls move into it as listed below.

## Core file icons

[File-type icons](FileIcons/SPEC.md) are bundled here for every app client; the host holds only their shared enabled setting. They require no builtin plugin or asset RPC.

## Shared control states

`ControlStyles.axaml` and `Ui.ApplyResources` own button, toggle and checkbox states for the entire app
and its plugins, including compiled layouts and controls created from code. Primary, link, quiet and
Welcome card variants share these rules; panes do not copy Fluent state resources. Button content
is vertically centered by default (Fluent leaves it stretched, which tops the text of fixed-height buttons);
a control that wants otherwise sets it locally. Selected toggles
retain an accent outline when hovered or pressed. Checkbox glyphs use the same contrast-safe foreground
as primary buttons: retain the manifest foreground when readable, otherwise use the more readable
black or white against both normal and hovered fills. Button factory labels and icons follow the
control foreground. Disabled controls use the shared disabled colors.

## Menus filled before opening

A native popup keeps the size it measured when it opened, so items added or changed afterwards are clipped.
A context menu whose items need off-UI-thread work registers through `MenuFill.FillBeforeOpening`: an open
request (a context request, or `MenuFill.Open` from code, since `ContextMenu.Open` raises no `Opening`) keeps
the menu closed, gathers on the thread pool, applies the result on the UI thread and then opens the menu once
on the control that asked. A newer request, a cancelled lifetime or a holder that left the visual tree drops
the result, and a menu with no items stays closed. The editor menu, the Changes scope and comparison-branch
menus and the Projects workspace menu (its Open in editors) use it; Ghostty's terminal menu, which cannot depend
on this kit, follows the same rule itself. Checks record each menu's items when it opens and require them unchanged
once its read has landed (`MenuFillProbe`).

## Quiet scrollbars

Adapted from upstream `apps/web/src/components/SPEC.md` (`QuietScrollArea`, `styles/global.css`). The same
stylesheet replaces Fluent's scrollbar presentation for every `ScrollBar` in the app and its plugins: a 6px
transparent track with no arrow buttons, a thumb that is transparent at rest, the border colour while its
`ScrollViewer` carries intent, and the muted text colour under the pointer or while dragged. Intent is
pointer hover, keyboard focus within, or scrolling with a 700 ms grace period (`QuietScroll` adds the
`quiet-scroll-intent` class); an ancestor's intent never reveals a nested viewer's bars, because each
selector reaches only its own template's bars. The editor frame's bars, which sit outside a viewer, take
the frame's hover as intent. High contrast themes keep a 2px resting hairline in the hint colour. Clipped
directions of every viewer fade through pointer-transparent 16px edge curtains, an opacity mask on the
content presenter so the fade reveals whichever surface lies beneath without a per-surface colour; when
both axes are clipped only the vertical curtains show. Terminals keep Ghostty's own scrollbar.

## Boundary

The fork's shared `Switch` exposes a label and controlled checked state without visible On/Off text.
Its compiled template keeps a 40×24 target, 36×20 track and 16×16 thumb. Mouse, keyboard and
automation activation request the next value exactly once; the caller updates the checked value.
Disabled switches and handled click events make no request. Automation reports the label and toggle
state. Checked, unchecked, hover, focus and disabled colors follow the shared theme brushes.
The parameterless constructor, bindable label/state and change-request event support compiled plugin
layouts; the convenience constructor supplies the same contract from code. Programmatic state updates
publish toggle automation changes without requesting another update.

- Owns, in `SharpRail.Plugins.UI.Kit`:
  - Shared theme brushes, interface and code fonts, live theme updates, colour composition
    and primitive controls. All consumers use the same palette and control factories.
  - Semantic typography and layout roles are generated from the app’s authored design files.
  - `Switch`, the controlled boolean primitive with a compiled target/track/thumb template and
    native toggle automation. Browser transition/reduced-motion behavior and native appearance
    remain to be audited against the fork.
  - `DialogWindow` (compiled XAML card shared by every dialog), owned by a `Window` rather than a
    `WorkbenchWindow`. Choice toggles use an accent outline and subtle primary fill for selection,
    including while hovered or pressed, so selection stays distinct from pointer feedback.
  - Search and bounded document presentation. Find searches rendered text blocks or a Scintilla
    buffer within the supplied scope, selects and scrolls the current match, and restores the
    opening control's focus on Escape.
  - `Visualization.ZoomGesture` shares the fork's zoom bounds, toolbar step, Control/Command detection
    and bounded wheel delta math; Avalonia's upward-positive line delta is converted to pixels.
  - `ScopedSetting` provides configuration scope/source rows and their filter toolbar;
    `SettingValueDialog` composes typed setting values, with either a closed set of choices or open
    suggestions shown as chips over a text field that accepts any value. Agent account and terminal components
    live in `SharpRail.Plugins.Agent.UI`, which depends on this kit.
  - `SvgAsset` can tint `currentColor` from a shared solid brush and reloads on theme changes while
    attached. Mounted icons with identical byte-array assets and tint share a parsed SVG; the last
    detach releases it. This avoids repeated parsing during tree scrolling without retaining a cache
    of offscreen SVGs. Different tints remain independent. Its original two-argument constructor
    remains available to compiled plugins.
  - Core glyphs use original Remix SVG paths as shared vector masks with a full 24×24 viewbox.
    `Ui.Icon` retains its Border and live Background tint, so geometry stays sharp at fractional sizes
    and display scales without caching raster or tint variants. The cache holds only bundled geometry,
    never mounted controls. The prototype brand mark remains raster until its original vector is available.
    Workbench tree expanders use the same original chevron mask, rotated when expanded, in their
    existing compact slot; stock Fluent's thin stretched chevrons do not substitute for the icon set.
  - `Assets/Icons` and `Assets/Fonts`, with their license files, as `avares://SharpRail.Plugins.UI.Kit/Assets/…`.
- Owns Markdown preview and source views, frontmatter presentation, links and the heading outline.
  Markdown panes below 640 logical pixels hide Split and fall back from Split to Preview, including restored views.
  The document view accepts a lazy source-control factory so its owner can supply an editable buffer;
  `RefreshPreviewAsync` debounces and parses edits off the dispatcher, rejecting stale results.
  `PreviewChanged` lets the owner reconnect selection observers after replacement.
  The app can enable preview frontmatter editing against its source buffer. Compiled property rows expose
  text/sequence/mapping conversion, key and value edits, list chips, mapping entries and add/remove.
  List chips and the Add field share a vertical centre within each wrapped row.
  Supported edits canonicalize only the frontmatter; the body and its line endings remain intact.
  Enter/blur commit and Escape cancels. Status/type suggestions are optional vocabulary, and unsupported
  YAML or a rendered diff stays read-only. Preview refresh keeps folding and active property inputs.
  Rendered diff context and expanded unchanged runs omit the YAML source block already represented by
  Properties, keeping following prose source ranges intact.
  Split follows the source caret by default, keeping its rendered block visible with minimal scrolling.
  Nested blocks use the smallest visible source range; blank lines use the nearest block, folded details
  stay folded, and blocks taller than the viewport reveal their beginning. The toolbar’s Follow cursor
  toggle is available in Split with an editor and is retained in the document’s view state. Cursor moves
  and edited-preview replacement request following after layout; preview scrolling never moves the source
  caret. Disabling following preserves independent scrolling through edits and mode changes.
  Cursor tracking does not change the source ranges reported for selections in the preview.
- Owns, in `SharpRail.Plugins.UI.Kit.Editor`: `EditorFrame` (the Scintilla host with theme colours, fonts and
  line styles).
- Owns, in `SharpRail.Plugins.UI.Kit.Visualization`: `MermaidRenderer` and `MermaidDialog` (inline and
  full-screen diagram rendering, pan and zoom). The full-screen viewer claims trackpad magnify gestures
  and clamps their scale through `ZoomGesture`.
  `MermaidRenderer` removes the implicit "/" root row that Mermaid's tree view (and Merman 0.7, matching it)
  draws above the source's first node, shifting the drawing up a row and left a level. Merman 0.7 has no
  tree view annotations (`:::class`, `## description`, `icon(...)`, from Mermaid 12); they stay parse errors.
  It themes diagrams through Merman's `host_theme`: the appearance plus design-token roles (canvas, surface,
  text, line, border, note, actor, status colours), which Merman expands into every diagram's theme variables.
  Plain base-theme variables are not enough: Merman 0.7 does not derive sequence, gantt and similar variables
  from them, and its tree view reads only its own `treeView` variables, so it drew black text and lines on dark
  themes. On a dark theme the renderer also maps the near-black ink Merman 0.7 still writes as literals (gantt
  ticks, mindmap labels, markers) to the theme text colour, a Merman limitation handled centrally for every
  surface. `--visualize-selection` holds text at 4.5:1 and connecting lines at 3:1 in dark and light themes
  in the Visualize card, Markdown preview and their full-screen viewers.
- Public API analyzers validate declarations against committed API baselines during compilation.
- Allowed deps: Avalonia and Avalonia.Themes.Fluent, SkiaSharp, Markdig, Svg.Controls.Skia.Avalonia, and
  `SharpRail.Scintilla`. The Merman native library stays copied by the app's project (the Android project packages the release's
  `.so` per ABI under the same name); the kit binds it by name.
- Forbidden: any `SharpRail.Host.*` project, `SharpRail.UI`, and the plugin API assemblies. A parameter or
  callback replaces every reach the moved code had: `MarkdownPreview` takes an image reader
  (`Func<string, CancellationToken, ValueTask<byte[]?>>`) instead of `IProjectServices`, a spec-link resolver
  instead of the workspace spec catalog, and its line width and bound instead of `Preferences`; `LineWidths`
  takes the width and bound it converts; `DialogWindow` takes its owner window.

## Reaches replaced by parameters

The Files and Specs trees share an internal virtualized row list. Hierarchy, expansion and
row actions stay in data; only rows near the viewport acquire controls. Row text, chevrons,
selection and role labels use Avalonia's drawing surface, with plugin icons retained as
small child controls. Containers recycle without carrying the previous entry's menu or actions.
Each container retains its label across the presenter's template clearing during recycling.
Callers can supply an icon identity to reuse the child icon across equivalent entries; a different
identity or explicit plugin invalidation replaces it. Retention stays bounded by recycled containers,
not the number of entries visited.
The list preserves keyboard navigation and exposes tree/tree-item automation identities,
selection and expand/collapse providers. File operations and spec navigation remain callbacks
owned by their respective modules.

Markdown documents with more than 32 top-level blocks, or with any split block, keep block content
outside the mounted control tree and arrange only the viewport through a virtualizing panel.
Top-level tables with more than 50 body rows, lists with more than 100 items and code blocks with
more than 100 lines render as consecutive chunks of that size, so virtualization reaches inside a
single large block. Table chunks share fixed column widths estimated from cell text (100–360 px) and
the header row stays in the first chunk; list numbering continues across chunks; a code block keeps
one Copy, on its first chunk, that copies the whole block, and every chunk is as wide as the longest
line. Chunks of one table or code block share their horizontal offset; only the last shows the
horizontal scroll bar. Nested blocks never split.

A preview constructed with `progressive: true` builds its top-level blocks in 8 ms dispatcher slices at
background priority instead of all at once: the first slice is built in the constructor and the rest are
appended below it, with the same margin collapsing, into the virtualizing panel. `RenderComplete` turns
true and `Rendered` fires after the last block; until then line offsets, anchors, selection and Find see
only the blocks built so far. Constructing every block of a book-sized document (thousands of
paragraphs) took 100–150 ms on the UI thread locally, which the default constructor still does.

Opening and width changes measure nothing offscreen. A layout pass visits only the blocks the viewport overlaps (found by binary search over the block
tops) and keeps each unrealized block's estimate until the width changes, so a scroll or page turn in a
6,000-block book costs the viewport, not the document. Unrealized blocks use estimates: a chunk's
row or line count times its line height, otherwise the block's text length at the current width,
or the height measured at the previous width. Realized blocks are measured and replace their
estimates, so scrolling may revise heights and the document extent. Realized blocks are scroll-anchor
candidates: a correction above the reader moves the extent, not the visible text. Completed images and
diagrams mark heights for remeasurement when next realized. Detached document text releases its
shaped-text layout cache. Code text collects its runs before attaching them, so building a chunk
stays linear in its length. Content remains available to whole-document selection, source-line
reporting and Find; each chunk reports its own source lines, and selected code chunks join with one
line break rather than a paragraph break. Anchors, search results and source-line reveals realize
their block or chunk before scrolling to text. Preview and rendered diffs share this path; an
expanded run of unchanged blocks joins the document as its own top-level blocks and chunks.
This reuses the existing rich-text drawing and interaction behavior rather than introducing a
second Markdown renderer. Content and media storage is not bounded by the viewport.

Selection gestures index document blocks once at the start. Pointer hit-testing visits mounted
text only; extending or shrinking a drag updates changed ranges, while selected text and source
lines read the tracked selection instead of rescanning the document. Offscreen blocks still
participate in Shift-click, Copy and Select All. Content added by diff expansion or revealed by
Find joins the selection index before interaction.

Document text retains shaped runs across selection changes when the selection keeps the normal
foreground. Inline/content and styling changes invalidate this cache; detaching releases it.
Themes that replace the selected foreground keep Avalonia's existing styled-selection layout.

- `MarkdownContext` carries what a rendered document reaches outside the kit for: the image reader, the spec-link
  resolver (spec id to workspace path; an id it omits renders disabled), navigation, and the font size, reading
  measure and bound. The app builds it with `Rendering/MarkdownContexts.For(host, preferences, navigate)`.
- `CommandKeys` shares platform and keyboard-layout matching between app commands and dialogs.
- `RowActivation.OnActivate` activates a list or tree row on a mouse's press and on a finger's tap, since a
  finger's press also begins a scroll.
  Dialogs close on Escape or the platform close chord without referencing the app assembly.
- `IDialogOwner.Dim` is how `DialogWindow` dims the window that owns it; `DialogWindow.Create(title, width)` is the
  standard card the app's `Dialogs` and `MermaidDialog` build on.
- `MarkdownPreview.LineOffset`, `ScrollToLine` and `SourceAt` address the rendered document by one-based source
  line: the offset a top-level block (or chunk) starts at, optionally a share of its height into it and optionally
  mounting it first; and, for an offset, the covering mounted block's first line and share. Readers built on the
  preview (the EPUB Reader) jump by line and keep a location across re-layout with them.
- `MarkdownDocumentView.Preview` exposes the rendered document, and `MarkdownPreview.SelectionChanged` reports the
  selected text, which the app feeds into the plugin editor-event stream. Each rendered block remembers the source
  lines it came from, so `SelectedLines` names a selection in the file; `ContextActions` supplies extra items for the
  preview's context menu, shown beside Copy, and with none the text keeps its own menu.
- Inline links, including spec links, inherit rendered diff insertion/deletion colors and backgrounds;
  deleted links and links inside Markdown strikethrough carry the strike decoration.
  Paragraphs reserve the full height of wrapped link labels, including inline HTML links, so long
  URLs cannot overlap following unchanged, inserted or deleted prose. Labels stay readable, clickable
  and selectable at narrow widths and larger font sizes.
- Markdown images in SVG draw: the app's image reader answers an SVG as the inert drawing (`VectorPictures`, off the UI
  thread) rendered to PNG, since the host serves SVG as text. An image that cannot load (unreadable, malformed, unsupported
  scheme) is replaced by a visible `MarkdownImageError` line naming the source and the reason, never a blank gap. The
  drawing is light-themed; a stylesheet's dark media rule is not applied.
- Paragraphs containing Markdown images or inline HTML images reserve the full image height,
  including inside table cells, rather than clipping pictures to a fixed text line height.
- Rendered Markdown diffs align pipe tables as complete blocks, then compare their rows and cell text.
  Insertion/deletion marks belong inside cells; pipes, alignment separators and container prefixes stay
  outside the marks so added and removed tables still render as tables. Changed headers and paired cells
  use word-level marks, while added/removed rows mark each cell. Tables whose column counts differ render
  as separate removed and added tables, preserving each layout. Unequal cell counts in paired body rows
  likewise retain both rows so removed cell text is never lost. Fenced table-like text stays code.
  This avoids wrapping structural Markdown in HTML tags, which makes the parser treat an added table
  as an ordinary paragraph of pipe-separated text.
- `LineWidths.File(width, bounded)` and `Markdown(width, bounded, fontSize)` take the values a preference holds.
- The kit's XAML uses `FindControl` rather than the Avalonia name generator, whose generated members would join
  the public listing.

## What moved, mapped from the fork

| fork `@thinkrail/plugin-ui` | SharpRail |
| --- | --- |
| shadcn primitives: `button`, `dialog`, `dropdown-menu`, `context-menu`, `tooltip`/`IconTooltip` | `Ui.Button`/`Ui.IconButton` (tooltip and automation name from one string), `DialogWindow`, `Ui.Menu` items in Avalonia `ContextMenu`s |
| `popover`, `resizable`, `command`, `textarea` | absent: Avalonia's own `Popup`, `GridSplitter`, `TextBox`; the command palette is the app's `SearchDialog` |
| `toast` | absent: the app reports through the window's notification line (`IPluginUIContext.Notify`) |
| `cn`, `menu-styles`, `tokens.css` | the brush tokens on `Ui`; there are no class names to merge |
| `useThemeSwap` | `Ui.ThemeChanged` |
| `Outline`, `OutlineColumn`, `OutlineToggle`, `outlineTree` | `Markdown.Outline`, extracted from `MarkdownDocumentView` |
| `ToggleSegment` | `Ui.Segment`, extracted from `DiffView`'s private segment factory; `MarkdownDocumentView`'s Preview \| Source \| Split uses the same selected-state rule |
| `path-line` | `PathLine`: the muted folder icon and middle-trimmed code-font directory (`PathLine.Label`, also `TerminalFacts.CwdLabel`) with the full path as tooltip, shared by the Claude Code and Code Intelligence panes |
| `chips` (`CHIP*`) | `Chips` (terminal accessory chips: label, tooltip, button, `ChipGap`), `Ui.Chip`, the chip border `MarkdownProperties` and `DiffView`'s path chip share |
| `SvgAsset` | `SvgAsset`, a control drawing SVG bytes through Svg.Skia, used for `asset:` icons |
| `./markdown`: `Markdown`, `FrontmatterProperties`, frontmatter parsing, alerts, heading ids | `Markdown.MarkdownPreview`, `MarkdownDocumentView`, `MarkdownProperties`, `Frontmatter`, `MarkdownLink` |
| `./markdown`: `CodeBlock`, `highlightCode`, Shiki theme | absent as separate pieces: code blocks are part of `MarkdownPreview`'s minimal tinting |
| `./editor`: `MonacoEditor`, `monacoSetup`, `editorFont`, `editorWrapping` | `Editor.EditorFrame` over `SharpRail.Scintilla`; `CodeDocumentView`, which binds host saving, stays in the app |
| `./editor`: review gutter and widgets | absent: SharpRail has no review commenting |
| `./visualization`: `MermaidView`, `PanZoomView`, `renderMermaid`, `zoomGesture` | `Visualization.MermaidRenderer`, `MermaidDialog` |
| `./visualization`: `VisualizationCard`, `DiagramCard`, `ComparisonCard` | absent for now: the fork's visualize tool card is an AI chat surface not ported yet |
| `ToolFileLink` | absent: a chat tool card's file link |
| `ScopedSetting`, `SettingValueDialog`, `SettingsToolbar` | shared configuration rows, typed value composer, `ScopedSetting.SettingsToolbar` |

Stays in the app: the theme catalogue and resolution (`Rendering/Themes.cs`, `Themes.SPEC.md`, `Assets/Themes`),
`DiffView`, `MarkdownDiff`, `RenderedDiffs.cs`, `CodeDocumentView`, `Panels/Dialogs.cs` (built on
`DialogWindow`), and the colour and spacing documents `Rendering/COLOR.md` and `Rendering/SPACING.md`, which
document the token contract below.

## The token contract

Nothing in this assembly declares a colour. Every visual property is one of `Ui`'s brushes, a font from `Ui`,
or a spacing value from `Rendering/SPACING.md`. The brushes are mutable `SolidColorBrush` instances shared by
every control, and `Ui.Apply(ThemeManifest)` rewrites all of them and then raises `ThemeChanged`, so no consumer
observes half a palette. That makes the brush names, and the manifest colour keys `Apply` reads (`accent`,
`accentHover`, `accentSolid`, `onAccent`, `content`, `sidebar`, `header`, `elevated`, `hover`, `borderStrong`,
`text`, `muted`, `hint`, `selection`, `selectionForeground`, `editorSelection`, `editorSelectionForeground`,
`info`, `success`, `danger`, `warning`), a contract this kit depends on, wherever it is mounted.

- Surfaces that bake colour (Mermaid SVG, the Scintilla editor) read `Ui.Theme` when they render and render again
  on `ThemeChanged`, subscribing on attach and unsubscribing on detach.
- The app applies a theme before anything reads the kit: the catalogue's default from `App.Initialize`, then the
  host's settings. The kit has no default of its own, because it cannot see the catalogue.
- A plugin that paints a literal colour stops following theme changes, and nothing reports it.

## Get right

- `Ui.Icon` caches original SVG geometry per standard icon name and draws it as an opacity mask over a brush, so a glyph sizes with its
  box and colours with a brush exactly like text; the icon set includes `puzzle`, the plugin fallback glyph.
- Moving a type keeps its behaviour: the Markdown, editor and diagram rules in `Rendering/SPEC.md` apply
  unchanged, and that spec points here for where the code lives.
- The kit's checks are the app's existing Markdown, editor, diagram and theme checks, which keep passing
  against the moved controls.

## Agent visualizations

`VisualizationCard` renders raw JSON diagram, SVG or comparison arguments, with tolerant comparison reads. Hand-written SVG goes through `SvgSanitizer` (refusals and theme placeholders: the Visualize spec) into `MermaidView.FromSvg`.
Its frames, options, bullet rows, loading/error states and pan/zoom chrome are compiled XAML. Interactive
diagrams fill a companion; inline diagrams are capped and offer a fullscreen button. The kit owns no
terminal or plugin state. A consumer supplies a render-verdict callback. Comparisons settle immediately;
Mermaid renders through Merman off the UI thread and reports its native parser error. Theme rerenders
retain the mounted navigation control and its zoom/scroll. The pan/zoom surface clips oversized diagrams
to its bounds, including when resized or panned beside other content. Toolbar and wheel use `ZoomGesture`, shared
with PDF Preview, with 25–600% bounds. Native trackpad magnify and further lifecycle/appearance cases
remain verification gates in the Visualize plugin spec.
The pan/zoom surface reads the drawn text runs (with per-character boxes) from the parsed SVG's drawing commands
once per render, off the UI thread, so diagram labels select like document text: a press on a label selects
(drag, Shift-click, double-click for the label), a press elsewhere pans and a plain click clears; Cmd/Ctrl+A and
Cmd/Ctrl+C select and copy in reading order (a tree view as an indented outline), and the context menu adds
Copy All Text and Copy Mermaid Source. The highlight is drawn over the picture in the theme selection colour and
follows zoom. Comparison card text is selectable text.

The account presentation exposes labelled rows, provider-supplied severity and percentage usage,
relative reset times and reading timestamps. Row and usage-window layouts are compiled XAML.
The Codex builtin uses these controls without referencing app or host implementations.

  - Semantic typography and layout roles are generated from the app’s authored design files.
## Syntax highlighting

Editor horizontal scrolling is clamped to the rendered text extent. Soft wrap resets
the horizontal offset and rejects horizontal wheel/API scrolling. Edits reset tracked
widths and a wider viewport clamps an obsolete offset, keeping vertical trackpad motion.

File editors and Markdown Source use TextMateSharp with the bundled language grammars,
plus pinned MIT Kotlin, Scala, TOML and Fish grammars. Common ignore files use the bundled Git ignore
grammar; filename detection includes Dockerfile variants, Makefiles, Git configuration
dotfiles, Gradle/Kotlin scripts, properties, shell scripts and shell startup dotfiles.
`.slnx` solution files select the XML grammar.
Git attributes have a dedicated grammar for paths, macros, attribute flags and assigned values.
Standalone patches use the bundled diff grammar with distinct inserted/deleted colours. Unknown files
stay plain. The tokenizer consumes source and returns UTF-8 style indices independently
of the editor; Scintilla applies the ranges without changing text, selections or undo.
Scope categories map to the applied manifest's syntax palette. Property and key scopes
(`support.type.property-name*`, `variable.other.property*`) take the property colour, ahead of the
generic `support.type` type colour, so TOML, JSON and CSS keys and TOML table names are not coloured
as types. `--syntax` tokenizes a mise.toml-like sample (quoted table segment, inline table, string
arrays, literal and multi-line strings, escapes, numbers, booleans, dates, comments, array tables)
and asserts the role of each token and that keys, strings and comments differ in the dark and light themes.

Grammar loading and tokenization run off the dispatcher. Each document caches line
text and incoming/outgoing tokenizer state, so unchanged lines with unchanged incoming
state reuse their tokens; lines shifted by an inserted or deleted line are matched at their
shifted index, so an edit retokenizes from the edited line until the state converges (`--syntax`
reports lines tokenized per keystroke on 5000 lines: one for a character, two for a newline).
Lines tokenize without a time limit (lines over 16 384 characters stay plain): a line stopped early
would hand its mid-line state, such as still inside a string, to every later line and make styles
depend on machine load. `--syntax` checks the repository's `mise.toml` opened line by line and
after edits against fresh passes, with digits inside basic strings staying strings.
Edits coalesce for 60 ms, superseded requests cancel, and
results apply only to the still-attached document version, in one styling pass without clearing.
Colours never blink off while typing: typed bytes take the neighbouring token's style
(the previous byte's, or the next byte's at a line start) until the pass lands, stale results are
dropped without restyling or hiding the editor, and `--syntax` types into a string and a comment
with a frame between keystrokes, faster than the debounce, requiring every byte outside the typed
range to keep its style in every frame and the settled styles to equal a fresh tokenization. Detaching cancels work
and discards cached style bytes; reattaching highlights the current buffer, including
edits made while detached, before any cached syntax styles can be recoloured.
Theme changes recolour existing tokens.
Highlighting is bounded to 1 Mi characters per document and 16 Ki UTF-16 units per
line, with a 50 ms tokenizer budget per line. Larger inputs remain readable with
plain text and existing diff line styles. TextMateSharp does not support cross-grammar
injections; embedded-language coverage follows the grammar's ordinary includes.

Inline and split diffs project displayed source rows into independent old/new streams.
Diff prefixes, metadata and filler rows are not source. Hunk headers and omitted
context reset tokenizer state, so highlighting at a hunk boundary can be approximate
when the omitted source opened a multiline construct. Syntax colours compose with
added/deleted whole-line background bands; metadata keeps its existing foreground.
Acceptance covers all RedMonk January 2025 top-20 languages, development-file
selection, multiline edits, Unicode/line endings, stale replies, attachment, theme
changes and both diff presentations with preserved background bands.

Custom JSON TextMate grammars override bundled filename selection. Comma-separated
filename globs match case-insensitively without directory components; up to 32 patterns
of 200 characters and 512 Ki characters of grammar are accepted per entry. Replacing
the same scope invalidates cached grammar/state; removal restores bundled detection.
The app applies validated host snapshots off the dispatcher and refreshes attached
editors after applying a new catalogue. Settings owns import/removal and agent launch UI.

TextMateSharp's extension helper returns a package's first grammar; detection instead
resolves the matched language ID to its grammar, keeping C++ distinct from C and
TSX distinct from TypeScript. The bundled Swift grammar uses nested repositories,
but TextMateSharp's repository merge drops the parent scope. Before loading Swift,
the adapter flattens scoped repositories into uniquely named rules and rewrites local
includes, preserving lexical references and ordinary self-includes. This compatibility
transform is restricted to Swift and exercised by real keyword/comment/string samples.

Merman can be supplied through `MermaidRenderer.PlatformRender`, retaining the same source/options/SVG
contract for a browser WASM instance. Native desktop rendering remains the default.
Markdown preview, rendered Markdown diffs and visualization views all use this asynchronous platform
contract; no Markdown path bypasses it for a native-only call. SVG parsing remains off the dispatcher,
and canceled or superseded renders do not replace the current view.

Markdown diff merging can report the original and modified source-block ranges for every emitted merged line, without changing merged output. Unchanged and paired blocks retain both sides; deletion-only and insertion-only blocks retain their own side. Table ranges cover the source table block. Consumers use these ranges for read-only IDE selection context.

Rendered Markdown diff merging aligns Mermaid fences as complete blocks. Unchanged diagrams appear once; changed diagrams retain separate Before/After sources, while added/removed diagrams retain the available side. Labels and fences preserve their own source-side ranges for IDE context. Native diagram rendering and error fallback are shared with normal Markdown preview.

`FileDrop` provides attached acceptance and attachment callbacks on an agent accessory control.
It owns no host or plugin state; the workbench passes resolved paths and the plugin formats the prompt.
The drawn tree supports a threshold drag callback separately from row activation and a highlighted drop target.

Markdown code blocks place Copy beside the source without a separate header row.
The source scrolls horizontally in its own column so long lines never overlap Copy.

## Recorded large-document measurement limits

User-requested 2026-10-11 headless Skia measurements of 2,000-item surfaces showed that virtualization
moves work from construction to scrolling; they are not whole-app RAM or native presentation results.
The Files follow-up reduced allocations over 120 scroll steps from a historical 75.74 MB sample to a
29.21 MB median across three fresh processes (29.19–29.21 MB), still above the fully prebuilt tree's
3.60 MB. Its scroll-step p95 changed from 9.19 ms to a 6.14 ms median (5.53–6.64 ms). Those are not
interleaved controlled trials.

The 2026-10-12 selection comparison used a 19,000,000-character document, 1,795 selectable blocks,
one mounted midpoint paragraph, an 800×600 headless window and 120 pointer updates with source-line
observation and captured frames. Both versions produced 120 selection notifications and the same
245-character final selection. Pointer median/p95 changed from 81.06/103.13 ms to 7.96/21.53 ms;
allocation over the updates changed from 19,926.43 to 149.55 decimal MB. One baseline and one final
fresh-process run establish a directional improvement for that fixture, not current browser speed,
whole-app memory or native latency. Parsing, retained content and long-paragraph reflow remain
proportional to document size; later exact-height scrolling changes were not part of these measurements.

The 2026-10-20 chunking comparison ran `--rendering-bench markdown-large` (2,000 prose paragraphs
around a 5,000-row three-column table and a 20,000-line code block) and the 2,000-paragraph
`markdown` surface in an 800×600 headless Skia window. The baseline was the previous commit with only
this fixture added. Runs alternated baseline and chunked builds as fresh Release processes on the same
machine while other workspaces were building, so absolute times are noisy. Open is construction plus
the first captured frame; scroll steps jump through the whole extent in 120 frames.
| Surface (runs base/chunked) | Open ms | Open allocated | Scroll median / p95 ms | Scroll allocated |
| --- | --- | --- | --- | --- |
| `markdown-large` (2/3) | 472,070–792,637 → 1,482–3,203 | 502.6 GB → 0.60 GB | 40.5–55.1 / 87.8–198.4 → 21.2–57.3 / 79.6–189.5 | 1.16 GB → 0.41 GB |
| `markdown` (3/3) | 1,191–1,581 → 235–262 | 235 MB → 55 MB | 4.2–5.2 / 7.7–12.9 → 9.0–13.2 / 16.1–25.0 | 26 MB → 35 MB |

Most of the large-document gain comes from attaching code runs once: the baseline built its single
20,000-line code text by appending to an attached inline collection. Prose scrolling in small steps
is slower than the baseline because blocks are now measured as they are first realized instead of on
open; jumps across the large document realize new chunks every frame. Construction of all block
controls and parsing remain proportional to document size. Baseline large runs took 8–13 minutes each
under a machine load average above 100, so only two completed; the remainder was stopped.

## Semantic editor colors

`EditorFrame.ApplySemanticTokensAsync` converts zero-based UTF-16 `SemanticToken` spans to UTF-8
style bytes off the dispatcher. Semantic colors use the theme palette; syntax and theme refreshes
preserve semantic precedence. Edits, detach and disposal invalidate old tokens. The kit knows no
host or language-server protocol.

When supported semantic tokens arrive, semantic roles override TextMate colors only on their
reported spans. Uncovered spans retain syntax colors, including keywords, comments and literals;
sparse server responses must not erase lexical highlighting. Empty/unsupported tokens, edits and stopped
servers retain or restore TextMate fallback. Subsequent syntax refreshes cannot overwrite semantics.
Edits keep semantic roles on bytes outside the edited span (common prefix and suffix) until fresh tokens
arrive, and semantic results wait for the current syntax pass rather than painting over unstyled syntax.

Token types map to palette roles: namespace, class, struct, interface, enum, typeParameter, delegate and
record to `type`; parameter and variable to `variable`; property, field and event to `property`;
function and method to `function`. Every bundled theme gives `type` a colour different from `variable`,
`property` and `function` (guarded in the language checks), so a type name never reads like a parameter,
local or member; the TextMate fallback uses the same roles.


Initial syntax highlighting exposes a completion signal and keeps the editor body hidden until
its first pass finishes or reports a failure. Attached editors reuse completed syntax styles
for unchanged text; identical semantic responses do not restyle the document. Cached syntax
and semantic bytes survive temporary detachment, while in-flight results remain version-gated.
Tree row icon layout measures the supplied control's width so adjacent provider marks do not
overlap labels; standard icons retain their original spacing.

EditorFrame.DeferSyntaxColors holds lexical style application while a semantic pass is pending; it retains native style bytes until a replacement is ready. SemanticToken.Italic selects a matching italic palette without changing the semantic color role. Diff line palettes retain their existing bounds.

Static-member italic styling follows the FONT_TYPE=2 defaults in IntelliJ Community's
[default color schemes](https://github.com/JetBrains/intellij-community/blob/master/platform/platform-resources/src/DefaultColorSchemesManager.xml).

Settings pages show a boolean as a `CheckBox` holding a label and a muted description, never a bordered toggle row. `Button.link` carries an 8,4 padding so its hover and focus region has room around the text.
