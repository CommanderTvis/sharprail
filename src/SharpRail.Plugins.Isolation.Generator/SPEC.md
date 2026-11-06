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
`[PluginCrossing]` attributes and `src/SharpRail.Plugins.Api/isolation-pending.txt`, and writes the code that
carries the API across the plugin channel. [Security.SPEC.md](../SharpRail.Plugins.Api/Security.SPEC.md) under
"Classified in source" states what is classified and the rules SRI001 to SRI006; this project applies them and
reports each as a build error.

A project opts in by importing `Isolation.Generator.props` and setting `SharpRailIsolation`:

- `trusted-host` (`SharpRail.Plugins.Isolation`): the host context's member table.
- `trusted-ui` (`SharpRail.Plugins.Isolation.UI`): the UI context's member table.
- `runner` (`SharpRail.Plugins.Runner`): both tables.

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
generator on in-memory snippets: a clean one generates code that compiles, and each rule fails a snippet that breaks
it.
