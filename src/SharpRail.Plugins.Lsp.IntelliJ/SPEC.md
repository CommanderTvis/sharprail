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

The plugin owns the pinned distribution (SHA-256 per platform, JetBrains hosts only, bounded and traversal-safe
extraction), its terms review (the EULA bytes and a fingerprint of the whole installation) and the acknowledged
launcher (`--stdio --system-path <session directory> --eula <hash prefix>`, launcher debugging variables removed).
It replaces `java` and `kotlin` when chosen, accepts an installation directory, project JDK and build importer,
refuses overrides and its protocol arguments in configured ones, reads `jar:`/`jrt:` sources through the public
`decompile` command, and treats exit 7 as JetBrains refusing the build: a managed installation is then removed and
marked so it is not downloaded again; a user-selected one is never deleted. The Kotlin plugin keeps its own copy
of the shared JVM initialization.
