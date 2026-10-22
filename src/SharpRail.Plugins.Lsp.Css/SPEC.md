---
id: module-plugin-lsp-css
type: module-design
status: draft
title: CSS (VS Code) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# CSS (VS Code) language server plugin

The plugin id is `lsp-css`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`css` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: VS Code CSS Language Server · Microsoft; vscode-langservers-extracted packaging.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).
