---
id: module-plugin-runner
type: module-design
status: active
title: Plugin runner — the JIT process an external plugin runs in
parent: module-plugin-security
references: [module-plugin-isolation, module-plugin-isolation-ui, module-plugin-isolation-generator]
tags: [plugins, security]
---

# Plugin runner

## Responsibility

The `SharpRail.PluginRunner` executable. The host starts it with its channel (descriptor 3, or `--channel <handle>`
on Windows) and it exits when the channel ends. It marks the channel close-on-exec (or not inheritable) first, so
nothing it starts holds it.

- `plugin`: one external plugin's process. `Init` names the plugin's directory, its UI assembly and its assets;
  `PluginLoadContext` loads the assembly, sharing every assembly the runner ships (its trusted platform list) and
  taking the rest from the plugin's directory only, and the runner answers `Ready`, or `Ready` with the reason it
  could not. Each client context the host lends gets its own activation through the generated proxy: one-ways
  `Activate` makes on it are kept and answered in the activation's reply, settings arrive as mirrors and reach
  `OnSettings` observers, `ReadAssetAsync` reads inside the assets directory, and the log goes to standard error.
  The plugin's code runs on the Avalonia UI thread of `RunnerPlatform`: SharpRail's remote platform backend
  (`RemotePlatform`) with Skia's CPU raster, set up on the main thread, with Fluent, the kit's styles and the theme a client pushed. A factory runs
  only when the host opens a surface for it (`surface.open`); `SurfaceRenderer` shows its control in its own
  top-level, renders it on a timer while the client has taken the last frame, diffs 64-pixel tiles, sends
  scrolling as moves (a `ScrollViewer.ScrollChanged` class handler) and applies the client's input, focus and size.
  A keyframe request (`surface.keyframe`, from a client that reconnected) stops waiting for the frame in flight and
  sends the next frame whole, from transparent, marked as a keyframe; the surface's popups do the same.
  Popups are top-levels of their own, at the surface's scale. Each tick
  `SurfaceRenderer` walks the surface's open popups (a submenu's under its menu's), numbers each new one from 1 and
  sends `surface.popup` with its place from the surface's corner and its size, again when either changes, and its
  closing after its last frame; each popup has its own frames, the first a keyframe, and its own input. A popup that
  asked for its surface's overlay stays inside the surface. `surface.dismiss` (the client hid every popup) closes them
  all, the newest first, a tooltip through its control so hovering opens it again; Escape while a popup that takes
  input is open closes the newest such popup and goes no further. A tooltip is sent as a popup that takes no input.
  `RemotePlatform` is the backend: an `IWindowingPlatform` whose every window and popup is a `RemoteTopLevel`
  (`IWindowImpl`, `IPopupImpl` and a framebuffer surface Skia draws a whole frame into, in memory), a render timer on
  the UI thread that the renderer ticks before it reads a frame, a cursor factory of standard shapes, and a mouse per
  top-level. Each input event runs the dispatcher's pending jobs and renders before and after it, as Avalonia's
  headless platform did, so what one event changed is drawn before the next.
  Client messages run through one queue in arrival order, because applying input runs the dispatcher's pending
  jobs. With `SHARPRAIL_RUNNER_DIAGNOSTICS=1`, which only the checks set, `surface.capture` answers a fresh render
  and named controls' bounds.

The probe modes load no plugin:

- `probe echo`: the handshake, then the `RunnerProbe` members, including ones that break the protocol on purpose
  so the checks can see the host refuse them.
- `probe avalonia-private`: registers a stub `IWindowingPlatform` through `AvaloniaLocator` and prints whether it
  resolves.
- `probe ui-context`: a UI-only plugin through the generated `IPluginUIContext` proxy, on a context handle the host
  lends: registrations, a notification, a preference, settings, a mirrored read and pending members. Its factories
  make no control, since the probe has no Avalonia platform; the surface they answer names their arguments.

The isolation generator (`SharpRailIsolation` = `runner`) writes both contexts' member tables, proxies and callback
invokers into the runner, and `Hello` carries the tables' hashes (`RunnerTables.Hash`: the host context's, a dot, the
UI context's). `PeerRuntime` connects the proxies to the channel and keeps the mirrored values. `plugin` mode
implements `IPluginUIContextLocal` (id, logger, settings observers, assets; `OnReconnect` never fires, since the
process never reconnects) except `WatchHost`, which throws until the host projection is mirrored.

## Boundary

- The one `src` project that is not NativeAOT-analyzed: it is the JIT process, and plugin loading
  (`AssemblyLoadContext`) will live here and nowhere else.
- The only project that compiles against Avalonia's private and unstable API (`AvaloniaAccessUnstablePrivateApis`,
  AVA3001 suppressed), for the remote platform backend (Security.SPEC.md, UI halves).
- References `SharpRail.Plugins.Isolation`, `SharpRail.Plugins.Isolation.UI`, Avalonia, and the kit and the chat
  contract (`SharpRail.Plugins.Chat`, `SharpRail.Plugins.Chat.UI`), which it shares with the plugins it loads. The
  checks copy its framework-dependent build into `plugin-runner/` of their own build output; packages do not ship it
  yet. `Avalonia.Skia` and `Avalonia.HarfBuzz` draw plugin views on the remote backend; the runner does not reference
  `Avalonia.Headless`.
