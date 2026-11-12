---
id: tereditor-browser
title: Browser — the workbench as a remote host client
type: module
status: implemented
parent: group-clients
---

# Browser client

The browser runs the same workbench as the desktop remote client. Feature parity is the contract: projects
and workspaces, docking and windows, files and editable source, resource previews, Git and Review,
terminal sharing and agent integrations, settings, and builtin plugin UI. External plugins are listed and
refused, as in the desktop app. The browser compiles what shows an isolated external plugin's views
(`RemoteSurface` and the client's `RemoteIsolatedUIHost`), but its composition leaves the seam null, and no browser
check runs one yet.
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
For loopback hosts, the connect form permits a blank token and requests approval in the desktop
app. It shows pending/cancel, denial, expiry and unsupported-host states; manual tokens remain
available. The app approval names the page’s actual Origin and describes access to projects, files,
settings, plugins and terminals. The browser stores its temporary credential only in session storage,
separate from the visible token field. Reload/reconnect reuses it; listener restart or revocation
rejects it and immediately returns the user to requesting approval when the state subscription
reports authentication rejection. Ordinary transport outages retain the existing reconnect behavior. Nonloopback hosts still require a token.

The token is never included in a URL. The endpoint and token live in session storage; client-local view
state lives in local storage. Shared settings and workspace lifecycle belong to the host.
Tabs live under their workspace in Projects on every client. Pane geometry, direction, selection and
open documents remain client-local. Legacy tab-mode settings have no effect. Older browser frames drop
their bottom zone and its automatic terminal before validation; the bottom placement repair marker is retired.

The standalone host serves a built bundle from `TEREDITOR_BROWSER_ROOT` on `TEREDITOR_PORT`, including
cross-origin isolation headers required by the threaded runtime. macOS browser platform detection accepts
both `MacIntel` and `macOS`, so standard fields, editors and terminal shortcuts use Command consistently.

## Phone layout

A narrow touch viewport gets the Android client's phone shell: the workbench composed compact, one page at a time
(Projects, the current terminal or editor, Tools, Settings) from a bottom bar, with touch affordances always shown.
It is the same shared workbench code as Android; only the decision and the page's own viewport handling are the
browser's. The page is a phone when its layout viewport is narrower than 600 CSS pixels, or when its primary pointer
is coarse and the screen's smallest side is under 600 (so a phone turned sideways stays a phone and a tablet keeps
the desktop frame). Once a phone, the page leaves the shell only past 640 pixels, so dragging a desktop window around
the breakpoint does not rebuild it. Pinch zoom on a phone does not change the layout viewport; browser zoom on a
desktop does.

Crossing the rule on resize or rotation rebuilds the workbench in the other shell. The windows, workspace, tabs and
open documents come back from the saved view state and the host, and a Playground tab's own host is kept. The
switch waits while a document has unsaved edits, an agent is working or the workbench owns processes, so a resize
never raises their close questions.

The page fits the visible area: the soft keyboard and the address bar resize the layout (`interactive-widget`) and
the visual viewport sets the window's height and top, notch and gesture-bar insets pad the workbench, and
pull-to-refresh is off. The browser Back button does not yet step out of a page. What only a real phone can show:
keyboard and address-bar behaviour on iOS and Android, safe-area insets, touch scrolling and long press in the
canvas, pinch, and the on-screen keyboard over terminals.

The host-less Playground greets a newcomer with a getting-started card, in the Default workspace's empty view and
on Welcome, and never in a hosted or desktop session. It opens with a short, truthful account of the tab: no server
or account; files, git history and keys are saved in the browser's storage and survive a reload but not cleared
site data; keys go only to the chosen model provider; clones pass through a public CORS proxy; the terminal is a
simulated shell without node or npm; only Pi runs in a tab, while Claude Code and Codex need a host. Three steps
follow, with links that open the clone dialog and the model providers settings. The wording changes with what the
tab really does.

The rail header keeps a small inset from the page's top edge where there is no window title row to clear.

## Tailscale

The connect screen offers "Connect over Tailscale" beside the address form. The tab then joins the tailnet as
its own device, like the Android embedded node, and reaches hosts only through it. The node is a Go-to-WASM build
of Tailscale v1.104.1 (`Tailscale.Wasm`, adapted from upstream `cmd/tsconnect`), roughly 29 MB raw and 4.7 MB
Brotli. It is never in the initial bundle: the host serves it under `tailscale/` beside the app, and
`tailscale/manifest.json` names the build, its files and sizes. `tailscale.js` is imported only when the option
opens. The option reads the manifest and Cache Storage, then shows "Download Tailscale (~N MB)"; nothing is
fetched until it is pressed. The download shows progress and can be cancelled; only a complete, size-checked
download is cached, under `tereditor-tailscale-<version>-<build>` (a new pin or bridge change gets a new cache and
older builds' caches are deleted). Later visits start from the cache. A server without the module, a failed
download and a node that cannot start show the reason with retry.

The node's identity (machine key, prefs) is kept in IndexedDB (`tereditor-tailscale`), read into a synchronous
map before start and written through in order, so a browser signs in once and rejoins as the same device
(non-ephemeral). One tab owns the node through a Web Lock; a second tab says Tailscale is already running.
Sign-in opens Tailscale's HTTPS login page in a new tab after an explicit click; if the browser blocks it, the
button reopens it from a fresh click. Administrator approval and connection states show as on Android. Once
running, the shared Tailscale picker lists online netmap peers that answer tereditor bootstrap and runs the
host-approved comparison-code pairing. Discovery, pairing HTTP and every RPC channel dial numeric tailnet
addresses through the node (`StreamHttpHandler` and `RpcTransports.RegisterStreamTransport`, registered
before the adapters and removed after them); names never reach system DNS and CORS does not apply, since the
traffic never uses the browser's fetch. Bytes cross the JavaScript bridge as base64 on the dispatcher. The
pairing secret is mirrored from the in-memory file system to local storage; the endpoint is remembered with a
tailnet flag, so a reload re-pairs through the node instead of connecting directly. Sign-out logs the node out,
wipes its IndexedDB state and forgets the pairing secret; Disconnect also forgets the secret. The module runs
without log uploads, SSH or exit-node support. Real tailnet sign-in, pairing and RPC from a browser need a
manual check with a Tailscale account.

## Native components and platform services

Pinned libghostty-vt and its existing bridge compile into a static WASM library
linked into the managed runtime alongside matching Skia and HarfBuzz assets. Ghostty allocations use the
runtime's allocator, including terminal pages, rather than growing an independent heap. Native draw callbacks use unmanaged function
pointers. The browser retains the shared Skia terminal renderer as a loading fallback, with host-owned session lifetime and replay. Read-only attachments receive recorded live alternate-screen
contents before incremental output, without resizing, taking over, or sending input to the holder.
Plain ASCII rows batch existing Skia text-blob calls inside native WASM, preserving glyph rasterisation,
positions, colors and draw order while reducing managed/native transitions. Rows with a cursor,
composition, complex text, selection, contrast correction, decorations or hovered links use the shared drawing path.
The native path also scans cells, resolves ordinary/inverse/faint colors and draws background runs,
so the managed interpreter does not process each cell of a common ASCII row. Browser checks
compare the batched and shared paths pixel for pixel. The browser keeps its untrimmed managed
runtime.

Release Build and Publish use hybrid WASM AOT for the assemblies in `BrowserAotAssemblies`
(`BrowserAot.targets`) and their shared generic instances; other assemblies remain interpreted.
This is not NativeAOT. The build fails when the selected or compiled set differs from that list.
Debug remains interpreted; TereditorBrowserAot=false explicitly opts out for diagnostics. The pinned
SDK requires the linker pipeline for Publish, but every resolved managed assembly is rooted and
uses copy mode, with IL stripping disabled. A mandatory SHA-256 comparison verifies every linked
assembly against its original bytes. JSON reflection remains enabled, though the tab's own JSON (the saved
endpoint, PDF pages, Git answers, Tailscale status) comes from source-generated metadata or JSON nodes.
Copy mode still performs trim analysis, so only this browser copy-only configuration disables the
trim analyzer and linker trim-analysis warnings; ordinary warnings and referenced-project analyzers
remain enabled. Build guards reject trimming, IL stripping, disabled reflection or a non-hybrid runtime,
and verify both selected and compiled AOT assembly sets. SDK upgrades must retain these guarantees.

### Browser AOT assemblies

| Assembly | Why it is compiled |
|---|---|
| System.Private.CoreLib, System.Collections, System.Linq | Strings, spans, collections and LINQ under every other hot path. |
| Avalonia.Base, Avalonia.Controls, Avalonia.Browser | Layout, rendering, input and the browser platform loop on every frame. |
| Avalonia.Skia, SkiaSharp, Avalonia.HarfBuzz, HarfBuzzSharp | Drawing and text shaping wrappers called per glyph run. |
| Tereditor.Scintilla | The managed code editor: document, layout, shaping and painting. |
| Tereditor.UI, Tereditor.Plugins.UI.Kit | Workbench, diff, Markdown, tree and terminal glue drawing. |
| Markdig, TextMateSharp, System.Text.RegularExpressions | Markdown parsing and syntax highlighting of whole documents. |
| Tereditor.Host.Protocol, Tereditor.Host.Client, System.Text.Json | Every host RPC message is serialized and dispatched through them. |
| System.Runtime.InteropServices.JavaScript | Terminal output and every other JS interop call cross it. |

Everything else stays interpreted: plugins (cold, activated on demand, many), the host core, XAML
loading, Fluent theme, SVG, YAML/TOML, XML and data serialization, and TextMateSharp.Grammars
(6.7 MB of grammar data with little code).
`restty.mjs --perf` (test-only bundle) prints startup, terminal, editor and diff timings for
comparing sets; pass `-p:BrowserAotAssemblies="..."` to build a candidate.

Normal published-bundle acceptance also opens restored prose and Mermaid, Settings and the Branch
graph builtin plugin performing a typed JSON request; test-only exports must be absent.

### Build and transfer settings

Release is selected by the `browser` task for build, run, publish and checks. Selective hybrid AOT, preserved IL/reflection,
SIMD, WASM exception handling and threads stay enabled. Release runs the post-link wasm-opt pass at `-Oz`
(`WasmOptConfigurationFlags`; `-p:TereditorWasmOz=false` turns it off for comparisons) and sets `WasmDebugLevel=0`, which drops the
browser-debugger component and the `.pdb` files from the boot config (browsers download every listed `.pdb` at start). Pages serves gzip
only, so `dotnet.native.wasm` is the largest single transfer.
Rejected after inspection: trimming (the preserved-assembly policy above is what keeps JSON reflection, plugin loading
and XAML working, and a trimmed set cannot be proven without a browser run), `InvariantGlobalization` (saves one
0.8 MB ICU shard but changes culture and normalization behavior) and lazy-loading plugin assemblies (all plugins
total 4.3 MB gzip and are referenced statically). Pi's JavaScript (`vendor/tabpi.js`, 0.58 MB gzip) is a dynamic
import fetched when Pi first starts. Its status request, which the Pi plugin sends at every start, used to import it
for the version number; it now reads the one-line `vendor/tabpi-version.js`. The Tailscale node is fetched only on
download. The standalone
`host` task now also defaults to Release, with the existing configuration override.

This project uses the plain WASM SDK rather than the Blazor static-web-assets pipeline.
Release Build and Publish generate Brotli quality 11 and gzip level 9 sidecars for compressible
runtime and vendor assets through `scripts/browser-compress.cs`. Files below 1 KiB and
representations that would grow use identity. SHA-256 keyed compressed bytes under
`$TEREDITOR_CACHE_DIR/browser-compression` survive SDK bundle regeneration; complete sidecars and cache entries
are published by atomic rename. The original
bundle remains deployable. Compression adds bundling CPU time and disk space, avoiding
compression CPU on every request. The standalone host negotiates fresh sidecars at original
URLs, preserving MIME types, COOP/COEP, HEAD and conditional caching with encoding variation.
Other deployment servers must implement equivalent sidecar serving. Managed assembly preservation
and selective AOT retain their existing policies. Unfingerprinted assets
retain normal conditional caching.

The restty DOM surface forwards file drag events to its Avalonia container with window-relative
coordinates. The shared terminal drop flow uploads browser-local bytes before invoking the active
agent accessory. Restty's own input handlers do not process these drops. Forwarding listeners are
removed with the surface.

The restty DOM surface sits above the canvas that draws popups (menus, flyouts). The window tells the page where
its open popups are (`cutNative`), and the page clips each terminal around them, so a popup over a terminal shows and
takes the click. A modal dims the terminal instead (`dimNative`). `restty.mjs --hover` checks a click on a menu item
over the terminal.

## Recorded rendering evidence

Dated Chromium samples (GPU memory return after Restty open/close cycles, about 60 GPU draws per second against a higher output rate) are
not budgets and do not show 120 Hz presentation, physical-display or other-engine acceptance. Output throughput, GPU draw cadence and
presentation are different measurements; the browser checks are the evidence, and a rerun replaces any number a past run produced.
