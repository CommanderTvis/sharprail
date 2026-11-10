---
id: submodule-ui-kit-action-icons
type: submodule-design
status: active
title: Menu action icons
parent: module-plugin-ui-kit
---

# Menu action icons

Every icon that appears in a context menu or drop-down menu names a meaning (`ActionIcon`), and
`ActionIcons` maps each meaning to exactly one shipped icon. Menu builders take the meaning
(`Ui.Menu(label, action, ActionIcon.Delete)`, `ActionIcons.Create`), never an asset name, so no menu can
show its own variant of "Delete".

## Rules

- One meaning, one icon; one icon, one meaning. The registry has no two meanings sharing an asset.
- Destructive removal (delete a file or folder, remove an item) uses the Delete icon.
- A new meaning is added to `ActionIcon` and the table in `ActionIcons.cs` with an icon from
  `Assets/Icons` (the Remix set, sources in `Assets/Icons/sources.json`); a glyph that is missing is added there in the
  same format (24x24 filled path). It is never chosen inline at the menu.
- Actions that copy a different form of the same object keep distinct meanings, so neighbouring rows never show the same
  glyph: Copy path (relative to the project root) uses CopyPath, Copy absolute path uses CopyAbsolutePath.
- Menus may show icons on some rows only. Avalonia's menu shares one icon column across the rows of a menu, so rows
  without an icon keep their labels on the same edge as rows with one. Menu icons are 14 px, tinted by the menu item.
- Plugin identity glyphs (the Claude mark on "Mention ... in Claude Code", agent launcher marks) say who
  acts, not what happens; they come from the plugin (`PluginIcons`) and are outside this registry.
- Every continue action of every agent uses Continue and every resume action uses Resume. "Resume as a new session",
  "Teleport a session here…" and "Fork a session…" show no icon. omp has no launcher menu with such entries.
- A state mark such as the checked theme uses Check.

## Table

| Meaning | Icon | Used by |
| --- | --- | --- |
| Delete | `trash` | Files tree: Delete file, Delete folder; workspace menu: Remove worktree… |
| NewFile | `fileAdd` | Files tree: New file |
| NewFolder | `folderAdd` | Files tree: New folder; add-project menu: Create project… |
| CopyPath | `route` (Remix route-line, the way from the project root) | Files tree: Copy path |
| Add | `add` | Scope menus: Add project, New workspace; project menu: Start work |
| Close | `close` | Project menu: Close project; dock tab menu: Close |
| OpenFolder | `folderOpen` | Add-project menu: Open project; external workspaces in the switcher |
| Home | `homeFill` | Scope menu: Project home; the default workspace in the switcher |
| Branch | `gitBranch` | Worktree workspaces in the switcher |
| Check | `check` | Selected theme in Settings |
| Continue | `play` | Agent launcher menus: Continue the last conversation (Claude Code), Continue the last session (Codex) |
| Resume | `history` | Agent launcher menus: Resume a session… (Claude Code, Codex) |
| EnterPath | `inputCursor` | Add-project menu: Enter path…, Enter host path… |
| Clone | `gitFork` | Add-project menu: Clone repository… |
| CopyAbsolutePath | `mapPin` (Remix map-pin-line, a fixed location) | Files tree: Copy absolute path; project and workspace menus: Copy absolute path |
| Settings | `settings` (the gear) | Browser unified-chrome overflow menu: Settings… |
| Pin | `pushpin` | Project menu: Pin; the mark on a pinned project row |
| Unpin | `pushpinFill` | Project menu: Unpin |

## Enforcement

Check mode `--conformance` (and the full suite's `action-icons` case) verifies that
every meaning has exactly one registry entry, that assets are distinct and shipped, and scans every source file that builds
menu items: an `Icon =` assignment from `Ui.Icon(` or a string literal fails. Plugin and registry assignments pass. The scan
sees assignments in files that mention `MenuItem` or `Ui.Menu(`; it does not follow an icon passed through a helper.

## Icon slot

Every menu icon sits in one `LayoutRoles.MenuIconSlot` box (16 px, set once in `App.Initialize`), centred both ways and never clipped, whatever its glyph size; rows without an icon reserve the same column.
The check `MenuIconAudit` runs on every context menu any headless check opens and fails unless each icon lies inside its slot and every clipping ancestor, is centred within 0.5 px, and the rows' text starts on one edge.

## Mixed menus

Fluent shares the icon, shortcut and chevron columns across a menu's rows, but the shared widths only settle after the first
layout pass. A menu that mixes icon rows, plain rows and a submenu row therefore measures narrower on its first pass than once
settled, and a popup sized from that pass clips the icons and chevrons. The app re-measures every context menu right after it
opens (`App.Initialize`), and check mode `--menu-layout` (also in `--ui-smoke`) requires such a menu to be as wide as its
widest row with the icon and chevron inside the menu padding.

The icon sits as far from the menu's edge as from its row's text (one 10 px gap, set once in `App.Initialize`), and a submenu
opens flush against its parent menu's visible edge: the app measures the gap once the submenu is shown and shifts the popup
by it, so no per-menu offset is needed. A confirmation dialog opened from a popup (the branch card's Delete branch) closes
the popup first, so it is never covered.
