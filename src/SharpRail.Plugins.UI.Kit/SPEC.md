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

## Shared control states

`ControlStyles.axaml` and `Ui.ApplyResources` own button, toggle and checkbox states for the entire app
and its plugins, including compiled layouts and controls created from code. Primary, link, quiet and
Welcome card variants share these rules; panes do not copy Fluent state resources. Selected toggles
retain an accent outline when hovered or pressed. Checkbox glyphs use the same contrast-safe foreground
as primary buttons: retain the manifest foreground when readable, otherwise use the more readable
black or white against both normal and hovered fills. Button factory labels and icons follow the
control foreground. Disabled controls use the shared disabled colors.

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
    `SettingValueDialog` composes typed setting values. Agent account and terminal components
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
  - `Assets/Icons` and `Assets/Fonts`, with their license files, as `avares://SharpRail.Plugins.UI.Kit/Assets/…`.
- Owns Markdown preview and source views, frontmatter presentation, links and the heading outline.
  Markdown panes below 640 logical pixels hide Split and fall back from Split to Preview, including restored views.
  The document view accepts a lazy source-control factory so its owner can supply an editable buffer;
  `RefreshPreviewAsync` debounces and parses edits off the dispatcher, rejecting stale results.
  `PreviewChanged` lets the owner reconnect selection observers after replacement.
  The app can enable preview frontmatter editing against its source buffer. Compiled property rows expose
  text/sequence/mapping conversion, key and value edits, list chips, mapping entries and add/remove.
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
- Public API analyzers validate declarations against committed API baselines during compilation.
- Allowed deps: Avalonia and Avalonia.Themes.Fluent, SkiaSharp, Markdig, Svg.Controls.Skia.Avalonia, and
  `SharpRail.Scintilla`. The Merman native library stays copied by the app's project; the kit binds it by name.
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

Markdown documents with more than 64 top-level blocks keep block content outside the
mounted control tree and arrange only the viewport through a virtualizing panel. Block heights
are measured at the current document width; scrolling does not revise estimated heights or
change the document extent. Each block is briefly mounted for measurement so inherited styles
and templates contribute their actual height, then offscreen text layouts are released. Cached
sizes avoid whole-document text layout on scroll. Opening and width changes require one full
measurement pass; completed images and diagrams invalidate the geometry. Detached
document text releases its shaped-text layout cache. Content remains available to whole-document
selection, source-line reporting and Find; anchors and search results realize their block before
scrolling to text. Preview and rendered diffs share this path, including expanded unchanged blocks.
This reuses the existing rich-text drawing and interaction behavior rather than introducing a
second Markdown renderer. Virtualization is between top-level blocks: a single large table,
list or code block still uses its existing internal layout, and content/media storage is not
bounded by the viewport.

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
- `MarkdownDocumentView.Preview` exposes the rendered document, and `MarkdownPreview.SelectionChanged` reports the
  selected text, which the app feeds into the plugin editor-event stream. Each rendered block remembers the source
  lines it came from, so `SelectedLines` names a selection in the file; `ContextActions` supplies extra items for the
  preview's context menu, shown beside Copy, and with none the text keeps its own menu.
- Inline links, including spec links, inherit rendered diff insertion/deletion colors and backgrounds;
  deleted links and links inside Markdown strikethrough carry the strike decoration.
  Paragraphs reserve the full height of wrapped link labels, including inline HTML links, so long
  URLs cannot overlap following unchanged, inserted or deleted prose. Labels stay readable, clickable
  and selectable at narrow widths and larger font sizes.
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
| `chips` (`CHIP*`) | `Ui.Chip`, the chip border `MarkdownProperties` and `DiffView`'s path chip share |
| `SvgAsset` | `SvgAsset`, a control drawing SVG bytes through Svg.Skia, used for `asset:` icons |
| `./markdown`: `Markdown`, `FrontmatterProperties`, frontmatter parsing, alerts, heading ids | `Markdown.MarkdownPreview`, `MarkdownDocumentView`, `MarkdownProperties`, `Frontmatter`, `MarkdownLink` |
| `./markdown`: `CodeBlock`, `highlightCode`, Shiki theme | absent as separate pieces: code blocks are part of `MarkdownPreview`'s minimal tinting |
| `./editor`: `MonacoEditor`, `monacoSetup`, `editorFont`, `editorWrapping` | `Editor.EditorFrame` over `SharpRail.Scintilla`; `CodeDocumentView`, which binds host saving, stays in the app |
| `./editor`: review gutter and widgets | absent: SharpRail has no review commenting |
| `./visualization`: `MermaidView`, `PanZoomView`, `renderMermaid`, `zoomGesture` | `Visualization.MermaidRenderer`, `MermaidDialog` |
| `./visualization`: `VisualizationCard`, `DiagramCard`, `ComparisonCard` | absent: the visualize tool is an AI chat surface |
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

- `Ui.Icon` caches one bitmap per name and draws it as an opacity mask over a brush, so a glyph sizes with its
  box and colours with a brush exactly like text; the icon set includes `puzzle`, the plugin fallback glyph.
- Moving a type keeps its behaviour: the Markdown, editor and diagram rules in `Rendering/SPEC.md` apply
  unchanged, and that spec points here for where the code lives.
- The kit's checks are the app's existing Markdown, editor, diagram and theme checks, which keep passing
  against the moved controls.

## Agent visualizations

`VisualizationCard` renders raw JSON diagram or comparison arguments, with tolerant comparison reads.
Its frames, options, bullet rows, loading/error states and pan/zoom chrome are compiled XAML. Interactive
diagrams fill a companion; inline diagrams are capped and offer a fullscreen button. The kit owns no
terminal or plugin state. A consumer supplies a render-verdict callback. Comparisons settle immediately;
Mermaid renders through Merman off the UI thread and reports its native parser error. Theme rerenders
retain the mounted navigation control and its zoom/scroll. The pan/zoom surface clips oversized diagrams
to its bounds, including when resized or panned beside other content. Toolbar and wheel use `ZoomGesture`, shared
with PDF Preview, with 25–600% bounds. Native trackpad magnify and further lifecycle/appearance cases
remain verification gates in the Visualize plugin spec.

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
Scope categories map to the applied manifest's syntax palette.

Grammar loading and tokenization run off the dispatcher. Each document caches line
text and incoming/outgoing tokenizer state, so unchanged lines with unchanged incoming
state reuse their tokens. Edits coalesce for 60 ms, superseded requests cancel, and
results apply only to the still-attached document version. Detaching cancels work
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

Markdown diff merging can report the original and modified source-block ranges for every emitted merged line, without changing merged output. Unchanged and paired blocks retain both sides; deletion-only and insertion-only blocks retain their own side. Table ranges cover the source table block. Consumers use these ranges for read-only IDE selection context.

Rendered Markdown diff merging aligns Mermaid fences as complete blocks. Unchanged diagrams appear once; changed diagrams retain separate Before/After sources, while added/removed diagrams retain the available side. Labels and fences preserve their own source-side ranges for IDE context. Native diagram rendering and error fallback are shared with normal Markdown preview.

`FileDrop` provides attached acceptance and attachment callbacks on an agent accessory control.
It owns no host or plugin state; the workbench passes resolved paths and the plugin formats the prompt.
The drawn tree supports a threshold drag callback separately from row activation and a highlighted drop target.

Markdown code blocks place Copy beside the source without a separate header row.
The source scrolls horizontally in its own column so long lines never overlap Copy.
