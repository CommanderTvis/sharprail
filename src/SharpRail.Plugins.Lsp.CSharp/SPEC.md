---
id: module-plugin-lsp-csharp
type: module-design
status: draft
title: C# (Roslyn) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# C# (Roslyn) language server plugin

The plugin id is `lsp-csharp`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`csharp` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: Roslyn Language Server · Microsoft.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

It waits for Roslyn's `workspace/projectInitializationComplete` before serving requests while automatic project loading is on, and restores each distinct set of workspace projects Roslyn reports as needing a restore once.

Its project inputs are project and solution files, `.props`/`.targets` and `project.assets.json`; a change to one, or a created or deleted watched source, holds back incomplete answers until Roslyn logs `[LanguageServerProjectSystem] Completed (re)load of`. Avalonia `.axaml` views have no Roslyn watcher, so creating or deleting one is reported as a change of the owning `.csproj`.

Design-time builds (`DesignTimeBuild=true`, which Roslyn sets) skip the repository's native-library targets: in a fresh workspace their checked-out inputs are newer than the cloned outputs, and a failing native build made Roslyn drop the whole project (every `SharpRail.UI` reference was missing).
