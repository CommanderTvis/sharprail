---
id: module-plugin-epub
type: module-design
status: active
title: EPUB Reader — .epub books in a reader tab, as a builtin plugin
parent: architecture
references: [module-plugin-api, module-plugin-ui-kit, submodule-ui-plugins]
tags: [plugins]
---

# EPUB Reader — .epub books in a reader tab, as a builtin plugin

## Responsibility

Open `.epub` files of a workspace in a reader: a table of contents beside the text, continuous scrolling or
viewport-sized pages, in-book and external links, images and the cover, and Find across the whole book. Reading
position is not remembered between openings.

Three projects under `Contract`, `Host` and `UI`: `SharpRail.Plugins.Epub` (manifest and wire contract),
`SharpRail.Plugins.Epub.Host` and `SharpRail.Plugins.Epub.UI`. The plugin is builtin and **disabled by default**;
Settings › Plugins enables it. While it is disabled nothing claims `.epub` and opening one behaves as before.

## Boundary

- Owns: reading and converting EPUB packages on the host (VersOne.Epub 3.3.6, Unlicense, pinned in
  `Directory.Packages.props`), the conversion to Markdown, a small converted-book cache, and the viewer control.
- Uses only Plugin API capabilities: the manifest's file viewer for the `epub` extension with read strategy
  `None`, `FileViewer` (W7), `ObserveFileRevision` (W14), request methods (H1/W1), and the UI kit's Markdown
  renderer and Find bar. Nothing in `SharpRail.UI` knows the plugin beyond the builtin arrays.
- The UI never reads the file: the host reads it under the workspace (paths that climb out are refused), so a
  remote host behaves like the embedded one.

## Contract

- `open {workspaceId, path}` returns the book: title, authors, one Markdown document holding every reading-order
  document in spine order, the one-based Markdown line each chapter starts on, and the table of contents
  (title, depth, line). A package that cannot be read answers with `error` set instead of throwing.
- `resource {workspaceId, path, href}` returns one package file's bytes (empty when absent); the viewer asks for
  images only.
- Links inside the book are rewritten to `line/<n>` and images to `res/<package path>`; external links keep their
  URL and open through the kit's ordinary link path (the system browser on desktop).

The host caches the last four converted books by absolute path, size and write time; a rewrite of the file
misses the cache and the viewer reloads on the file's revision.

## Conversion

XHTML is parsed as XML (named HTML entities are decoded first; a document that is still not well formed keeps its
words as plain paragraphs). Structure survives; presentation does not:

| XHTML | Markdown |
| --- | --- |
| `h1`–`h6` | headings of the same level |
| `p`, inline runs in `div`/`section`/`body` and other containers | paragraphs |
| `ul`, `ol` (with `start`), nested lists | lists |
| `blockquote` | block quotes |
| `pre` | fenced code |
| `hr` | thematic break |
| `table`/`tr`/`td`/`th` | pipe tables, first row as header |
| `dt` / `dd` | bold term / paragraph |
| `em`, `i`, `cite`, `dfn`, `var` / `strong`, `b` | emphasis / strong |
| `code`, `kbd`, `tt`, `samp` | code spans |
| `a href` | links (in-book or external); `id` and `a name` anchor the block that holds them |
| `img`, SVG `image` | images |
| `br` | hard line break |

Everything else contributes its text. `head`, `script`, `style`, `noscript`, `template` and MathML are dropped. All
CSS (style sheets, `style` elements and attributes), fonts, sizes and colours are ignored: text takes the theme's
foreground on the theme's surface, so a book styled black-on-white stays readable on a dark theme. Text is escaped
so nothing in a book is read as Markdown or HTML. The cover is shown first unless a chapter already shows it.
The TOC comes from the EPUB 3 nav document or the EPUB 2 NCX; without either, each chapter is an entry.

## Viewer

The open request and the Markdown parse run off the UI thread; the result is applied on the dispatcher only if the
tab is still open and the load is the latest. The kit renderer mounts only blocks near the viewport.

- Scroll mode reads continuously; the TOC sidebar (toggled by Contents) and in-book links scroll their target block
  to the top.
- Paged mode hides the scroll bar and shows one viewport-high page at a time, with Previous/Next buttons,
  Right/Page Down/Space/Down and Left/Page Up/Up, the mouse wheel and a horizontal touch swipe, and "Page X of Y"
  for the whole book. Page breaks come from a sweep over the document in 8 ms dispatcher slices at background
  priority: a page ends at the top of the first text line or image the viewport's bottom edge would cut (a mask
  hides the rest), and every chapter starts a new page. A resize, theme change, measured block or arriving image
  re-runs the sweep; the reader stays on the block (and the share of it) the page started in.
- Find is the kit's Find bar (Mod+F, Enter / Shift+Enter, `i/n`, Escape) over every chapter; the current match is
  selected. In paged mode the viewer turns to the page holding the match.

## Platforms

The UI half ships wherever the builtin UI halves do: desktop, Browser and Android; the host half with the host.
VersOne.Epub is a managed .NET Standard 2.0 library with no package dependencies; the Browser AOT build (trim mode
`copy`, which keeps whole assemblies) and the Android build include it unchanged. Touch swipe turns pages on Android and touch browsers.

## Verification

`--epub` (gate case `epub-reader`) generates an EPUB 3 package with a nav document and an EPUB 2 package with an
NCX (three chapters with headings, lists, in-book and external links, an image, a cover and black-on-white CSS)
and checks: conversion of both; disabled by default with no viewer; enabling in Settings › Plugins and opening;
TOC jumps; in-book link; external link URL; images and cover rendering; theme text colour and at least 4.5:1
contrast in dark and light themes; Find across chapters with stepping and selection; paged mode page count,
Next/Previous and arrow keys, chapters starting pages, no line cut at a page's bottom edge, a TOC jump, a resize
that keeps the location, Find turning pages; the EPUB 2 TOC; and a 6,000-paragraph book opening and paginating
with no UI-thread stall over 250 ms. It runs against the embedded host and a remote host.