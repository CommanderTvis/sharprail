---
id: module-plugin-pdf-preview
type: module-design
status: active
title: PDF Preview — PDF viewing as a UI-only builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-ui-plugins]
tags: [plugins]
---

# PDF Preview — PDF viewing as a UI-only builtin plugin

Upstream: packages/plugin-pdf-preview/SPEC.md @ ab23be7cf (CommanderTvis fork)

## Responsibility

Renders `.pdf` files opened from the Files tree or anywhere else a file opens: each page rasterized at the zoom it
is shown at, a selectable text layer over it, its own toolbar (page count, zoom level, Reload from disk, zoom out,
zoom in, reset), pinch and Command- or Control-wheel zoom, and a reload whenever the file's bytes change. It ships
`EnabledByDefault = true`. SharpRail had no PDF viewer in core, so nothing moved out of it.

## Projects

| Project | Holds |
| --- | --- |
| `Contract/SharpRail.Plugins.PdfPreview` | `PdfPreviewPlugin.Manifest` only: the plugin has no methods, channels or settings |
| `UI/SharpRail.Plugins.PdfPreview.UI` | `PdfPreviewUI` (the `PluginUIModule`), `PdfPreview` (the viewer, compiled XAML), `PdfTextLayer`, `PdfEngine` (PDFium) |

## UI only, no host half

The manifest declares no `Host`: the plugin needs no method, channel or settings namespace, and reads a file's
bytes through the UI context (`ReadFileAsync`, W14) the way the fork read them over the worktree's `/files/` route.
`Host.Core/Plugins/BuiltinPlugins.cs` lists the manifest with no module, which makes it a manifest-only builtin
(see `Plugins.SPEC.md`): active at once, with an empty wire surface, toggled like any other plugin.

The manifest's `Contributes.FileViewers` declares `extensions: ["pdf"]` with read strategy `None`, so the open path
knows the extension before the UI half loads and never reads a PDF's bytes as text. The UI half registers one
`FileViewer` with no `Matches` predicate: the declaration is enough for the registry to route `.pdf` here.

With the plugin off, an open `.pdf` viewer tab shows core's "viewer is off" placeholder offering to open the file as
text, and a `.pdf` opened afresh is an ordinary text tab: the open path's fallthrough once no viewer claims the
extension. That is the intended dormant behaviour.

## Rendering: PDFium, not pdf.js, PDFKit or a native view

The fork renders with pdf.js because a browser `<iframe>` hides the macOS pinch from the page. SharpRail has no
browser, so the choice is between macOS's PDFKit and a managed binding to a cross-platform renderer:

- PDFKit's `PDFView` is a native `NSView`. Embedding it would need the Ghostty bridge's native-control path, which
  cannot run in the headless checks (so the fork's e2e could not be translated), and it would leave Linux, where
  SharpRail's CI runs the checks, with no viewer at all. Rasterizing through CoreGraphics' `CGPDFDocument` avoids
  the view but has no text extraction, so selection would need PDFKit through the Objective-C runtime anyway.
- PDFium, the renderer Chrome uses, through its C API: page rasterization at any scale into a buffer Avalonia draws,
  and per-character text with boxes for a selection layer, on macOS and Linux alike, in-process and headless. The
  prebuilt libraries come from bblanchon/pdfium-binaries through the NuGet packages `bblanchon.PDFium.macOS` and
  `bblanchon.PDFium.Linux`, exact-pinned at `[156.0.8076]` in `Directory.Packages.props`. `PdfEngine` binds the
  twenty functions it uses with `LibraryImport` rather than taking a wrapper package, so the only dependency is
  the native library itself.

PDFium is not thread-safe: every call runs under one lock, on a background thread. A document is opened from the
file's bytes for each read or rasterization and closed in the same call, so no native handle outlives it and a
closed tab leaks nothing; the bytes stay in the viewer for the next zoom. Where the native library is absent
(Windows), the viewer says rendering is not available on this platform.
The viewer releases replaced raster bitmaps, cancels reads on detach and reloads when mounted again.
Text and character boxes are indexed together from PDFium's Unicode values so omitted characters do
not shift the selection geometry.

## How the fork's viewer maps

- `PdfPage` (canvas plus pdf.js `TextLayer`) becomes a bordered page box holding an `Image` of the rasterized page
  and a `PdfTextLayer` over it. The image is rasterized at the zoom times the window's render scaling, so a page is
  sharp on a Retina display, which is the reason to render rather than scale a finished image.
- The text layer is a control rather than positioned spans: it keeps each character's box in points from the top
  left, scales them by its own width, selects by dragging (the character under the pointer, or the nearest one, so
  a drag from the margin still selects), paints the selection as a 40% wash of the kit's `TextSelection`, and copies
  with Command or Control+C (Command or Control+A selects the document).
  Selection spans the document: a drag can cross pages in either direction, Copy includes the whole
  range, and Select All covers every page. UTF-16 boundaries expand to preserve a complete Unicode
  character. PDFium maps character boxes through the rendered page's crop and rotation transform.
  A captured drag at the viewport edge scrolls continuously and updates its selection even while the
  pointer is stationary. Release, capture loss and detach stop scrolling. PDFium's UTF-16 surrogate
  pairs are combined before converting to a Unicode scalar; both units share the glyph's box.
- Zoom: the toolbar steps by `ZoomGesture.ScaleStep`; a Command- or Control-wheel and a trackpad pinch
  (`PointerTouchPadGestureMagnify`) zoom continuously, clamped by `ZoomGesture.Clamp`. These come from the kit's
  `Visualization.ZoomGesture`, the counterpart of the fork kit's `zoomGesture` (`clampZoomScale`,
  `zoomScaleForWheel`, `isZoomGesture`, `ZOOM_SCALE_STEP`), added to the kit with this plugin since SharpRail's kit
  had no shared zoom math.
- The live scale stretches the pixels rasterized last; the pages are rasterized again once the scale holds still
  for 120 ms (`PDF_RASTER_SETTLE_MS`), so a continuous pinch never asks for a render per event. A rasterization
  superseded by a newer read or zoom is dropped rather than shown.
- `?t={byteRevision}.{reloads}`: the viewer observes the file's own revision (`ObserveFileRevision`), not the
  workspace's, so a LaTeX build writing a dozen files beside the PDF does not redraw an unchanged document, and
  Reload from disk reads again for a change no watch reported. Revisions advance through the host's file-change
  stream for both embedded and remote workspaces.
  Watch readiness rechecks open document paths so a compiler rewrite between the first read and
  watch registration cannot leave the initially rendered PDF stale.
- An unreadable file shows "This PDF could not be read: …" with Try again, in place of the toolbar and pages.

## Checks

`tests/SharpRail.Checks/PdfPreviewChecks.cs`, run by the full suite, `-- --plugins` and `-- --pdf-preview`, with the fork's
minimal one-page fixture generated with correct offsets for each text:

- the engine: page size and text, a character's box from the top left, rasterizing at a scale, and a non-PDF
  refused with a reason;
- the fork's `pdf-preview.spec.ts` through embedded and real gRPC hosts: the page rasterizes, zooming in rasterizes it
  larger and reset returns to 100%, a pointer drag across the line selects "ThinkRail PDF", the toolbar shows and
  no Markdown chrome does; a rewrite, a delete-and-rename and a Reload each show the new text without reopening the
  tab;
- the fork commit's dormant rule: turned off in Settings › Plugins, the open tab offers the file as text and another
  `.pdf` opens as text.
- a held file-watch subscription: render the original PDF, rewrite before watching starts, then
  release registration and show the fresh bytes without another edit or Reload gesture.

## Remaining port gates

Selection across visible pages, clipboard copying, Unicode selection boundaries and rotated/cropped
geometry have checks, as do drag autoscroll and native Unicode extraction from a PDF ToUnicode map.
The toolbar and retry layout use compiled XAML. Control/Command-wheel, ordinary scrolling and
toolbar zoom bounds have checks. Trackpad magnify and load-race checks, native appearance,
published output and full-suite verification remain open.
