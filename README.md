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
- Run local shell sessions in macOS terminal tabs using embedded libghostty and Metal.

This is a prototype: source files are read-only,
and editing, commit/push commands, and AI integration are not available.

## Run

From a source checkout:

macOS terminal builds require Xcode, LLVM at `/opt/homebrew/opt/llvm`, and its
Metal Toolchain (`xcodebuild -downloadComponent MetalToolchain`). The build downloads pinned Ghostty 1.2.3
and Zig 0.14.1 into `.tools`, then links libghostty into the native bridge.
No installed Ghostty application is required.

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

On macOS, choose New terminal from a pane's + menu or context menu. Each tab runs
a shell in that local workspace. Sessions survive tab moves and workspace
switches; closing a terminal ends its session. App restart starts fresh shells.
Remote terminals and terminals on other platforms are not available yet.
The terminal follows the UI's dark/light colors. ⌘V pastes images as quoted file
paths, saving PNG files under `~/.sharprail/clipboard` (or the active profile's
`clipboard` directory). These files remain available after closing the terminal.

Verify native terminal execution and Metal pixels with `sh scripts/check-terminal.sh`,
then run `.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release -- --native-terminal`
to check the actual Avalonia embedding and session lifecycle. These open test windows.

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
