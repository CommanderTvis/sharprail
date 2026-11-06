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
on Windows) and it exits when the channel ends. Today it has only probe modes, which load no plugin:

- `probe echo`: the handshake, then the `RunnerProbe` members, including ones that break the protocol on purpose
  so the checks can see the host refuse them.
- `probe avalonia-private`: registers a stub `IWindowingPlatform` through `AvaloniaLocator` and prints whether it
  resolves.
- `probe ui-context`: a UI-only plugin through the generated `IPluginUIContext` proxy, on a context handle the host
  lends: registrations, a notification, a preference, settings, a mirrored read and pending members. Its factories
  make no control, since the probe has no Avalonia platform; the surface they answer names their arguments.

The isolation generator (`SharpRailIsolation` = `runner`) writes both contexts' member tables, proxies and callback
invokers into the runner, and `Hello` carries the tables' hashes (`RunnerTables.Hash`: the host context's, a dot, the
UI context's). `PeerRuntime` connects the proxies to the channel and keeps the mirrored values. The runner's own
implementation of `IPluginUIContextLocal` (logger, observers of mirrors, assets) comes with the UI mode; today only
the probe implements the part it uses.

## Boundary

- The one `src` project that is not NativeAOT-analyzed: it is the JIT process, and plugin loading
  (`AssemblyLoadContext`) will live here and nowhere else.
- The only project that compiles against Avalonia's private and unstable API (`AvaloniaAccessUnstablePrivateApis`,
  AVA3001 suppressed), for the remote platform backend (Security.SPEC.md, UI halves).
- References `SharpRail.Plugins.Isolation`, `SharpRail.Plugins.Isolation.UI` and Avalonia. The checks copy its
  framework-dependent build into `plugin-runner/` of their own build output; packages do not ship it yet.
