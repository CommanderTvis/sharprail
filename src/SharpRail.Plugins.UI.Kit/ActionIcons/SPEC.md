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
- Related actions share a meaning when they do the same thing to a different form of the same object:
  Copy path and Copy absolute path both use CopyPath.
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
| CopyPath | `fileCopy` | Files tree: Copy path, Copy absolute path; project and workspace menus: Copy absolute path |
| Add | `add` | Scope menus: Add project, New workspace; project menu: Start work |
| Close | `close` | Project menu: Close project; dock tab menu: Close |
| OpenFolder | `folderOpen` | Project menu: Open existing worktree; add-project menu: Open project; external workspaces in the switcher |
| Rename | `pencil` | Project menu: Rename |
| Home | `homeFill` | Scope menu: Project home; the default workspace in the switcher |
| Branch | `gitBranch` | Worktree workspaces in the switcher |
| Check | `check` | Selected theme in Settings |
| Continue | `play` | Agent launcher menus: Continue the last conversation (Claude Code), Continue the last session (Codex) |
| Resume | `history` | Agent launcher menus: Resume a session… (Claude Code, Codex) |
| EnterPath | `inputCursor` | Add-project menu: Enter path…, Enter host path… |
| Clone | `gitFork` | Add-project menu: Clone repository… |

## Enforcement

Check mode `--conformance` (and the full suite's `action-icons` case) verifies that
every meaning has exactly one registry entry, that assets are distinct and shipped, and scans every source file that builds
menu items: an `Icon =` assignment from `Ui.Icon(` or a string literal fails. Plugin and registry assignments pass. The scan
sees assignments in files that mention `MenuItem` or `Ui.Menu(`; it does not follow an icon passed through a helper.
