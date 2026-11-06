---
id: module-plugin-isolation-ui
type: module-design
status: active
title: Plugin isolation UI — the trusted side's UI half of the isolated runtime
parent: module-plugin-security
references: [module-plugin-isolation, module-plugin-isolation-generator]
tags: [plugins, security]
---

# Plugin isolation UI

## Responsibility

The part of the trusted side's isolated runtime that names UI API types, which reference Avalonia and so cannot live
in `SharpRail.Plugins.Isolation`:

- `IsolationUiJsonContext`: the by-value UI API types that cross the channel, with their enum converters. The
  generator's SRI003 rule names a missing one.
- `PluginUIContextMembers` and `PluginUIContextDispatcher`, generated (`SharpRailIsolation` = `trusted-ui`): the UI
  context's member table and hash, and the dispatcher that replays a UI-only plugin's calls onto a real
  `IPluginUIContext`. It rebuilds registrations with delegates that call the plugin back through
  `IIsolatedCallbacks`; a factory answers the surface `IIsolatedCallbacks.Surface` gives.

## Boundary

- References `SharpRail.Plugins.Isolation` and `SharpRail.Plugins.Api.UI`; NativeAOT code, no reflection.
- Host projects never reference it: the host takes no UI dependency. The relay's Avalonia-free copy of the UI table
  is pending (slice 14 of the design).
- No composition uses it yet; only the checks and the runner reference it.
