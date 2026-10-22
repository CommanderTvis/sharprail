---
id: module-plugin-lsp-php
type: module-design
status: draft
title: PHP (Intelephense) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# PHP (Intelephense) language server plugin

The plugin id is `lsp-php`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`php` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: Intelephense · Ben Mewburn.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

It keeps Intelephense's workspace and global storage in SharpRail's per-workspace state and per-version cache unless the user set them.
