---
id: submodule-core-pdf-preview
type: submodule-design
status: active
title: PDF Preview — core PDF resource viewing
depends-on: [module-plugin-ui-kit]
references: [submodule-ui-panels]
tags: [ui, resources]
---

# PDF Preview — core PDF resource viewing

Upstream: packages/plugin-pdf-preview/SPEC.md @ ab23be7cf (CommanderTvis fork)

## Responsibility

Renders `.pdf` files opened from the Files tree or anywhere else a file opens: each page rasterized at the zoom it
is shown at, a selectable text layer over it, its own toolbar (page count, zoom level, Reload from disk, zoom out,
zoom in, reset), pinch and Command- or Control-wheel zoom, and a reload whenever the file's bytes change. It is always available as a core resource renderer, independent of plugin enablement.

## Core composition

`SharpRail.UI/Resources/Pdf` owns the viewer, compiled XAML, selection layer and PDFium engine.
The resource registry offers PDF preview ahead of the binary file card. Ordinary file tabs and
persisted legacy PDF viewer tabs use this same registry. No PDF plugin manifest, module, contract
or assembly remains. External file viewers retain their existing override behavior.

The workbench supplies byte reads and file-revision observation directly. These use the same
embedded/remote host services and shared revision stream as other documents; the viewer creates
no independent filesystem watcher. PDF retry, reload, raster disposal and detach behavior remain.
The browser supplies its existing PDFium WASM adapter from the core namespace; Android retains its
native PDFium package. File icons remain core UI-kit assets, controlled by Appearance.

Acceptance requires existing local/remote raster, geometry, Unicode selection, zoom and live-file
checks to pass without enabling a plugin; PDF must be absent from the plugin roster, its binary
fallback must remain selectable, and old viewer-kind tabs must restore into the core preview.

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
closed tab retains no native PDF document handle; the bytes stay in the viewer for the next zoom. Where the native library is absent
(Windows), the viewer says rendering is not available on this platform.
The viewer releases replaced raster bitmaps, cancels reads on detach and reloads when mounted again.
Text and character boxes are indexed together from PDFium's Unicode values so omitted characters do
not shift the selection geometry.

## How the fork's viewer maps

- `PdfPage` (canvas plus pdf.js `TextLayer`) becomes a bordered page box holding an `Image` of the rasterized page
  and a `PdfTextLayer` over it. The image is rasterized at the zoom times the window's render scaling, so a page is
  sharp on a Retina display, which is the reason to render rather than scale a finished image.
  Raster dimensions carry the backing density; bitmap metadata stays at 96 DPI so Avalonia's image
  source rectangle addresses the full pixel buffer. The explicit page bounds supply the logical size.
  Encoding the backing density in both places crops the raster on high-DPI displays.
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
  had no shared zoom math. The preview claims bubbling pinch gestures over its pages and toolbar so they never change application zoom.
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

- drawn portrait pages at 1× and 2× backing density: all four corner landmarks remain visible on both
  pages during reduced zoom and after raster replacement; image and selection bounds stay aligned;
- the engine: page size and text, a character's box from the top left, rasterizing at a scale, and a non-PDF
  refused with a reason;
- the fork's `pdf-preview.spec.ts` through embedded and real WebSocket RPC hosts: the page rasterizes, zooming in rasterizes it
  larger and reset returns to 100%, a pointer drag across the line selects "ThinkRail PDF", the toolbar shows and
  no Markdown chrome does; a rewrite, a delete-and-rename and a Reload each show the new text without reopening the
  tab;
- PDF remains available independently of the plugin roster; the resource toggle offers the binary file card.
- a held file-watch subscription: render the original PDF, rewrite before watching starts, then
  release registration and show the fresh bytes without another edit or Reload gesture.

## Remaining port gates

Selection across visible pages, clipboard copying, Unicode selection boundaries and rotated/cropped
geometry have checks, as do drag autoscroll and native Unicode extraction from a PDF ToUnicode map.
The toolbar and retry layout use compiled XAML. Control/Command-wheel, ordinary scrolling and
toolbar zoom bounds have checks. Trackpad magnify and load-race checks, native appearance,
full packaged-output and full-suite verification remain open.

The browser supplies the same pinned PDFium release as a separate WASM instance through `PdfPlatform`.
Its adapter returns the viewer's existing page text, character rectangles and premultiplied BGRA rasters,
including crop and rotation transforms. The shared viewer keeps selection and zoom behavior.

## Core migration verification

The core renderer passes the existing PDF-focused checks through embedded and real WebSocket hosts,
including 1×/2× page geometry, selection, zoom, retry, live rewrites and watch-readiness races.
A plugin-era `viewer` tab restores into the core PDF renderer, which is absent from the plugin roster;
the binary fallback remains selectable. Dependency-boundary, core file-icon and document-body checks
pass. Clone-dependent Git document scenarios were skipped without an upstream fixture source.

The desktop solution builds in Release with nullable/warnings-as-errors enabled; existing Avalonia
URI-constructor warnings remain visible. Browser and Android managed `Compile` targets pass with their
real project references and shared XAML; this is not WASM AOT, browser acceptance or physical-device
verification. A macOS framework-dependent publish contains a loadable, initializable `libpdfium.dylib`
and no former PDF plugin assemblies. PDFium notices remain in `licenses/PDFium.txt`, copied by the
existing package scripts; complete canonical-package and Windows runtime verification are separate.

PDF paper is an invariant white design effect; selection uses the theme selection colour at 40% alpha. Toolbar padding follows the shared spacing scale.
