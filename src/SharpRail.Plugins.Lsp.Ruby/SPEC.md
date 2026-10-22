---
id: module-plugin-lsp-ruby
type: module-design
status: draft
title: Ruby (Ruby LS) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Ruby (Ruby LS) language server plugin

The plugin id is `lsp-ruby`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`ruby` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: Ruby LSP · Shopify.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

It allows `initialize` 15 minutes, since Ruby LSP sets up its bundle first.
