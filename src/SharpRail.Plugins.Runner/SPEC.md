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
  Factories are refused before they run, since views are not streamed yet.

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
  yet.
