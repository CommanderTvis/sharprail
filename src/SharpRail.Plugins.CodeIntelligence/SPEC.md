---
id: module-plugin-code-intelligence
type: module-design
status: active
title: plugin-code-intelligence — the switch for every language feature, as a builtin plugin
parent: group-plugins-code-intelligence
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

## Responsibility

The plugin `code-intelligence` is the one switch for the language features of the app. Builtin, enabled by default, wire
version 1, headless, no methods, channels, settings or UI half. Every language server plugin and Air Context require it.

## Where the line is

The host keeps the language services, the `lsp_*` agent tools, headless, the on-demand lifecycle and server
installation: agents and headless need them whatever the interface shows. The plugin governs what the interface shows.

## Dependency rule

A plugin lists the plugins it requires. Switching Code Intelligence off switches off every plugin that requires it, and
switching them back on is the user's: turning the base on does not bring them back. Enabling one of them while the base
is off is refused and says what it requires ("depends on code-intelligence, which is off"); the settings page offers
to switch the base on with it. A cycle is refused. This is the host's general dependency rule, not new.

## When the plugin is disabled

There is no Code Intelligence settings section, no Language tool tab, strip entry or catalog entry, no language keymap
group, and in the editor no hover, go to definition, usages, diagnostics decorations, quick info or status chip. A saved
layout that names the Language tool restores without it and keeps its slot. Enabling the plugin brings all of it back.
