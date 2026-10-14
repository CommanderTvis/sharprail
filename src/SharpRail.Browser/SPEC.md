---
id: sharprail-browser
title: Browser — the workbench as a remote host client
type: module
status: implemented
---

# Browser client

The browser runs the same workbench as the desktop remote client. Feature parity is the contract: projects
and workspaces, docking and windows, files and editable source, resource previews, Git and Review,
terminal sharing and agent integrations, settings, and builtin and compatible external plugin UI.
Workspace files, Git, native processes, plugin hosts and shells run exclusively on the connected host.
The browser contributes connection, browser window surfaces, view-state persistence and platform services.

The client compiles the existing UI sources, XAML and assets under the same assembly identity, following
the Android composition. It excludes desktop host-serving entry points and composes remote adapters only.
The browser project is built separately so desktop builds need no WebAssembly workload. There is no second
implementation of the workbench and no reduced browser feature set.

## Transport and state

Browser, desktop and Android share the protobuf WebSocket RPC transport at the host's normal endpoint.
Calls dispatch directly to host adapters with multiplexing, receive-credit flow control, cancellation,
deadlines, structured errors and mutation replay. See Host.Protocol's spec for wire limits and lifetime.
The token is never included in a URL. The endpoint and token live in session storage; client-local view
state lives in local storage. Shared settings and workspace lifecycle belong to the host.
The horizontal/vertical tab mode and placement of vertical tabs in Projects follow the host, including live
changes. Pane geometry, strip width, selection and open documents remain client-local. On the first Projects-mode
catalog reconciliation for a host/workspace, a legacy browser profile moves its bottom terminals into the centre,
retaining the selected terminal when the centre had no selection. A persisted host/workspace marker makes this
repair happen once; later deliberate terminal placement is preserved. Older hosts keep the browser's local mode.

The standalone host serves a built bundle from `SHARPRAIL_BROWSER_ROOT` on `SHARPRAIL_PORT`, including
cross-origin isolation headers required by the threaded runtime. macOS browser platform detection accepts
both `MacIntel` and `macOS`, so standard fields, editors and terminal shortcuts use Command consistently.

## Native components and platform services

Pinned libghostty-vt and its existing bridge, Scintilla and SheenBidi compile into static WASM libraries
linked into the managed runtime alongside matching Skia and HarfBuzz assets. Ghostty allocations use the
runtime's allocator, including terminal pages, rather than growing an independent heap. Native draw callbacks use unmanaged function
pointers. The browser retains the shared Skia terminal renderer as a loading fallback, with host-owned session lifetime and replay. Read-only attachments receive recorded live alternate-screen
contents before incremental output, without resizing, taking over, or sending input to the holder.
Plain ASCII rows batch existing Skia text-blob calls inside native WASM, preserving glyph rasterisation,
positions, colors and draw order while reducing managed/native transitions. Rows with a cursor,
composition, complex text, selection, contrast correction, decorations or hovered links use the shared drawing path.
The native path also scans cells, resolves ordinary/inverse/faint colors and draws background runs,
so the managed interpreter does not process each cell of a common ASCII row. Browser checks
compare the batched and shared paths pixel for pixel. Runtime-loaded plugins remain supported;
the browser keeps its untrimmed managed runtime.
Clipboard images are uploaded to the host before their path is pasted into a shell.

The default terminal uses restty 0.3.0 with WebGL2. It
uses the existing host adapter, session lifetime and ordered output; streaming UTF-8
decoding preserves split characters at the restty string-input boundary. Its canvas
is hosted by Avalonia's native-control surface, not imported as a shared GPU texture.
Read-only shared terminals also use restty, retain the holder's grid and follow its
ordered resize notifications without sending input, terminal replies or resizes.
Skia is selected only when restty's bridge, module, fonts, WASM or GPU initialization
fails or exceeds the 30-second loading limit. Host attachment and stream errors do
not select another renderer. There is no query-string or settings override to Skia.
Font fallback, clipboard images, full scrollback reads, high-contrast treatment and
overlapping in-window UI still need parity work. Modal dialogs use the same themed backdrop
over the native terminal canvas as over the workbench, including nested dimming; closing
the dialog restores the canvas without detaching or resizing its host terminal. Restty's internal runtime API is
pinned; upgrades require repeating keyboard, resize, lifecycle and rendering checks.

PDFium runs its pinned official WASM distribution in separate memory; the browser adapter returns the same
page text, character boxes and BGRA rasters consumed by the shared viewer. Merman's pinned WASM package
returns SVG under the same renderer options. The build pins dependencies and verifies downloaded native
archives. A pinned current Binaryen accepts the target features emitted by the pinned Zig compiler while
the .NET SDK retains its own Emscripten toolchain.

Avalonia browser surfaces back the workbench and owned dialogs. The workbench fills the browser
viewport and restores one workbench from the saved profile; the browser adds no window selector
or New/Close window toolbar. Browser focus drives the shared
notification gates; notification clicks return to the target terminal. Permission is requested from the
existing Settings gesture. GitHub authentication is probed by the host, never by a browser-side process.

## Acceptance

A Release browser build, the strict desktop solution build and relevant checks must pass. Verification
must load the deployed bundle in a real browser and exercise host authentication, editable source save
and conflict handling, live terminal input/output and reconnection, PDF text/raster rendering, Mermaid,
settings persistence, window/dialog interaction and builtin plugin surfaces. Compilation alone does not
establish parity. Remaining verification is recorded in the repository's completion and validation records.

The restty DOM surface forwards file drag events to its Avalonia container with window-relative
coordinates. The shared terminal drop flow uploads browser-local bytes before invoking the active
agent accessory. Restty's own input handlers do not process these drops. Forwarding listeners are
removed with the surface.
