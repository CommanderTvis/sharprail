---
id: module-plugin-lsp-kotlin
type: module-design
status: draft
title: Kotlin (Kotlin LS) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Kotlin (Kotlin LS) language server plugin

The plugin id is `lsp-kotlin`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`kotlin` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: Kotlin Language Server · JetBrains.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

It shapes initialization like the IntelliJ server (no IntelliJ extensions, a projects array, the project JDK from SDKMAN with Maven/Gradle imports using it) and logs which JDK it chose.
