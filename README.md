# SharpRail

A fully native C# and Avalonia port of Thinkrail's workspace for browsing projects,
previewing Markdown, editing files, running terminals, and managing Git changes and
worktrees. There is no browser, Electron shell or WebView anywhere in the stack.

## Built for speed

- **Native rendering end to end.** Avalonia draws the workbench with Skia on the GPU.
  Terminals use Ghostty with Metal texture (default) or Skia (fallback) rendering, selected in
  Settings → Terminal. The editor is Scintilla on a custom Skia
  surface that records each frame once and lets the renderer replay it.
- **Compiled ahead, tuned at runtime.** Published builds are .NET 10 ReadyToRun
  (non-composite) with tiered PGO, so code starts precompiled and hot paths are
  re-optimised from real use. The runtime stays open-world, so dynamic loading and JIT
  still work.
- **Startup does only what the first frame needs.** SharpRail resolves just enough to
  route to the right workspace, restores the visible documents first, and loads panels
  progressively. Full Git snapshots, recursive scans, spec indexing, hidden panels and
  inactive documents are deferred. Blocking I/O and parsing run off the UI thread, and
  Git or indexing failures never block opening files.
- **No local daemon, no local serialization.** On your own machine the host runs inside
  the app, so file, Git and project calls are direct method calls. Remote hosts use
  code-first gRPC over HTTP/2 with streaming for live state and terminal output.
- **Heavy work stays off the UI thread and is cancellable.** Git refreshes are scoped
  to the workspace, and a newer request cancels an older one. Rendered Markdown diffs
  are merged in the background, and an edit on disk cancels a stale merge. Changed lines
  are compared word by word only after lines are aligned, which keeps large, repetitive
  files responsive. Mermaid layout runs in the background too.
- **Native Mermaid.** Diagrams are laid out by Merman, a Rust port of Mermaid linked in
  as a native library, and drawn as vector SVG with Svg.Skia. No JavaScript engine is
  involved.
- **Smooth, exact scrolling in large files.** The editor wraps long documents in idle
  slices without stalling input, sizes its scrollbar by wrapped display lines, and
  scrolls trackpad and momentum input by the pixel.
- **Live refresh with coalescing.** Files, Specs, Changes and open tabs follow changes
  on disk. Write storms are batched into a few refreshes, so the UI and the host stay
  responsive.
- **Terminals that outlive their windows.** Shells are owned by the host, so closing a
  window or losing a connection detaches them rather than killing them. Reattaching
  replays recent output exactly once.

## Features

- Arrange tabs and split panes with drag-and-drop or context menus; open several
  windows (⌘⇧N) that share settings, projects and workspace changes live, while each
  keeps its own layout.
- Preview Markdown with selectable text, tables, links, images, callouts and Mermaid
  diagrams (full-screen pan and zoom). Markdown diffs offer a rendered view.
- Edit text files on macOS in Scintilla with HarfBuzz shaping and bidirectional
  layout; save with ⌘S.
- Browse staged, working, branch and commit diffs; stage and unstage changes.
- Create, switch, rename and remove Git worktrees from a Project Home.
- Choose from the bundled themes, including high contrast, or follow the system with
  your own light and dark pair.
- Open local projects or connect to a remote host. Terminals work in both.

This is a prototype: commit/push commands and AI integration are not available.
Editing and terminals currently target macOS. Markdown source and Git diffs remain
read-only. Syntax lexers, completion, full IME preedit and accessibility text
providers are not yet implemented in the editor.

## Run

From a source checkout. macOS builds need the Xcode command-line tools, LLVM at
`/opt/homebrew/opt/llvm`, and its Metal Toolchain
(`xcodebuild -downloadComponent MetalToolchain`). The first build downloads pinned
Ghostty 1.2.3, Zig 0.14.1, Scintilla, SheenBidi and Merman into `.tools`. No installed
Ghostty application is required.

```sh
scripts/bootstrap.sh
.tools/dotnet/dotnet run --project src/SharpRail.UI -c Release
```

To build the ReadyToRun macOS app bundle:

```sh
scripts/publish.sh
open artifacts/SharpRail.app
```

## Get started

Open a project using **+** in Projects or ⌘O (Ctrl+O on other platforms). A project
opens on its Project Home; create a workspace with ⌘N or work in the project folder.
In Files, click a folder's name to expand it, single-click a file to preview it, and
double-click or press Enter to keep a tab. Use Changes to review diffs. Press F5 to
refresh files and Git, or ⌘, to open Settings. Save or explicitly discard edits
before closing a modified tab or window; failed saves keep the buffer, and files
changed on disk are never overwritten silently.

Each new workspace opens a terminal in the bottom panel; add more from a pane's +
menu. ⌘⇧J shows and hides the bottom panel, even from inside a terminal. Option acts
as Alt on U.S. layouts, as in Ghostty, so Option+Backspace deletes a word. ⌘V pastes
images as quoted file paths, saving PNG files under `~/.sharprail/clipboard`.
Quitting the app ends local shells.
Changing the renderer in Settings reattaches the terminal views to the same
shells. The reusable controls live in `src/Ghostty.Avalonia`; its README documents
the API, build requirements and limits of the Skia path.
Metal texture is the default; creation or rendering failure automatically falls
back to Skia without ending the host shell. NSView remains a library API only.
Renderer benchmark sources and reproduction commands are in
[`benchmarks/ghostty`](benchmarks/ghostty/README.md).

## Checks

```sh
.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release
```

`-- --editor`, `-- --terminals`, `-- --ghostty-skia`, `-- --sync` and `-- --workspaces` run focused
subsets. `sh scripts/check-terminal.sh`, `-- --native-terminal` and `-- --native-texture` exercise real
Ghostty windows and open test windows. Set `SHARPRAIL_TEST_GIT_SOURCE` to a Thinkrail
clone to include the Git fixtures. See `src/SharpRail.Scintilla/README.md` for the
editor control's architecture and limits.

## Remote projects

After publishing, start the host and connect the app with the same token:

```sh
SHARPRAIL_TOKEN=your-token artifacts/host/SharpRail.Host.Remote
SHARPRAIL_REMOTE=http://127.0.0.1:54123 SHARPRAIL_TOKEN=your-token artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI
```

For another machine, set the host's `SHARPRAIL_BIND` to its Tailscale IP and use that
address in `SHARPRAIL_REMOTE`. Remote connections use cleartext HTTP/2 and require an
encrypted network such as Tailscale. The host keeps its shared state in
`SHARPRAIL_STATE_DIR` (default `~/.sharprail/host`) and its terminal sessions until it
stops.
