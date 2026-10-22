---
id: module-plugin-lsp-typescript
type: module-design
status: draft
title: JavaScript / TypeScript (TypeScript LS) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# JavaScript / TypeScript (TypeScript LS) language server plugin

The plugin id is `lsp-typescript`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`typescript` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: TypeScript Language Server · typescript-language-server project; Microsoft TypeScript engine.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).
