---
id: module-plugin-ui-kit
type: module-design
status: active
title: Plugin UI kit — the shared controls
parent: module-plugin-api
tags: [ui, plugins, public-surface-checked]
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
  - `Ui`: the brush tokens (`Sidebar`, `Surface`, `Header`, `Elevated`, `TextBrush`, `Muted`, `Hint`, `Accent`,
    `PrimaryFill`, `PrimaryFillHover`, `OnPrimary`, `DialogShadow`, `PrimarySubtle`, `PrimaryMuted`,
    `BorderBrush`, `Hover`, `WorkspaceSurface`, `ControlHover`, `ControlDisabledText`,
    `ControlDisabledBorder`, `PrimaryDisabled`, `TextSelection`, the status brushes and washes, the fades),
    the interface and code fonts, `FontSize`, the applied `Theme` and `ThemeChanged`, `Apply(ThemeManifest)`
    and `ApplyResources`, `Alpha`/`Over`, and the primitive factories `Icon`, `Text`, `Row`, `Button`,
    `IconButton`, `Frame`, `Place`, `Menu`, plus `Segment` and `Chip` (below).
  - `ThemeManifest`, the palette record a theme applies.
  - `Switch`, the controlled boolean primitive with a compiled target/track/thumb template and
    native toggle automation. Browser transition/reduced-motion behavior and native appearance
    remain to be audited against the fork.
  - `DialogWindow` (compiled XAML card shared by every dialog), owned by a `Window` rather than a
    `WorkbenchWindow`. Choice toggles use an accent outline and subtle primary fill for selection,
    including while hovered or pressed, so selection stays distinct from pointer feedback.
  - `FindBar`, `LineWidths`, `ViewerLimits`, `SvgAsset`.
    Find searches rendered text blocks or a Scintilla buffer within the supplied scope, selects and
    scrolls the current match, and restores the opening control's focus on Escape.
  - `Visualization.ZoomGesture` shares the fork's zoom bounds, toolbar step, Control/Command detection
    and bounded wheel delta math; Avalonia's upward-positive line delta is converted to pixels.
  - `ScopedSetting` provides configuration scope/source rows and their filter toolbar;
    `SettingValueDialog` composes typed setting values. Agent account and terminal components
    live in `SharpRail.Plugins.Agent.UI`, which depends on this kit.
  - `SvgAsset` can tint `currentColor` from a shared solid brush and reloads on theme changes while
    attached. Its original two-argument constructor remains available to compiled plugins.
  - `Assets/Icons` and `Assets/Fonts`, with their license files, as `avares://SharpRail.Plugins.UI.Kit/Assets/…`.
- Owns, in `SharpRail.Plugins.UI.Kit.Markdown`: `MarkdownPreview`, `MarkdownDocumentView` (with its compiled
  header), `MarkdownProperties`, `Frontmatter`, `MarkdownLink`, and `Outline` (the heading column, extracted
  from `MarkdownDocumentView.FillOutline`, which `DiffView` also uses).
- Owns, in `SharpRail.Plugins.UI.Kit.Editor`: `EditorFrame` (the Scintilla host with theme colours, fonts and
  line styles).
- Owns, in `SharpRail.Plugins.UI.Kit.Visualization`: `MermaidRenderer` and `MermaidDialog` (inline and
  full-screen diagram rendering, pan and zoom). The full-screen viewer claims trackpad magnify gestures
  and clamps their scale through `ZoomGesture`.
- Public surface: exactly the symbols in `PublicAPI.Unshipped.txt`, checked by
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` like the API assemblies.
- Allowed deps: Avalonia and Avalonia.Themes.Fluent, SkiaSharp, Markdig, Svg.Controls.Skia.Avalonia, and
  `SharpRail.Scintilla`. The Merman native library stays copied by the app's project; the kit binds it by name.
- Forbidden: any `SharpRail.Host.*` project, `SharpRail.UI`, and the plugin API assemblies. A parameter or
  callback replaces every reach the moved code had: `MarkdownPreview` takes an image reader
  (`Func<string, CancellationToken, ValueTask<byte[]?>>`) instead of `IProjectServices`, a spec-link resolver
  instead of the workspace spec catalog, and its line width and bound instead of `Preferences`; `LineWidths`
  takes the width and bound it converts; `DialogWindow` takes its owner window.

## Reaches replaced by parameters

- `MarkdownContext` carries what a rendered document reaches outside the kit for: the image reader, the spec-link
  resolver (spec id to workspace path; an id it omits renders disabled), navigation, and the font size, reading
  measure and bound. The app builds it with `Rendering/MarkdownContexts.For(host, preferences, navigate)`.
- `CommandKeys` shares platform and keyboard-layout matching between app commands and dialogs.
  Dialogs close on Escape or the platform close chord without referencing the app assembly.
- `IDialogOwner.Dim` is how `DialogWindow` dims the window that owns it; `DialogWindow.Create(title, width)` is the
  standard card the app's `Dialogs` and `MermaidDialog` build on.
- `MarkdownDocumentView.Preview` exposes the rendered document, and `MarkdownPreview.SelectionChanged` reports the
  selected text, which the app feeds into the plugin editor-event stream.
- Inline links, including spec links, inherit rendered diff insertion/deletion colors and backgrounds;
  deleted links and links inside Markdown strikethrough carry the strike decoration.
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
retain the mounted navigation control and its zoom/scroll. Toolbar and wheel use `ZoomGesture`, shared
with PDF Preview, with 25–600% bounds. Native trackpad magnify and further lifecycle/appearance cases
remain verification gates in the Visualize plugin spec.

The account presentation exposes labelled rows, provider-supplied severity and percentage usage,
relative reset times and reading timestamps. Row and usage-window layouts are compiled XAML.
The Codex builtin uses these controls without referencing app or host implementations.
