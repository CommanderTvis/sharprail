# macOS Scintilla / Skia port

Scintilla 5.6.7 owns the document, UTF-8 byte positions, selections, undo, line
layout and hit testing. SharpRail supplies `Editor` platform hooks and a `Surface`
implementation. The C ABI forwards synchronous drawing/measurement requests to
SkiaSharp; it does not embed Scintilla's Cocoa/CoreGraphics view. The macOS build
requires Xcode command-line tools and fetches a SHA-256-pinned upstream archive.
Scintilla's license is in `licenses/Scintilla.txt`.

Avalonia owns focus, pointer capture, text input, clipboard and scheduling. Native
calls and text measurement run on the UI thread. Rendering records an immutable
SKPicture on that thread; Avalonia's renderer only replays it. Each control owns
its native editor and font/surface resources, and disposes them when the document
is evicted, rather than when docking temporarily detaches the control.

The first integration targets macOS arm64/x64. Text files become editable;
Markdown keeps its existing preview/source views and Git diffs remain read-only.
Cmd+S explicitly saves through the host abstraction, with the original workspace
and contents checked before replacement. Failed saves retain edits. Editing keeps
preview tabs open; modified tabs and windows cannot silently close. Settings and
workspace switches preserve live editor buffers and undo state.

Acceptance checks cover typing, UTF-8 navigation, selection, undo/redo, clipboard,
scrolling, independent editor instances, read-only behavior, Skia output, local
and remote saves, stale/conflicting saves and close protection. No benchmarks.

This is the base editor integration. Lexilla syntax lexers, completion UI,
accessibility text providers, full IME preedit and bidirectional/complex-script
shaping are separate work; do not advertise those as implemented by this port.
