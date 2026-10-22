---
id: module-plugin-lsp-swift
type: module-design
status: draft
title: Swift (SourceKit-LS) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Swift (SourceKit-LS) language server plugin

The plugin id is `lsp-swift`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`swift` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: SourceKit-LSP · Swift project.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

There is no managed installation: the server runs only from PATH, when the user allows PATH for it, falling back to `xcrun --find sourcekit-lsp` on macOS.

Xcode projects and workspaces are manifests themselves and are not searched.
