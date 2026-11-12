---
id: module-plugin-isolation-generator
type: module-design
status: active
title: Plugin isolation generator — member tables, proxies and dispatchers from the API's crossing attributes, and the rules that keep them complete
parent: module-plugin-security
references: [module-plugin-isolation, module-plugin-isolation-ui, module-plugin-runner]
tags: [plugins, security]
---

# Plugin isolation generator

## Responsibility

A Roslyn incremental generator that reads the plugin API's symbols, its `[PluginAmbient]`, `[PluginPermission]` and
`[PluginCrossing]` attributes and `src/Tereditor.Plugins.Api/isolation-pending.txt`, and writes the code that
carries the API across the plugin channel. [Security.SPEC.md](../Tereditor.Plugins.Api/Security.SPEC.md) under
"Classified in source" states what is classified and the rules SRI001 to SRI006; this project applies them and
reports each as a build error.

A project opts in by importing `Isolation.Generator.props` and setting `TereditorIsolation`:

- `trusted-host` (`Tereditor.Plugins.Isolation`): the host context's member table and dispatcher.
- `trusted-ui` (`Tereditor.Plugins.Isolation.UI`): the UI context's member table and dispatcher.
- `runner` (`Tereditor.Plugins.Runner`): both tables; for each root context the proxy (`IsolatedPluginUIContext`),
  the interface of what the runner implements itself (`IPluginUIContextLocal`), the invoker of lent callbacks
  (`PluginUIContextCallbacks`), and a proxy for each interface a `LocalInRunner` member hands out
  (`IsolatedPluginPreference`).

Shapes it produces: calls (`Task` and `ValueTask`), one-ways, mirrored reads, `LocalInRunner` members, records split
into data and callback handles, callbacks that are factories, asynchronous, `void` or synchronous by crossing class,
and generic members (the trusted side closes them over `JsonObject`, with null members dropped, or `JsonElement`).
A dispatcher refuses JSON null for a non-nullable reference parameter, record member or accessor argument with
invalid arguments, since System.Text.Json does not check the annotation on a value it reads alone. Anything else that is not pending
is SRI002, so the build fails before a wrong proxy exists: handles returned by a call, `Blocking`, a synchronous
`IDisposable` result, by-name contracts, handles inside records.

The tables go into the project's root namespace, so the runner's and the trusted side's never clash. Both are built
from the same symbols and the same pending list, so their hashes agree unless one side is stale.

## Boundary

- netstandard2.0 against `Microsoft.CodeAnalysis.CSharp` 4.14, below the pinned SDK's compiler, so the compiler
  can load it. It is a compiler component, outside the NativeAOT analysis (`Directory.Build.props`).
- Generated code has no reflection: arguments go through `PluginJson`'s registered contexts.
- Reachability starts at the non-pending members, so pending parts of the API need no classification beyond the root
  contexts' own members. The pending-list stale check runs where both roots are built (the runner).
- SRI005 covers classes in the consuming project, which is where hand-written proxies live.

## Checks

`--plugin-crossing` pins both tables' hashes, checks the ids are FNV-1a of the documentation-comment ids, and runs the
generator on in-memory snippets: a clean one generates code that compiles without warnings in every mode, and each
rule fails a snippet that breaks it. It then runs the runner's `probe ui-context` against the generated dispatcher.
