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

The isolation generator (`SharpRailIsolation` = `runner`) writes both contexts' member tables into the runner, and
`Hello` carries their hashes (`RunnerTables.Hash`: the host context's, a dot, the UI context's).

## Boundary

- The one `src` project that is not NativeAOT-analyzed: it is the JIT process, and plugin loading
  (`AssemblyLoadContext`) will live here and nowhere else.
- The only project that compiles against Avalonia's private and unstable API (`AvaloniaAccessUnstablePrivateApis`,
  AVA3001 suppressed), for the remote platform backend (Security.SPEC.md, UI halves).
- References `SharpRail.Plugins.Isolation`, `SharpRail.Plugins.Isolation.UI` and Avalonia. The checks copy its framework-dependent build into
  `plugin-runner/` of their own build output; packages do not ship it yet.
