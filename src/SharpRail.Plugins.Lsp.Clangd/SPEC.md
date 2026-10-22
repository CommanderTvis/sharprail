---
id: module-plugin-lsp-clangd
type: module-design
status: draft
title: C / C++ (clangd) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# C / C++ (clangd) language server plugin

The plugin id is `lsp-clangd`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`clangd` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: clangd · LLVM project.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).
