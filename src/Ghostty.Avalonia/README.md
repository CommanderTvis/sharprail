# Ghostty.Avalonia

Avalonia terminal controls with three rendering paths, independent of SharpRail.
The project contains its managed controls, native bridges, pinned build script
and Ghostty license. It references no SharpRail project.

| Control | Rendering | Process ownership |
| --- | --- | --- |
| `GhosttyView` | Ghostty's AppKit view and Metal renderer, hosted by `NativeControlHost` | Ghostty starts the configured child |
| `GhosttyTextureView` | Ghostty's Metal output sampled directly by Avalonia's Skia compositor; no hosted native view | Ghostty starts the configured child |
| `GhosttySkiaView` | libghostty-vt cells and cursor recorded into Skia pictures, composed by Avalonia | The caller supplies output, consumes input and resizes its PTY |

Both native libraries currently build on macOS, on the target architecture.
The Skia control uses Avalonia input, selection and clipboard APIs and participates
in normal Avalonia clipping and overlays. The native view draws above Avalonia
content. Skia rendering uses Avalonia's chosen Skia backend.

## Build and reuse

Copy this directory into an Avalonia application and add a project reference to
`Ghostty.Avalonia.csproj`. It supplies .NET 10 settings and default dependency
versions when the application does not use central package management. With
central package management, add Avalonia 12.1.3, Avalonia.Skia 12.1.3,
SkiaSharp 4.148.0 and HarfBuzzSharp 14.2.1.301 to the application's package versions.

Build on macOS with Xcode command line tools, Git, curl and LLVM's Darwin archive
writer at `/opt/homebrew/opt/llvm/bin/llvm-ar`. The script downloads pinned Ghostty
sources and Zig toolchains, and caches native output under `obj/native` by default.
`GhosttyNativeCache` can point to a persistent cache. Libraries and shell resources
are copied to the consuming application's output and publish directory.
The native view uses Ghostty 1.2.3; the VT library uses unreleased Ghostty commit
`59c2dc032aba42aa5064bf206cc27286add3e9d8`. See `licenses/Ghostty-MIT.txt`.

## Skia control

```csharp
var terminal = new GhosttySkiaView { FontFamily = "Menlo", FontSize = 13 };
terminal.Input += (_, bytes) => QueuePtyWrite(bytes);
terminal.GridResized += (_, size) => ResizePty(size.Columns, size.Rows);
// Feed output on the UI thread; the control copies it into Ghostty's terminal state.
terminal.Write(output.Span);
```

The caller owns PTY startup, lifetime, reconnects and ordered asynchronous writes.
Wait for layout before starting the PTY at `Size`. Use `FocusTerminal()` when
activating a tab. Dispose the control on the UI thread when releasing the view.
`Write`, appearance changes and input operations also belong on the UI thread.
A supplied `SKTypeface` remains caller-owned and must outlive the control.

`Colors` supplies background, foreground, 16 ANSI colours, cursor, selection and
minimum contrast. The Skia path supports wide cells, combining clusters, HarfBuzz
shaping within a cell, font fallback, procedural box drawing, SGR decorations,
cursor styles, scrollback, mouse reporting, bracketed paste and IME preedit.
Command shortcuts bubble to the application; Option acts as Alt by default.
Set `ClipboardImageDirectory` to save pasted images and send their quoted paths.

All three controls support OSC 52 text clipboard writes and reads on macOS,
including terminal output received from remote hosts. Writes are allowed; reads
require an AppKit confirmation each time. Skia uses synchronous native clipboard
callbacks because libghostty-vt requests cannot outlive their callback. Clipboard
selectors map to the system pasteboard on macOS. Invalid UTF-8 writes are ignored.
The Skia library preserves embedded NULs; the full libghostty callback uses a
NUL-terminated string. Both return an empty clipboard on denied reads, completing
the pending request without disclosing data. Skia preserves the query's BEL/ST
terminator; full libghostty uses ST. Ordinary Skia copy/paste still uses Avalonia.

## Native control

```csharp
var terminal = new GhosttyView(new GhosttyLaunch(workspaceDirectory));
terminal.Exited += (_, exitCode) => OnExit(exitCode);
terminal.Shortcut += (_, shortcut) => HandleShortcut(shortcut);
```

`GhosttyLaunch` accepts a command, one environment variable and an optional image
paste directory. `Colors` updates the live view. Dispose it when its view is no
longer needed; its direct child belongs to Ghostty.

## Metal texture control

```csharp
// Configure the application to use Avalonia's Metal backend on macOS.
builder.With(new AvaloniaNativePlatformOptions
{
    RenderingMode = [AvaloniaNativeRenderingMode.Metal]
});
var terminal = new GhosttyTextureView(new GhosttyLaunch(workspaceDirectory));
container.Child = terminal;
await terminal.Ready;
terminal.FocusTerminal();
```

This control keeps Ghostty's full Metal renderer, including its shaping and image
support, while Avalonia owns presentation, clipping, overlays and input. The
platform NSView required internally by libghostty stays unparented; no
`NativeControlHost` or Ghostty NSView is inserted into the window. `Colors`,
`Exited` and `GhosttyLaunch` work as for the native control. Application Command
shortcuts bubble through Avalonia; text and image paste use Ghostty's callbacks.
Subscribe to `OperationFailed` for compositor errors and dispose on the UI thread.

The pinned `MetalTexture.patch` exposes completed source textures and guards each
target before Ghostty reuses it. An Avalonia custom draw operation wraps the
leased Metal texture as a Skia image and samples it directly, preserving the
canvas clip, transform and opacity. No export surfaces, snapshot textures, GPU
blits or CPU pixel readback are needed. Coalesced frame-ready notifications
invalidate the control without a polling timer or an unbounded frame queue.

The compositor flushes and waits for its GPU read before returning the lease.
This explicit completion wait trades some GPU overlap for bounded memory and
safe reuse. Only the latest source and in-flight readers are retained; resize
and disposal release old sources. Native leases keep their source alive even
if the view is disposed during a draw. Avalonia must use a Metal-backed Skia
context; other backends fail explicitly. The Skia cell control is the fallback.

`ScrollbackMemory.patch` fixes the pinned native Ghostty's recycling of enlarged
scrollback pages. It preserves their allocation size and capacity so repeated
ANSI/Unicode scrolling does not lose allocations. Both native Metal controls
use this patch; it does not reduce the scrollback limit.

## Limits and verification

The Skia path does not reproduce every feature of Ghostty's native renderer:
image protocols, cross-cell ligatures, OSC 8 hyperlink activation and an accessibility
text provider are not implemented. Physical macOS IME behavior and font raster
parity require native validation; headless input does not establish those.
Other operating systems do not yet have native build targets.

Both texture and Skia controls support Command-hover underlining and
Command-click to open HTTP(S) URLs in the system browser (Ctrl on other platforms).
Soft-wrapped URLs remain clickable across rows; dragging still selects text.
The texture control uses Ghostty's built-in link detection; Skia detects web URLs
from VT cells and soft-wrap metadata.

In SharpRail, `--ghostty-skia` runs focused real-pixel/input and host PTY checks;
`--terminals` additionally checks local/remote host behavior and terminal docking.
`scripts/check-terminal.sh` and `--native-terminal` cover the native Metal path.
`--native-texture` checks a real Metal-backed Avalonia window: imported pixels,
theme, overlays, clipping, input, clipboard, resize, remounting and local/remote
shell retention when switching between texture and Skia rendering.

## Skia rendering design

The retained framebuffer, dirty-row redraw, cached text blobs, ASCII batching and
bounded output-slice designs are adapted from Royal Apps' **RoyalTerminal**,
Copyright (c) 2026 Royal Apps, MIT licensed, at commit
`b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f`. This is an implementation for our
Ghostty/Avalonia boundary, not a RoyalTerminal runtime dependency.

- [Retained framebuffer and cursor-row invalidation](https://github.com/royalapplications/RoyalTerminal/blob/b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f/src/RoyalTerminal.Avalonia/Rendering/TerminalDrawHandler.cs).
- [Text batching and blob caches](https://github.com/royalapplications/RoyalTerminal/blob/b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f/src/RoyalTerminal.Rendering.Skia/Rendering/SkiaTerminalRenderer.cs).
- [Bounded shaped-run cache](https://github.com/royalapplications/RoyalTerminal/blob/b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f/src/RoyalTerminal.Rendering.Text/TextShaping/ShapedRunCache.cs).
- [Output backlog and processing budgets](https://github.com/royalapplications/RoyalTerminal/blob/b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f/src/RoyalTerminal.Avalonia/Controls/TerminalControl.cs).

The original [MIT notice](licenses/RoyalTerminal-MIT.txt) accompanies this library
and is copied to consuming build/publish outputs.

`GhosttySkiaView` compares viewport row content and cursor/preedit state on the UI
thread, then records only changed rows. Immutable row pictures are reference
counted across queued draw operations. `TerminalFramebuffer` retains pixels on a
Skia surface (GPU when available, raster otherwise); the render thread draws only
replaced rows before compositing the image. Font, scale, size, padding and theme
changes invalidate the relevant cache. ASCII runs use fixed grid positions;
complex graphemes retain cell-local shaping. Cross-cell ligatures are not added.
Cursor blink and focus changes reuse the last VT snapshot; other VT snapshot
extraction still traverses the viewport.

Text blobs are cached up to 4096 entries and explicitly disposed on eviction or
font disposal. A recorded picture retains its native drawing resources, so cache
eviction cannot invalidate a queued frame.

SharpRail's Skia adapter awaits one output dispatch at a time, processes at most
8 KiB per dispatch in 1 KiB pieces, and yields after approximately 2 ms. A single
piece can exceed that budget. Replay uses the same drain and exit follows the
last processed byte. This bounds UI dispatch, not the separate host queues;
VT processing remains on the UI thread to preserve its ownership contract.

`--native-skia` verifies the GPU framebuffer in a real macOS window with
own-window pixels, incremental redraw, theme parity with an Avalonia swatch,
Retina resize and disposal. `--ghostty-skia` also checks sparse redraw against a
full repaint, replay yielding to input, cancellation, split UTF-8 and exit ordering.
