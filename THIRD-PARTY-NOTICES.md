# Asset provenance

The macOS terminal embeds Ghostty 1.2.3 (MIT), pinned to commit
6d2dd585a5d87fa745d48188dd096ca6e63014d0. See licenses/Ghostty-MIT.txt.
The build backports upstream's configuration-file loader via
`src/Ghostty.Avalonia/Native/ConfigFile.patch` to load the host UI theme.
It also applies `src/Ghostty.Avalonia/Native/MetalTexture.patch` to expose completed
IOSurface-backed Metal render targets to Avalonia and wait for external GPU readers
before Ghostty reuses them. The targets also enable shader sampling.
`src/Ghostty.Avalonia/Native/ScrollbackMemory.patch` preserves enlarged scrollback
page allocations when recycling them, fixing unbounded memory growth.
`src/Ghostty.Avalonia/Native/ExternalIo.patch` adds caller-owned in-process I/O
without starting a Ghostty child or transport.
Its native library is built from https://github.com/ghostty-org/ghostty with
the Metal renderer; Ghostty's source dependency declarations retain their
upstream licenses.

The alternative Skia terminal uses libghostty-vt from the same repository, pinned
to commit 59c2dc032aba42aa5064bf206cc27286add3e9d8 (MIT). Its C shim and Avalonia
controls live in src/Ghostty.Avalonia; the Ghostty license is included there.

The Skia terminal adapts rendering and output-scheduling designs from
[RoyalTerminal](https://github.com/royalapplications/RoyalTerminal/tree/b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f),
Copyright (c) 2026 Royal Apps, MIT. The retained framebuffer, dirty-row updates,
ASCII batching, text-blob caches and bounded UI output slices are implemented
for SharpRail's existing Ghostty/host boundary. Pinned source links are in
[src/Ghostty.Avalonia/README.md](src/Ghostty.Avalonia/README.md#skia-rendering-design);
the original notice is in
[src/Ghostty.Avalonia/licenses/RoyalTerminal-MIT.txt](src/Ghostty.Avalonia/licenses/RoyalTerminal-MIT.txt)
and accompanies build/publish outputs.

The visual layout follows Thinkrail's original-workspace.png and current
workspace specification. The logo, custom diff icon and unchanged
scripts/window-probe.m came from prototypes/ThinkRailNative (Copyright 2026
JetBrains s.r.o., Apache 2.0; licenses/Thinkrail-Apache-2.0.txt).

Mermaid diagrams are laid out by Merman 0.7.0 (https://github.com/Latias94/merman,
MIT OR Apache-2.0): its released macOS merman-ffi static library is linked into
libSharpRailMermaid and its SVG is drawn with Svg.Skia (MIT). Merman's Rust crate
dependencies retain their licenses; licenses/Merman-Crates.txt lists each crate
with its license.

The four theme manifests in src/SharpRail.UI/Assets/Themes are copied unchanged
from Thinkrail's apps/web/src/themes/bundled (Apache 2.0;
licenses/Thinkrail-Apache-2.0.txt).

PNG icons in src/SharpRail.Plugins.UI.Kit/Assets/Icons came from that prototype's @remixicon/react 4.9.0 assets.
The collapse/expand vertical and horizontal ellipsis, fullscreen, subtract,
arrow-go-back and puzzle-2-line (the plugin fallback) icons use the same package's original
vector paths.
See licenses/RemixIcon.txt for Remix Icon License 1.0.

Geist Regular, Medium, SemiBold and Bold came from the reference's bundled assets.
Geist Book uses the reference native prototype's weight-370 NativeText face;
its family metadata was renamed to register alongside the other Geist faces.
JetBrains Mono Regular came from the reference's bundled font assets.
Their SIL Open Font Licenses are in src/SharpRail.Plugins.UI.Kit/Assets/Fonts and are
included in the published app's Resources directory.

Markdown parsing uses Markdig (BSD-2-Clause). Avalonia, protobuf-net.Grpc,
grpc-dotnet, and other NuGet dependencies retain their package licenses.

Side resize projection adapts the constraint and delta-distribution rules from
react-resizable-panels 2.1.9 (Copyright 2023 Brian Vaughn, MIT).
See licenses/React-Resizable-Panels.txt; no JavaScript runtime is bundled.

The macOS code editor embeds Scintilla 5.6.7 (Copyright Neil Hodgson and
contributors), fetched from https://www.scintilla.org/scintilla567.tgz with its
SHA-256 pinned in src/SharpRail.Scintilla/build-native.sh and patched by
src/SharpRail.Scintilla/Native/EditView.patch. See
src/SharpRail.Scintilla/licenses/Scintilla.txt. It also embeds SheenBidi 3.0.0
(Copyright 2014-2026 Muhammad Tayyab Akram, Apache-2.0), fetched from
https://github.com/Tehreer/SheenBidi with its SHA-256 pinned in the same script.
See src/SharpRail.Scintilla/licenses/SheenBidi.txt.
The custom platform port uses SkiaSharp 4.148.0, the version Avalonia’s Skia backend
and Svg.Skia share in this app;
SkiaSharp, Skia, HarfBuzzSharp and HarfBuzz retain their NuGet package licenses. No Cocoa Scintilla view
or Lexilla binary is bundled.
