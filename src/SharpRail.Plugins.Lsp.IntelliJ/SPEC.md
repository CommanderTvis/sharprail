---
id: module-plugin-lsp-intellij
type: module-design
status: draft
title: Java / Kotlin (IntelliJ) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Java / Kotlin (IntelliJ) language server plugin

The plugin id is `lsp-intellij`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`intellij` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: IntelliJ Java and Kotlin · JetBrains · optional evaluation provider.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

The spec carries identity, files, provenance and project policy only. The distribution, its terms review and the acknowledged launcher stay in the host (`IntelliJDistribution`, `IntelliJTerms`), which recognises this server by its id; the Java / Kotlin provider setting chooses between this server and the `java` and `kotlin` plugins.
