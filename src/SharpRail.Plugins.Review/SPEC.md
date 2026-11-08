---
id: module-plugin-review
type: module-design
status: active
title: plugin-review — the Review tool window as a builtin plugin
parent: group-plugins-source-control
depends-on: [module-plugin-api]
tags: [v1, plugins]
---

## Responsibility

Owns the Review tool window in the right rail: a one-line summary of the workspace's changes, the active repo hosting
plugin's pull request review section, and a Show Changes button. Builtin, enabled by default, no dependencies, wire
version 1, category Version control, icon `discuss-line`, right side, `RequiresGit`. The host and UI assemblies
reference only their plugin API entry and shared contract; the UI also uses the kit.

## Where the line is

Core keeps the Changes review pane, the Files and Changes tool windows and the action that shows Changes; the pane's
button reaches it through the public tool-reveal call. The pull request section stays the repo hosting plugin's (for
example GitHub); this plugin only gives it a place, through the plugin context's repo host review section.

- `summary({ workspaceId })` returns whether the workspace is a repository, its branch and how many files differ
  from HEAD (untracked included, a rename counted once). An unknown workspace fails with its id.
- The pane refreshes while attached whenever the workspace's files change, and follows the workspace it is shown for.

## When the plugin is disabled

There is no Review tab, pane, reveal-menu entry or catalog entry, and the preset named Review seats its other tools
only. A saved layout that names the tool restores without it and keeps its slot, so enabling the plugin again brings
the tool back where it was. A layout saved before this plugin existed, naming the old core tool, reads as the plugin's.
Pull request actions elsewhere (the location strip, Open PR) are untouched.
