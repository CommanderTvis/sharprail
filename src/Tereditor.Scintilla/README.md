# SharpRail.Scintilla

An Avalonia text editor control whose document and editing model are a managed C# port of the parts of
Scintilla 5.6.7 that SharpRail uses, drawn with SkiaSharp. Text is shaped with HarfBuzz and ordered with a
managed Unicode Bidirectional Algorithm (UAX #9), so emoji sequences, combining marks, ligatures, complex
scripts and mixed right-to-left text lay out, hit-test and edit correctly. It depends only on Avalonia,
Avalonia.Skia, SkiaSharp and HarfBuzzSharp, ships no native library and runs wherever those do (desktop,
Android and the browser); `ScintillaEditor.IsSupported` is always true. Windows and Linux editing use the
Ctrl shortcuts; the Windows default typeface is Cascadia Mono.

```csharp
var editor = new ScintillaEditor(text, typeface)   // the caller keeps the SKTypeface; Menlo by default
{
    Colors = new ScintillaColors(foreground, background, lineNumbers, selection),
    Direction = ScintillaTextDirection.LeftToRight, // or RightToLeft, or Auto per line
    WrapWidth = 800,                                // infinity disables wrapping
};
editor.TextChanged += (_, _) => { /* editor.Text, editor.IsModified */ };
editor.SelectionChanged += (_, _) => { /* editor.Selection: one-based lines and UTF-16 columns, and its text */ };
```

`StyleText` accepts one palette index per UTF-8 document byte and an opaque colour
palette, allowing an editor-independent tokenizer to style text without editing it.
Its style IDs start at 40, separate from whole-line styles and Scintilla defaults.
While text styles are applied, inserted bytes take the neighbouring style (SharpRail extension to `CellBuffer`) instead of style 0, so typing never flashes default-coloured text before the tokenizer restyles; `ClearTextStyles` turns this off and restores the default style in one document operation. The UI kit
supplies background TextMate highlighting for file and diff frames.

`LineStyles` is a palette of whole-line styles (a foreground and an optional
background band); `StyleLines` assigns one per document line, which suits read-only
views such as diffs. `ShowLineNumbers` toggles the line-number margin, and
`LabelLines` replaces the numbers with a right-aligned label per line. Views that
scroll in step listen to `VerticalOffsetChanged` and call `ScrollToPixel`.
`CaretLine` reports the one-based source line at the active end of the selection.
`SelectRange(start, length)` selects UTF-16 offsets and reveals the caret without changing focus or text.
`ReplaceRange(start, length, text)` changes that range as one undoable edit, retaining the save point.

Colours are opaque; composite translucent theme colours before passing them. `ScintillaColors.Caret`
overrides the caret (the foreground otherwise), and `CurrentLine` highlights the caret's line when set. The
control exposes scroll extents (`VerticalScroll`, `HorizontalScroll`, `ScrollChanged`)
for external scrollbars, `ContentHeight` for surfaces that grow to show every document row, and `OperationFailed` for clipboard errors.
Horizontal wheel/API scrolling is clamped to the tracked rendered text extent.
Soft wrap resets the horizontal offset and disables horizontal scrolling. Edits reset
the tracked width, so shortening text removes obsolete horizontal space; resizing
clamps an existing offset to the new viewport.

## Architecture

Everything below the public control is `internal` and lives in three layers:

- `Core/` is pure managed code with no Avalonia or Skia: the gap buffers (`SplitVector`, `Partitioning`,
  `RunStyles`), `CellBuffer` with UTF-8 bytes, styles, line starts and undo history, `Document` (EOL modes,
  character classes, word boundaries, decorations for indicators, margin text, styling) and the single
  stream `Selection`.
- `View/` is wired directly to SkiaSharp and HarfBuzzSharp; Scintilla's Surface and Platform abstraction is
  dropped, and `SKCanvas`, `SKRect`, `SKPoint` and `SKColor` stand in for its geometry types. `ViewStyle`
  holds styles, indicators and margins; `Bidi` implements UAX #9 on Avalonia's public `Codepoint` bidi
  class and paired-bracket data; `LineShaper` and `TextShaper` shape lines; `EditView` lays out and paints.
- `Editing/Editor` is the editor model. `Send(ScintillaMessage, w, l)` handles the numeric messages listed in
  `ScintillaMessage`, typed methods replace the pointer messages, and it handles keys (the default key
  map), text input, mouse selection, caret blinking, idle wrapping and painting. `ScintillaDocument` owns
  one `Editor` and keeps every call on the UI thread.

Unicode categories, case mapping, grapheme segmentation (`StringInfo`) and UTF-8 conversion come from .NET.
Autocompletion, call tips, lexers, folding, markers, annotations, multiple and rectangular selection,
printing, change history, regular-expression search and code pages other than UTF-8 are not ported.
Avalonia owns focus, pointer capture, text input, clipboard and scheduling; rendering records an
immutable `SKPicture` that Avalonia's renderer replays.

Rendering reuses the last recorded `SKPicture` until the editor invalidates (a dirty flag), so scrolling
within a line only moves a transform. Wrapping of off-screen lines runs in idle slices of about 10 ms;
`TextShaper` caches each code point's font and keeps two generations of shaped text, so a whole-document
wrap pass does not evict what is on screen.

Text layout is bidirectional for every line:

1. `EditView` splits each screen line at font changes, tabs and control character representations, runs
   the bidi algorithm over it and orders the runs visually, line ends last.
2. `TextShaper` segments each run into grapheme clusters (UAX #29), picks a font per cluster — the
   editor typeface, the system emoji font for emoji presentation, or a system fallback — and shapes
   consecutive clusters with HarfBuzz.
3. The layout keeps the clusters as caret stops in visual order and answers caret positions, hit tests,
   selection and indicator intervals; text and backgrounds are drawn at their visual positions.

Caret keys, Backspace and Delete step over whole grapheme clusters; word movement, line ends and
selections keep Scintilla's behaviour.

## Building

`dotnet build` is enough on every platform; there is no native build step.

The ported upstream unit tests (`test/unit`) and the bidi, shaping, view and editor-model checks run with
`dotnet run --project tests/SharpRail.Checks -c Release -- --scintilla-core`.

### Touch and soft keyboards

A finger taps to place the caret, drags to scroll with inertia and holds to select a word; mouse
and pen keep selecting by dragging. The soft keyboard edits through Avalonia's input connection,
which addresses text by offsets, so on Android the input client exposes the caret line with its
neighbours, counts a line break as one character and keeps that window until an edit has finished.
A read-only editor requests no keyboard there.

## Limits

- Caret movement is logical: Left moves toward the start of the text, which is
  rightwards inside right-to-left runs.
- Lines stay left-aligned whatever their direction. The bidi algorithm runs per
  wrapped subline, and shaping does not join across style boundaries.
- Whitespace markers and indentation guides keep logical positions.
- Built-in syntax lexers, completion, full IME preedit and accessibility text providers are
  not implemented. Soft keyboards are asked not to compose (no suggestions); touch has
  no selection handles or copy menu.

Scintilla's license, which covers the ported code, is in `licenses/Scintilla.txt`. HarfBuzzSharp and SkiaSharp retain their NuGet package
licenses.

## Text size and wrapping

`ScintillaEditor.TextSize` is one size for every editor (6 to 40, default 13). A two-finger pinch on any
editor changes it live and raises `TextSizeChosen` when it settles, for the host to remember. `WrapWidth`
is given at the default size and keeps its count of characters at any other. `WrapAlways` wraps at the
pane's edge even with no wrap width, for screens where sideways scrolling is a chore.

### Diagnostics

`SetDiagnostics` replaces diagnostic squiggles using UTF-8 byte offsets and lengths; severity 1–4
selects error, warning, information or hint. The caller supplies colors and performs coordinate
conversion. Underlines do not modify source text or undo history.

`CaretBounds` exposes the caret rectangle in editor coordinates for anchored navigation popups.

Diagnostic underlines may carry optional message text. Hover shows messages for overlapping byte ranges in severity order; pointer exit, editing, scrolling and detach/dispose clear the tooltip. Hit testing accounts for fractional vertical scrolling and excludes blank space beyond text.

Text and whole-line palettes support an optional Italic style flag. Font styles are explicitly set/reset when palettes apply, preserving text and undo state.

`CenterLine` centers a zero-based document line using its wrapped display-line position
and current viewport height; ordinary selection still only ensures caret visibility.
