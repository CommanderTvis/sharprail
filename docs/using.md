# Using SharpRail

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
- New worktrees start with your mise, direnv and Python environments applied. Chips under each terminal show what was
  applied.

## Layout

- Arrange tabs and split panes to suit the task.
- Open another window with ⌘⇧N. Each window keeps its own layout, while projects and settings stay in sync.
- Choose a bundled theme, including high contrast, or follow the system with your preferred light and dark pair.
- File-type icons are built in. Turn them on or off in Settings › Appearance.

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
- Open a pull request or push updates to one from the Review panel.

## Terminals

- Each new workspace opens a terminal in the bottom panel. Add more from a pane's + menu.
- ⌘⇧J shows and hides the bottom panel, even from inside a terminal.
- Option acts as Alt on U.S. layouts, as in Ghostty, so Option+Backspace deletes a word.
- ⌘V pastes images as quoted file paths, saving PNG files under `~/.sharprail/clipboard`.
- Quitting the app ends local shells.
- Change the terminal renderer in Settings without restarting your shells. If Metal texture rendering fails, the app
  switches to Skia and keeps the shell running.

See [Terminal rendering](terminal.md) for how terminals draw.

## Agents and remote

- [Agents](agents.md) covers the agent panes, worktrees and environments.
- [Remote hosts and clients](remote.md) covers working on a host from another desktop or [Android](android.md).
