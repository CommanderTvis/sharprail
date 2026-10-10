# SharpRail.Scintilla

An Avalonia text editor control built on Scintilla 5.6.7, drawn with SkiaSharp. Text
is shaped with HarfBuzz and ordered with the Unicode Bidirectional Algorithm
(SheenBidi 3.0.0), so emoji sequences, combining marks, ligatures, complex scripts
and mixed right-to-left text lay out, hit-test and edit correctly. It depends only on
Avalonia, Avalonia.Skia, SkiaSharp and HarfBuzzSharp, and runs on macOS arm64/x64 and
Android arm64-v8a/x86_64; `ScintillaEditor.IsSupported` says whether the platform has it.

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

`LineStyles` is a palette of whole-line styles (a foreground and an optional
background band); `StyleLines` assigns one per document line, which suits read-only
views such as diffs. `ShowLineNumbers` toggles the line-number margin, and
`LabelLines` replaces the numbers with a right-aligned label per line. Views that
scroll in step listen to `VerticalOffsetChanged` and call `ScrollToPixel`.
`SelectRange(start, length)` selects UTF-16 offsets and reveals the caret without changing focus or text.

Colours are opaque; composite translucent theme colours before passing them. `ScintillaColors.Caret`
overrides the caret (the foreground otherwise), and `CurrentLine` highlights the caret's line when set. The
control exposes scroll extents (`VerticalScroll`, `HorizontalScroll`, `ScrollChanged`)
for external scrollbars, and `OperationFailed` for clipboard errors.

## Architecture

Scintilla owns the document, UTF-8 byte positions, selections, undo, wrapping and
the editor model. `Native/` supplies its `Editor` platform hooks and a `Surface`
whose drawing and measuring calls cross a C ABI to `SkiaSurface`; Scintilla's
Cocoa view is not used. Avalonia owns focus, pointer capture, text input, clipboard
and scheduling. Native calls and text measurement run on the UI thread; rendering
records an immutable `SKPicture` that Avalonia's renderer replays.

Rendering reuses the last recorded `SKPicture` until Scintilla invalidates (the native
window sets a dirty flag), so scrolling within a line only moves a transform. Wrapping
of off-screen lines runs in idle slices of about 10 ms; `TextShaper` caches each code
point's font and keeps two generations of shaped text, so a whole-document wrap pass
does not evict what is on screen.

Text layout runs in Scintilla's bidirectional mode for every line:

1. `Native/Layout.cxx` splits each screen line at font changes, tabs and control
   character representations, runs SheenBidi over it and orders the runs visually.
2. `TextShaper` segments each run into grapheme clusters (UAX #29), picks a font per
   cluster — the editor typeface, the system emoji font for emoji presentation, or
   a system fallback — and shapes consecutive clusters with HarfBuzz.
3. The layout keeps the clusters as caret stops in visual order. Scintilla asks it
   for caret positions, hit tests, selection and indicator intervals, and draws
   each styled segment's glyphs at their visual positions.

Upstream Scintilla positions text and backgrounds logically even in bidirectional
mode, so `Native/EditView.patch` routes those two drawing paths through the layout.
The build applies it to the pinned source. Caret keys, Backspace and Delete step
over whole grapheme clusters; word movement, line ends and selections keep
Scintilla's behaviour.

## Building

`dotnet build` runs `build-native.sh`, which needs the Xcode command-line tools. It
downloads the SHA-256-pinned Scintilla and SheenBidi archives into `obj/native`,
patches Scintilla and links `libSharpRail.Scintilla.dylib`, which is copied to the
output and publish directories of every project that references this one.

### Android

`build-android.sh <arm64-v8a|x86_64> [cache directory]` compiles the same patched sources with
the NDK from `scripts/android-ndk.sh` into `<cache>/android/<abi>/libSharpRail.Scintilla.so`
(API 24, static libc++, 16 KB pages, only the `sr_*` entry points exported). The bridge draws
through callbacks into SkiaSharp, so it links no Skia. `dotnet build` does not run it: the
Android app runs it per ABI and packages the result as an `AndroidNativeLibrary`.

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
- Syntax lexers, completion, full IME preedit and accessibility text providers are
  not implemented. Soft keyboards are asked not to compose (no suggestions); touch has
  no selection handles or copy menu.

Scintilla's license is in `licenses/Scintilla.txt`; SheenBidi is Apache-2.0
(`licenses/SheenBidi.txt`). HarfBuzzSharp and SkiaSharp retain their NuGet package
licenses.
