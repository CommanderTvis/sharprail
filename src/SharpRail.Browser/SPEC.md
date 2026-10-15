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
The Error indicator opens actual failure details and a Retry action for the current workspace restore or refresh; subscription failures remain visible while the automatic reconnect loop continues. Browser connection tokens are removed from displayed failure text. Successful connections retain Change host.
The token is never included in a URL. The endpoint and token live in session storage; client-local view
state lives in local storage. Shared settings and workspace lifecycle belong to the host.
Tabs live under their workspace in Projects on every client. Pane geometry, direction, selection and
open documents remain client-local. Legacy tab-mode settings have no effect. Older browser frames drop
their bottom zone and its automatic terminal before validation; the bottom placement repair marker is retired.

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
Wheel events stay inside the terminal's DOM surface after its canvas handlers run, while native
scrolling remains enabled for read-only scrollback. They never reach Avalonia with a stale pointer
target from Projects; moving back to Projects restores its normal wheel scrolling. The terminal's
wheel ownership listener is removed during disposal. The focused `restty.mjs --wheel` check covers
writable and read-only scrollback, returning to Projects and listener teardown.
Skia is selected only when restty's bridge, module, fonts, WASM or GPU initialization
fails or exceeds the 30-second loading limit. Host attachment and stream errors do
not select another renderer. There is no query-string or settings override to Skia.
Font fallback, clipboard images, full scrollback reads, high-contrast treatment and
overlapping in-window UI still need parity work. Modal dialogs use the same themed backdrop
over the native terminal canvas as over the workbench, including nested dimming; closing
the dialog restores the canvas without detaching or resizing its host terminal. Restty's internal runtime API is
pinned; upgrades require repeating keyboard, resize, lifecycle and rendering checks.
Disposal explicitly loses the acquired WebGL context after runtime teardown, including
failed initialization; it never creates a context to dispose one. Restty 0.3.0's runtime
teardown alone leaves its base graphics objects live. The focused
`node tests/SharpRail.Browser.Checks/restty-lifecycle.mjs` check retains detached
canvases and graphics handles through 48 open/close cycles, verifies real rendering,
then checks context loss, invalid graphics objects and stopped drawing. It also covers
idempotent close, disposal before or during initialization and failure after graphics
allocation. Pass `--require-hardware` to reject software or unidentified terminal
renderers. It requires the pinned
browser dependencies, Chromium and the built browser vendor assets; an isolated
workspace can set `SHARPRAIL_BROWSER_ASSETS_ROOT` to a checkout with those assets.
CDP memory-infra dumps report GPU-process allocator bytes at cold, open and closed
checkpoints and a warmed open/close pair, alongside the actual renderer. Explicit
trace clock markers correlate each dump; missing or ambiguous samples fail the check.
`--require-memory-return` additionally requires a positive warmed open allocation and
GPU-process allocated bytes after close no greater than the warmed closed baseline,
without a tolerance. Without that flag, the memory outcome is reported separately
from the resource gate.
These are browser/driver allocation
measurements, not proof that physical GPU resident memory returns to the cold baseline;
ANGLE and the compositor may retain caches. Resource checks and memory evidence must
be reported separately, and a software renderer must not be called hardware validation.

PDFium runs its pinned official WASM distribution in separate memory; the browser adapter returns the same
page text, character boxes and BGRA rasters consumed by the shared viewer. Merman's pinned WASM package
returns SVG under the same renderer options. The build pins dependencies and verifies downloaded native
archives. A pinned current Binaryen accepts the target features emitted by the pinned Zig compiler while
the .NET SDK retains its own Emscripten toolchain.

Avalonia browser surfaces back the workbench and owned dialogs. The workbench fills the browser
viewport and restores one workbench from the saved profile. Its logical size follows the actual browser
surface even when the first resize arrives before Window ownership or a saved/constructor size is
reapplied without changing CSS. Window requests cannot leave the workbench smaller than its viewport.
Owned dialogs use their current CSS extent for logical size and resize notifications; delayed surface
observations cannot feed an old viewport size back into dialog layout. The browser adds no window selector
or New/Close window toolbar. Each canvas has one active renderer: the temporary embedded root that
`AvaloniaView` creates is stopped directly before the shared `Window` takes over its surface.
`TopLevel.StopRendering` only stops roots registered with the media context; `AvaloniaView` starts
its renderer directly, so that method would leave a competing empty renderer on the canvas.
Browser focus drives the shared
notification gates; notification clicks return to the target terminal. Permission is requested from the
existing Settings gesture. GitHub authentication is probed by the host, never by a browser-side process.

## Acceptance

A Release browser build, the strict desktop solution build and relevant checks must pass. Verification
must load the deployed bundle in a real browser and exercise host authentication, editable source save
and conflict handling, live terminal input/output and reconnection, PDF text/raster rendering, Mermaid,
settings persistence, window/dialog interaction and builtin plugin surfaces. Compilation alone does not
establish parity. Remaining cross-platform acceptance is tracked in [UPSTREAM.md](../../UPSTREAM.md#outstanding-acceptance-work).
Recorded Chromium passes do not establish Safari/Firefox, non-macOS keyboard/IME behavior, real
notification permission, production HTTPS/proxies or physical Retina acceptance.
The focused `mermaid.mjs` journey checks actual Markdown diagram controls on initial restoration, normal
opening, reload and rendered diffs, including invalid-syntax errors; a successful direct worker SVG call
alone does not establish that the workbench displays the diagram.

The focused `restty.mjs --settings` journey repaints the temporary embedded roots in magenta and checks
that neither the workbench nor Settings shows those pixels, then reopens sections and checks terminal
input after dismissal. Its emulated high-DPI variant exercises Avalonia's content-box fallback:
Playwright's `deviceScaleFactor` changes `devicePixelRatio` but leaves `devicePixelContentBoxSize`
unscaled ([Playwright #18591](https://github.com/microsoft/playwright/issues/18591)). Hiding that property
in this test uses the supported fallback with coherent CSS and backing dimensions; production code is
unchanged. This is emulated scaling coverage, not acceptance on a physical Retina display.

The restty DOM surface forwards file drag events to its Avalonia container with window-relative
coordinates. The shared terminal drop flow uploads browser-local bytes before invoking the active
agent accessory. Restty's own input handlers do not process these drops. Forwarding listeners are
removed with the surface.

## Recorded rendering evidence

The 2026-10-11 Chromium acceptance used an isolated real host and WASM controls for source save/
conflicts, live PTY reconnect, PDF Unicode/rotation/crop/pixels, Mermaid workers, modal settings and
external managed UI assembly/settings persistence. A separate normal-published-bundle smoke passed
with test hooks absent. These dated runs do not verify every later shared-UI delta.

The 2026-10-14 focused lifecycle probe passed 48 real Restty open/close cycles on Apple M4 Pro /
ANGLE Metal while retaining detached canvases and graphics handles. Chromium memory-infra reported:

| Checkpoint | GPU-process total bytes | ANGLE Metal bytes | Shared-image bytes |
| --- | ---: | ---: | ---: |
| Warm closed baseline, cycle 24 | 98,971,019 | 41,926,656 | 55,104,256 |
| Open, cycle 48 | 110,530,955 | 48,463,872 | 58,816,256 |
| Closed, cycle 48 | 98,971,019 | 41,926,656 | 55,104,256 |

These allocator categories overlap and must not be summed. The strict gate observed positive open
allocation and exact return to the warmed closed baseline; it did not measure physical GPU residency,
other drivers/engines or the full workbench lifecycle. Before explicit context loss, the pinned runtime
left its context and 12 graphics objects valid after teardown; the original bridge failed this probe.

The recorded 2026-10-13 headless Chromium Restty sample at 124×48 cells and DPR 1 counted 781 GPU-draw
frames in 13.006 seconds (60.05/s), matching the animation callbacks. The workload reported 111 output
FPS. Output throughput, GPU draw cadence and physical presentation are different measurements: this
sample proves neither 120 Hz presentation nor parity with desktop Metal. Earlier native-row Skia
measurements likewise reached about 60 redraws/s. These dated samples are not current performance
budgets or evidence that a transport change improves presentation.

Actual Chromium regression coverage on 2026-10-15 includes restored and reopened Markdown Mermaid,
invalid syntax, rendered diffs, writable/read-only terminal wheel ownership, listener teardown and
Settings viewport stability. Settings passed at 1840×1340 with native DPR 2 and 2560×1600 with DPR 1
and interface zoom 1.25. Test-build and emulated scaling passes remain distinct from normal-bundle
and physical-browser acceptance; intermittent startup/reload failures observed in earlier checks
were not established as resolved by the rendering fixes.
