# SharpRail

A C# and Avalonia prototype of Thinkrail's workspace for browsing projects,
previewing Markdown, and managing Git changes and worktrees.

- Arrange tabs and split panes with drag-and-drop or context menus.
- Preview Markdown with selectable text, tables, links, and images.
- Browse staged, working, and branch diffs; stage and unstage changes.
- Create, switch, and remove Git worktrees.
- Customize themes, fonts, and layouts. Your last project and open documents
  are restored on launch; settings are stored in `~/.sharprail`.
- Open local projects or connect to a remote host.

This is a prototype: source files are read-only, the terminal is a placeholder,
and editing, commit/push commands, and AI integration are not available.

## Run

From a source checkout:

```sh
scripts/bootstrap.sh
.tools/dotnet/dotnet run --project src/SharpRail.UI -c Release
```

To build a macOS app bundle:

```sh
scripts/publish.sh
open artifacts/SharpRail.app
```

## Get started

Open a project using **+** in Projects or ⌘O (Ctrl+O on other platforms).
In Files, single-click to preview; double-click or press Enter to keep a tab.
Use Changes to review diffs and Projects to manage worktrees. Press F5 to
refresh files and Git, or ⌘, to open Settings.

## Remote projects

After publishing, start the host and connect the app with the same token:

```sh
SHARPRAIL_TOKEN=your-token artifacts/host/SharpRail.Host.Remote
SHARPRAIL_REMOTE=http://127.0.0.1:54123 SHARPRAIL_TOKEN=your-token artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI
```

For another machine, set the host's `SHARPRAIL_BIND` to its Tailscale IP and
use that address in `SHARPRAIL_REMOTE`. Remote connections use cleartext HTTP/2
and require an encrypted network such as Tailscale. Choose a project directory
on the host when prompted.
