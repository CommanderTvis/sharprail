# Rendering: shared controls, document rendering and helpers

Upstream: apps/web/src/components/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: packages/ui/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/lib/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Upstream: apps/web/src/resources/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Typography contract: apps/web/src/styles/TYPOGRAPHY.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))
Markdown source contract: apps/web/src/panels/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))

## Responsibility

The dependency-light shared layer every workbench surface builds on: the theme brushes and control
factories in `Ui`, the native Markdown document renderer, the diff viewer, Mermaid rendering and small
helpers. It references no workbench, docking or host-state code, so any panel, dialog or the Settings window
can use it without a cycle. Theme catalogue and colour roles have their own documents:
[Themes.SPEC.md](Themes.SPEC.md), [COLOR.md](COLOR.md), [SPACING.md](SPACING.md) and
[TYPOGRAPHY.md](TYPOGRAPHY.md).

## Where the code lives

The shared controls this spec describes moved into [the plugin UI kit](../../SharpRail.Plugins.UI.Kit/SPEC.md)
(`SharpRail.Plugins.UI.Kit`), so a plugin's UI half renders with the same copy the app uses: `Ui`, `ThemeManifest`,
`DialogWindow`, `FindBar`, `LineWidths`, `ViewerLimits`, the Markdown controls, `EditorFrame` and the Mermaid
controls, with `Assets/Icons` and `Assets/Fonts`. Their rules below apply unchanged wherever they live. This folder
keeps the theme catalogue (`Themes.cs`), `DiffView`, `MarkdownDiff` and `MarkdownContexts`, which binds the kit's
Markdown views to a workspace's host (image reads, the spec catalog behind `[[id]]` links) and the window's
preferences (font size and reading measure). The app applies the catalogue's default theme in `App.Initialize`,
since the kit has no theme of its own.

## Boundary

- `Ui.cs` owns the semantic brushes, the interface and code fonts (Geist at weight 370, JetBrains Mono),
  the interface font size, and the primitive factories: `Icon`, `Text`, `Row`, `Button`, `IconButton`,
  `Frame`, `Menu` and `Place`. These play the role of upstream's shared primitives; a panel composes them
  rather than restyling Fluent controls ad hoc.
- `Icon` uses the shared vector masks described by [the kit's icon contract](../../SharpRail.Plugins.UI.Kit/SPEC.md),
  so glyphs scale with their box and take their colour from a live brush.
- `MarkdownPreview` renders Markdown natively through Markdig into Avalonia controls. `Frontmatter` reads properties and spec identity; `FindBar` searches rendered documents. The `Preview | Source`
  header of a Markdown file is the resource pane's view toggle ([../SPEC.md](../SPEC.md)).
- `DiffView` is a diff tab's pane: its slim header, the source diff it draws itself, and the other views
  the resource registry offers for the file. `MarkdownDiff` merges two Markdown sources into one
  document with `<ins>`/`<del>` marks.
- `MermaidRenderer` / `MermaidDialog` render fenced Mermaid through Merman's native library and show the
  full-screen viewer.
- `ToastStack` draws a `State/ToastQueue` as stacked cards above the window's bottom-right corner: a
  variant stripe (accent, success or danger), optional title, wrapped message, the toast's one action (or its two,
  on a row under the message) and a
  Dismiss button. Each card is a `ToastCard` (compiled `ToastCard.axaml`, `x:DataType=Toast`) that fades and rises in; the
  stack owns the per-toast lifetime timers and does not use Avalonia's `WindowNotificationManager`, which would
  duplicate the queue and move the cards out of the window's logical tree. Cards are kept by toast id, so a push or dismissal never restarts another toast's timer;
  an error card is announced assertively, the others politely.
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
- Selected state is the `Ui.Selected` fill with `TextBrush` text, whether on a tab, a toggle segment
  (List | Tree, Preview | Source) or a rail row, so "selected" reads the same everywhere.
- An on/off setting is a `Switch` (`Rendering/Switch.cs`, themed in `App.axaml`): a 32 by 18 track with a
  thumb beside its label. It is a toggle button underneath, so the toggle pattern carries its state to
  assistive technology and it never shows On or Off text. Checked fills the track with `PrimaryFill`;
  disabled paints from the disabled roles.
- Disabled, pressed and focused states come from roles through one application-wide table and from
  `Ui.Button`/`Ui.IconButton` (see [COLOR.md](COLOR.md)); no control restyles its own.

## Markdown documents

- `.md` and `.markdown` files open rendered by default; the extension check is the Markdown gate shared by
  file tabs and diff tabs.
- Source is a lazy editable file buffer. On macOS it uses the normal file editor / Scintilla with the same font,
  theme colours, selection and scrollbars as code files; other platforms retain selectable source text.
  Creating the rendered preview does not allocate the source editor. Source shows the complete file,
  including YAML frontmatter, alert markers and Mermaid fences, and follows the file line-width preference.
- The choice is per tab, survives tab switches, and is not persisted across reload. Switching Preview
  and Source retains both controls and their scroll positions. External edits update an existing source
  editor in place and preserve its first visible line; returning to a previously hidden Source shows the
  latest disk content when the buffer is clean. Unsaved edits update the preview and use the normal Save,
  conflict and close guards; external changes never replace a dirty buffer. Closing disposes both views.
- Acceptance checks cover Scintilla selection and typing on macOS, saving, editor identity across mode
  switches and external reloads, and raw frontmatter, alert and Mermaid content. Long files use the
  viewer limits below rather than eagerly creating a rendered preview.
- External edits reload the document without replacing its mode controls or switching the selected
  Source/Preview mode; both views show the new disk content.
- A leading YAML frontmatter block renders as an Obsidian-style properties block at the top of the scrolling
  document, never as a stray heading: key/value rows, list values as chips, collapsible. With an editable
  source buffer, Preview supports property add/rename/remove, text edits, list chip add/remove, one-level
  mapping entry edits and conversion between text, sequence and mapping, matching the fork. Enter or blur
  commits a field; Escape cancels it. `status` and `type` offer suggestions without constraining values.
  Edits share Source's unsaved draft, undo, Save and conflict checks; preview refresh retains property focus
  and folding. Editing canonicalizes the supported YAML block (including dropping comments), preserving
  the Markdown body verbatim. Empty sequences and mappings retain their shape; duplicate keys are refused.
  Rendered diffs and documents without an editable source remain read-only. Top-level scalars, inline and multi-line flow
  sequences, block lists of scalars and one-level mappings are readable; any other shape shows the raw block
  rather than a guess. The source view shows the YAML as written.
- A document is a spec when its frontmatter carries an `id` and one of the spec graph's `type`s
  (`goal-and-requirements`, `architecture-design`, `module-design`, `submodule-design`, `task-spec`), never
  because of its file name. A spec's `title:` is drawn as an element above the properties, not spliced into
  the source, and its `[[id]]` / `[[id|label]]` links resolve against the workspace spec catalog; a link
  naming a spec the workspace does not have is disabled with the id in its tooltip. In ordinary Markdown
  `[[text]]` stays text.
- Selection spans the document: each paragraph, cell and code body is its own text block, and a drag
  that leaves its block selects through every block up to the pointer. Mod+C copies the combined
  selection with blank lines between blocks and Mod+A selects the whole document.
- `Preview | Source | Split` switches the body; Split shows the source beside its preview with a draggable
  divider. Below 640 logical pixels of pane width, Split is hidden and an existing split falls back to Preview.
  The Outline toggle opens a heading column at the pane's left edge in every view. Its entries come
  from the parsed source (so fenced `#` lines are not headings), open with a spec's title, and a click jumps
  whichever sides are showing: the preview to the heading's anchor, the source to its line.
- Mod+F over a rendered document or rendered diff opens a find bar: case-insensitive, Enter / Shift+Enter
  step with wrap, a count shows `i/n`, the input border turns red on no match, Escape closes it. The current
  match is selected and scrolled into view; other matches are not painted, and a match spanning two text
  blocks is not found.
- Markdown Source find searches the current editable buffer, including syntax hidden in Preview. Split
  find searches the focused pane once; a single bar stays above both panes. Escape restores focus to
  the control that opened find, retaining a source match for editing. Switching modes dismisses find.
  Source ranges use UTF-16 offsets and are converted to Scintilla's UTF-8 positions without editing text.
- Standalone file editors use the same Find bar over their current editable buffer. Command+F on macOS
  opens and focuses it through normal keyboard routing; Escape restores the selected match for editing.
  Markdown owns its single bar above Source/Split rather than adding another inside the file editor.
- The document skin owns typography and the reading measure: headings at 24/20/18/16 against the interface
  body size, section spacing, bordered tables, blockquotes, task lists and GitHub-style alert callouts with
  their own icons. The column is capped at the Markdown line width (default 78 symbols, measured in the
  reading font) unless the unbounded preference is set.
- Links navigate. A `#` link scrolls to the heading anchor. An absolute `http`, `https` or `mailto` link opens
  in the system handler. A relative link resolves against the document's own path, and a leading slash
  against the worktree root (`MarkdownPreview.ResolveLink`); the result is handed to the workbench's
  navigation callback, fragment included, so the workbench decides preview versus keep. Nothing else is
  followed. A link that climbs out of the worktree, or whose percent-encoding is malformed or decodes to a
  NUL, never becomes a control: its text renders as the plain text around it and no request reaches the
  host.
- Images load asynchronously: relative images read through `IProjectServices.ReadFileAsync`, remote
  `http`/`https` images through a shared client with a timeout and an 8 MB cap. A failed image stays an empty
  holder whose tooltip carries the reason, never a crash. Images fit the column, keep their aspect ratio
  and reserve their full height rather than being clipped to a text line.
- Parsing can happen off the UI thread (`MarkdownPreview.Parse`), and the control is built from the parsed
  document on the dispatcher.

## Mermaid

Fenced `mermaid` blocks render as themed diagrams. Rendering blocks on native parsing and layout, so it
runs off the UI thread; Svg.Skia displays the SVG. The base theme variables are captured from the current
brushes at render time, and because Mermaid bakes colour into the SVG, every diagram renders again on
`Ui.ThemeChanged`. Inline, a diagram draws at its natural size up to the document width, in a box capped at
480 px that clips rather than scrolls, so a tall diagram never pushes the prose out of reach. The box carries
the same zoom controls as the full-screen viewer, and ⌘/Ctrl+wheel zooms: zoom scales the drawing, never the
box, a drag pans inside it, and a plain wheel keeps scrolling the document. A diagram opens in the full-screen viewer, which fits the viewer width at 100% and offers
drag panning, trackpad pinch and 25–500% zoom. Where Merman is unavailable (Windows and Linux builds; macOS and Android load the native library, the browser its WASM build) the source is shown with an
unavailability message. Rendered Markdown diffs render unchanged Mermaid fences once, changed fences as labeled Before/After diagrams, and added or removed fences as a single labeled diagram. Each side keeps complete source syntax and its source-line bounds. Both standalone document diffs and continuous Changes sections share this renderer, including its theme refresh, zoom and error/unavailability source fallback.

## Diffs

- Changes review offers split, inline and whitespace controls only when its current file scope contains
  a Source view. Preview-only review hides them; Stacked versus One file is independent. Offscreen
  Source sections retain their mode so scrolling alone does not change the toolbar. The review's chosen
  layout drives real inline or before/after source editors, including after resizing, and survives
  file navigation and Preview/Source switches. Changing source layout leaves rendered previews intact.
Large rendered Markdown previews and diffs share the UI kit's block virtualization,
including chunked large tables/lists/code blocks, estimated offscreen heights, expanded unchanged
runs as top-level blocks, whole-document selection/search and offscreen anchor realization; see the
[kit contract](../../SharpRail.Plugins.UI.Kit/SPEC.md#reaches-replaced-by-parameters).

- `DiffView` shows a path chip, a hide-whitespace toggle, a copy button (copies the diff text; no clipboard
  is a silent no-op), a side-by-side toggle when showing source (on splits, off shows the diff inline),
  and `Source | Preview` when a rendered alternative exists (Markdown renders by default;
  choosing Source is remembered per tab). Until the user clicks
  the toggle, a pane whose halves would each hold fewer than 40 code columns opens inline and a wider
  one splits, re-derived live on resize. Added and deleted files default to inline at every width,
  avoiding an empty comparison pane; the default is re-evaluated when the diff refreshes. The toggle
  always shows the effective view, and a click pins it
  for that tab while wide enough. Narrow panes hide the toggle and use inline even after a pinned split.
  Markdown Source uses the same toggle and width rule outside continuous Changes review. Source
  lines wrap at the file line width. Upstream's diffs stopped honouring that width when they moved off the
  editor component to a diff library that scrolls horizontally; SharpRail's source diffs are still editors,
  so they keep wrapping.
- On macOS source diffs are read-only Scintilla editors (`Editor/EditorFrame`), so only visible lines are laid
  out and large diffs stay responsive. Each line carries a whole-line style: added/removed lines get a tinted
  foreground over a composited wash band, hunk headers the accent, collapsed context a `⋯ N hidden lines` row.
  The gutter shows file line numbers (old and new columns inline, one per side when split). Split sides hold
  the same line count (blank fillers pad unequal runs) and follow each other's pixel offset. Other platforms
  show the raw diff text without view modes.
- Expensive source diffs wait behind a notice, as in the fork. Above 2000 changed (added plus removed) lines,
  or when the counting parse cannot finish within 250 ms, the Source view shows "too expensive to show
  automatically" with Show diff anyway and, where the modified side is the worktree, Open file. One-sided
  (added or deleted) diffs are never guarded. Show diff anyway is remembered per diff tab and per review
  section for the window's session. Diffs up to 64 Ki characters are parsed where they are drawn; longer ones
  are parsed and counted on the thread pool, and a newer render, text change or disposal drops the stale
  result. The fork's order-sensitive check is not ported: SharpRail's budget covers its own counting parse.
- A stacked review section over 1000 source lines, unchanged context included, gets a bounded 640 px viewport
  (the fork's `boundedDiff: "large"`): its editor scrolls on its own and draws only visible lines, and wheel
  input over it stays inside it. Smaller stacked sections with more than one hunk realize per hunk: each hunk
  is a slot that holds editors only within one outer viewport of the visible area and releases them beyond
  two. A slot is estimated at 18 px per row before it is first drawn and keeps its measured height after
  release; the review's scroll anchor absorbs the corrections. Selections, Keep and Revert work per realized
  hunk with the same original/modified line mapping.
- `ViewerLimits` bounds viewers by character count. A Rendered Markdown diff whose two sides together exceed
  1 Mi characters falls back to the source diff and disables Rendered; Markdown files past it open in
  Scintilla. Files and diffs past 32 Mi characters show a notice instead of an editor.
- The rendered Markdown diff is a real rich diff: `MarkdownDiff.Merge` aligns lines (Myers after trimming the
  common prefix and suffix, with a bounded edit budget before falling back to replacing the changed region),
  diffs only paired changed lines word by word, and keeps whitespace outside the marks so emphasis and line
  structure survive. Pipe-table merging preserves the structure and marks cell content as defined in
  [the UI kit contract](../../SharpRail.Plugins.UI.Kit/SPEC.md). The merged document renders through the same
  `MarkdownPreview` pipeline. The merge runs
  on a thread-pool thread; a newer merge or leaving the rendered view cancels the stale one, and a content
  update keeps the current rendering visible until the fresh merge lands.
- The rendered diff carries the modified side's outline (the `Outline` toggle shows only in the rendered view)
  and the properties block, diffed per key so an added, removed or changed value wears the same ins/del marks
  as the prose.

Source diffs can share a continuous review's outer scroll by growing their editor frames to the measured document height. Embedded code does not wrap. Embedded views leave Split/Inline and whitespace controls to their review toolbar. Optional
triage supplies content-keyed Keep controls on source change blocks; individual file tabs and historical
scopes supply no triage. Markdown review previews grow naturally; other rendered review sections use a bounded viewport and the existing registry.
Syntax highlighting for editable files, Markdown Source and source diffs follows
[the UI kit contract](../../SharpRail.Plugins.UI.Kit/SPEC.md#syntax-highlighting).
Source diffs retain the line backgrounds and metadata styling described above while
code tokens use the current syntax palette.

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

## Menus and time

Every context menu is bounded to 480px high and 420px wide (an application style): a long list, such as a
branch's commits, scrolls inside it and rows trim rather than widen it. `Ui.RelativeTime` words an instant
against a given now, never the clock — `just now` under a minute, then `5m ago`, `3h ago`, `2d ago` — and
commit rows in the scope menu append it to the short SHA and author.

## Get right

- Theme-baked surfaces subscribe to `Ui.ThemeChanged` on attach and unsubscribe on detach, so a detached
  preview never re-renders diagrams for a window that no longer shows it.
  The subscription belongs to the shared primitive layer, independent of theme catalogue and preference
  resolution; consumers observe a completed swap rather than the window's appearance directly.
- Links never become native navigation: a relative target is always routed through the callback.
- Nothing here catches workbench errors on behalf of a panel; failures surface through the workbench's own
  reporting.

## Not yet ported

- Per-renderer natural-height capabilities for continuous review. Source and Markdown diffs grow with their content;
  other rendered sections use a bounded viewport rather than format-specific sizing contracts.
- Content-shaped skeleton rows and the fade-in reveal of resolved content; SharpRail's placeholders are text.
- The quiet-scroll frame: intent-revealed 6px scrollbars and directional edge curtains on clipped scroll
  viewers; SharpRail uses Fluent's scrollbars.
- A tooltip provider with tuned delay and a `wrapTrigger` for disabled controls.
- A shared non-modal dialog panel with dialog/title semantics, Escape dismissal and focus return while
  surrounding controls remain interactive; `DialogWindow` supplies modal cards instead.
- Painting every find match rather than selecting the current one.
- A user-chosen code font and its ligatures across the editor, terminals, diagrams and code blocks.
- Syntax highlighting for Markdown code blocks beyond the minimal keyword/string/comment tinting, and a
  manifest-driven syntax palette. Upstream now highlights chat fences off the main thread with lazy
  grammars, bounded caching and ordered replies; its worker/fallback machinery is web-specific. Any native
  equivalent must keep expensive highlighting off the dispatcher and reject stale results.
- The fork's single notice look (its NOTIFICATIONS design, #730). Toasts and inline notices share a neutral
  elevated fill whatever the severity; severity is only the one-pixel muted border and the icon colour, with
  one line glyph per info, success, warning and error, and a fourth `warning` severity next to the existing
  three. Body text keeps the default colour. A notice is a single row (icon, message, actions) or, when it has a
  secondary line or can wrap, a content row over a right-aligned action row; toasts always use the latter.
  Actions are small outline buttons, a secondary one ghost, never filled or coloured. SharpRail's toasts still
  draw a coloured variant stripe, and its inline error text is not a notice.
- Swipe-to-dismiss on toasts, and pausing a toast's timer while the pointer or focus is on it.
- Full typography adoption. [TYPOGRAPHY.md](TYPOGRAPHY.md) is the one vocabulary and the design gate holds
  literal sizes, weights and line heights to it, but most controls still write a size on the scale
  (`Ui.Text(text, brush, 12)`) rather than naming a role; they move to roles as they are touched.

Diff selections feed the shared editor event stream for Codex and Claude Code, including cached files in continuous Changes review. Source selections map displayed rows to the original or modified file lines and subtract unified-diff prefixes from columns; metadata and gap rows have no source selection. Deletion-only selections use the original path for renamed files. Rendered Markdown reports exact selected visible text and the whole source-block line bounds: mixed blocks use modified bounds, deletion-only blocks use original bounds. These bounds may enclose unchanged lines; they are not exact Markdown character positions. Replacing a diff body clears its previous selection.

## Native visual acceptance limits

The earlier 1352×848 logical / 2× comparison matched sampled frame separator positions and palette,
not the entire screenshot. Matching Geist faces still left about one physical pixel of glyph-boundary
difference in the sampled Projects label. Those dated measurements do not close current typography,
controls, menus, docking animation or representative-document fidelity. Native comparisons need matched
content, dimensions and scale; a rendered or accessibility capture proves only what it observes.
Headless rendering, platform compilation and functional checks do not establish native IME, screen-reader
or physical-device acceptance. See the current [acceptance inventory](../../../STATUS.md#outstanding-acceptance-work).
