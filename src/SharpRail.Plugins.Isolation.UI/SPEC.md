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
  `IIsolatedCallbacks`; a factory answers the surface `IIsolatedCallbacks.Surface` gives. It refuses JSON null
  where the API takes a non-nullable reference, and passes the settings object of `UpdateSettingsAsync` with its
  null members dropped.
- `RemotePluginUIModule`: an external plugin's UI half as a client holds it. It opens an `IIsolatedUISession`
  (refusing a runner whose UI table differs), replays the registrations onto the client's real context during
  `Activate`, failing the activation if a replayed call would wait rather than hold the UI thread, then dispatches
  each later call on the UI thread. When the session ends on the host's side it tells its opener, so the client can
  open a new one with a replaced process. A factory answers the control the client's surface factory makes from
  a way to open surfaces of the session (the app's `RemoteSurface`, which opens a new one when shown again after
  its surface closed), each under the client's kit theme; a resolver answers
  from its last answer for those arguments, or as if the plugin had none, and asks the plugin in the background,
  re-evaluating the plugin's predicates when it answers, so the UI thread never waits on the plugin. Before it
  answers a call it brings the plugin's settings mirror up to date.

## Boundary

- References `SharpRail.Plugins.Isolation` and `SharpRail.Plugins.Api.UI`; NativeAOT code, no reflection.
- Host projects never reference it: the host takes no UI dependency. The relay's Avalonia-free copy of the UI table
  is pending (slice 14 of the design).
- No shipping composition uses it yet: the checks hand `RemotePluginUIModule` to `Workbench.IsolatedPlugins`, and
  the runner references the generated tables.
