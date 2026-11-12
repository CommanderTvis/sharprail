# Using tereditor

## Get started

1. Open a project with **+** in Projects or ⌘O (Ctrl+O on other platforms). It opens on its Project Home.
2. Create a workspace with ⌘N, or work in the project folder.
3. Click a folder's name in Files to expand it. Single-click a file to preview it, and double-click or press Enter to
   keep it in a tab.
4. Use Changes to review diffs.

## Workspaces

- Keep each task in its own Git worktree. Create, switch, rename and remove workspaces from Project Home.
- Start work offers a new worktree on its own branch from a base you pick, or the project folder itself.
- Claude Code and Codex can create and remove their own worktrees too. See [Agents](agents.md).
- Settings › Projects can list a workspace an agent created under the terminal or chat that created it (off by default).
- New worktrees start with your mise, direnv and Python environments applied. Chips under each terminal show what was
  applied.

## Layout

- Arrange tabs and split panes to suit the task.
- Open another window with ⌘⇧N. Each window keeps its own layout, while projects and settings stay in sync.
- Choose a bundled theme, including high contrast, or follow the system with your preferred light and dark pair.
- File-type icons are built in. Turn them on or off in Settings › Appearance.

## Keymap

- Settings › Keymap picks the keymap preset: the built-in IntelliJ one (macOS or Windows/Linux flavour, whichever matches your system) or a custom one. The choice applies at once to every window.
- New preset duplicates the selected preset under a name you type; Rename, Duplicate and Delete work on custom presets. Deleting the active one returns to IntelliJ. IntelliJ itself is read-only: the first edit offers to make a custom copy.
- Select an action and press Edit (or double-click it), then press the new shortcut. Escape cancels; Backspace or Delete clears it. Each action holds up to two shortcuts. A shortcut another action uses is reported by name and can be reassigned; keys terminals need (for example Ctrl+C) are refused. Reset to preset puts a row back to the preset the copy was made from.
- Actions are grouped by area (General, Navigation, View, Tabs and windows, Language), and each plugin's actions form a group of their own under Plugins. Click a group to collapse it; search looks across every group and opens the ones with a match. The page shows how many shortcuts are used twice, and Reset group puts a whole group back to its preset in a custom preset.
- Language covers the editor's language-server actions: Go to Declaration (⌘B or F12; F12 on Windows and Linux), Go to Type Declaration (⌘⇧B), Go to Implementation (⌘⌥B), Find Usages (⌥F7), Document Symbols (⌘F12), plus Workspace Problems and the language server controls, which have no shortcut until you give them one. The editor's context menu and the Edit menu show the current shortcuts.
- A plugin's actions keep your shortcuts when the plugin is turned off: they stop working and the page lists them as plugin not available, then come back when you turn the plugin on again.
- Text-editing keys, terminal keys, chat composer keys, find and save are not part of the keymap.

## Files and editing

- Edit text files with TextMate syntax highlighting and save with ⌘S.
- Read Markdown with tables, images, callouts and Mermaid diagrams, or edit its source.
- Read PDFs alongside your code, with selectable text, zoom and refresh when the file changes.
- Save or explicitly discard edits before closing a modified tab or window.
- A failed save keeps the buffer, and files changed on disk are never overwritten silently.

## Git

- Review staged, working, branch and commit diffs.
- Review Markdown changes as rendered documents as well as source diffs.
- Stage and unstage files.
- Open a pull request or push updates to one from the Review panel (the Review plugin).

## Terminals

- Each new workspace opens a terminal in the bottom panel. Add more from a pane's + menu.
- ⌘⇧J shows and hides the bottom panel, even from inside a terminal.
- Option acts as Alt on U.S. layouts, as in Ghostty, so Option+Backspace deletes a word.
- ⌘V pastes images as quoted file paths, saving PNG files under `~/.tereditor/clipboard`.
- Quitting the app ends local shells.
- Change the terminal renderer in Settings without restarting your shells. If Metal texture rendering fails, the app
  switches to Skia and keeps the shell running.

See [Terminal rendering](terminal.md) for how terminals draw.

## Agents and remote

ACP agents (Early support): see "ACP agents" in `docs/agents.md`.

- [Agents](agents.md) covers the agent panes, worktrees and environments.
- In the Claude Code chat, a dynamic workflow or monitor is one card: its name, description, phases, status and result, with the script folded away. Its completion updates that card instead of adding a message.
- [Remote hosts and clients](remote.md) covers working on a host from another desktop or [Android](android.md).
