# Asset provenance

The browser terminal uses [restty](https://github.com/wiedymi/restty) 0.3.0,
Copyright (c) 2026 Wiedy Mi, MIT, and its bundled text-shaper 0.1.28,
Copyright (c) 2025 wiedymi, MIT. Their notices are in `licenses/Restty-MIT.txt` and
`licenses/TextShaper-MIT.txt`. Restty includes Ghostty/libghostty-vt WASM (MIT;
the Ghostty notice is in `src/Ghostty.Avalonia/Licenses`). The browser supplies the
existing bundled JetBrains Mono font and does not fetch restty's CDN font defaults.

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
It appends `src/Ghostty.Avalonia/Native/keyboard-mode.zig` to the embedding runtime, which
implements the agent newline fallback while preserving negotiated keyboard protocols.
`src/Ghostty.Avalonia/Native/CommandEnd.patch` marks the command line of a command that
exited non-zero (OSC 133 D), and `prompt-surface.zig`/`prompts.zig` add prompt features
(command output, click to move, failed-command rows) to the embedding runtime.
Its native library is built from https://github.com/ghostty-org/ghostty with
the Metal renderer; Ghostty's source dependency declarations retain their
upstream licenses.

The alternative Skia terminal uses libghostty-vt from the same repository, pinned
to commit 59c2dc032aba42aa5064bf206cc27286add3e9d8 (MIT). Its C shim and Avalonia
controls live in src/Ghostty.Avalonia; the Ghostty license is included there.
The zsh and bash shell-integration scripts the terminal host injects (shell-integration/) are
SharpRail's own, written for SharpRail, and are not third-party code.
Ghostty's shell-integration scripts (`share/ghostty`, partly GPL-3.0 as derived from kitty) are not
shipped; of Ghostty's resources only its terminfo entries (MIT) are.

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

The workflow skills in src/SharpRail.Plugins.Agent.Skills are adapted from Thinkrail's
packages/pi-thinkrail-workflow/skills (Apache 2.0; licenses/Thinkrail-Apache-2.0.txt): the text was
changed to name SharpRail and the tools Claude Code and Codex sessions have.

Mermaid diagrams are laid out by Merman 0.7.0 (https://github.com/Latias94/merman,
MIT OR Apache-2.0): its released macOS merman-ffi static library is linked into
libSharpRailMermaid, its released Android merman-ffi shared libraries
(merman-android-v0.7.0.aar) ship as libSharpRailMermaid.so, and its SVG is drawn with Svg.Skia (MIT). Merman's Rust crate
dependencies retain their licenses; licenses/Merman-Crates.txt lists each crate
with its license.

The four theme manifests in src/SharpRail.UI/Assets/Themes are copied unchanged
from Thinkrail's apps/web/src/themes/bundled (Apache 2.0;
licenses/Thinkrail-Apache-2.0.txt).

Core SVG icons in src/SharpRail.Plugins.UI.Kit/Assets/Icons use the original paths from
@remixicon/react 4.9.0, including the server glyph for Host settings. The alias map is
Assets/Icons/sources.json. The custom file-diff SVG comes from ThinkRail's
apps/web/public/custom-icons/file-diff-line.svg (Apache-2.0). The prototype's brand.png
remains a raster asset because its original vector source is unavailable.
See licenses/RemixIcon.txt for Remix Icon License 1.0.
Branch Graph's bundled commit and code-file SVG menu glyphs use the same 4.9.0 release and license.

Geist Regular, Medium, SemiBold and Bold came from the reference's bundled assets.
Geist Book uses the reference native prototype's weight-370 NativeText face;
its family metadata was renamed to register alongside the other Geist faces.
JetBrains Mono Regular came from the reference's bundled font assets.
Their SIL Open Font Licenses are in src/SharpRail.Plugins.UI.Kit/Assets/Fonts and are
included in the published app's Resources directory.

Markdown parsing uses Markdig (BSD-2-Clause). Avalonia, protobuf-net, and other NuGet dependencies retain their package licenses.
The spec dialect plugin parses YAML with YamlDotNet 18.1.0 (MIT);
see licenses/YamlDotNet.txt.
The EPUB Reader plugin reads EPUB packages with VersOne.Epub 3.3.6 (Unlicense, https://github.com/vers-one/EpubReader),
which has no further dependencies on .NET Standard 2.0; see licenses/VersOne.Epub-Unlicense.txt.

PDF Preview renders through PDFium 156.0.8076 from bblanchon.PDFium.macOS/Linux, and from
bblanchon.PDFium.Android in the Android client.
The Linux AppImage starts with the AppImage type 2 runtime 20251108 (https://github.com/AppImage/type2-runtime, MIT License,
Copyright (c) 2004-23 probonopd), which links its own libraries statically; appimagetool 1.9.1 (MIT License) writes the image
and is not part of it. Both are fetched at the pinned checksums in mise.toml.
The browser client uses the same chromium/8076 release's official PDFium WASM distribution and
Merman 0.7.0's WASM package. Their licenses and the project's dependency notices are included in the
browser bundle under `vendor/`; native terminal/editor dependencies retain the licenses listed here.
The PDFium license and the linked libraries' notices from the checksummed
chromium/8076 release archive are collected in licenses/PDFium.txt.

The Claude Code plugin's monochrome `assets/claude.svg` is the fork's recolored
Material Icon Theme Claude mark (MIT; Copyright 2025 Material Extensions).
See licenses/Material-Icon-Theme-MIT.txt and the upstream license at
https://github.com/material-extensions/vscode-material-icon-theme/blob/main/LICENSE.

Side resize projection adapts the constraint and delta-distribution rules from
react-resizable-panels 2.1.9 (Copyright 2023 Brian Vaughn, MIT).
See licenses/React-Resizable-Panels.txt; no JavaScript runtime is bundled.

The code editor is a managed C# port of parts of Scintilla 5.6.7 (Copyright Neil
Hodgson and contributors). See src/SharpRail.Scintilla/licenses/Scintilla.txt.
The editor's drawing uses SkiaSharp 4.148.0, the version Avalonia’s Skia backend
and Svg.Skia share in this app;
SkiaSharp, Skia, HarfBuzzSharp and HarfBuzz retain their NuGet package licenses. No Cocoa Scintilla view
or Lexilla binary is bundled.

File Icons bundles 1,251 recoloured SVGs generated from Material Icon Theme 5.38.1
(MIT; Copyright 2025 Material Extensions). See licenses/Material-Icon-Theme-MIT.txt.
The source archive checksum is pinned in the plugin generator; generated assets are build output.

Codex uses Tomlyn 2.10.1 (BSD-2-Clause; Copyright 2019–2026 Alexandre Mutel).
See licenses/Tomlyn-BSD-2-Clause.txt. Its Codex SVG derives from LobeHub Icons
(MIT; Copyright 2023 LobeHub); see licenses/LobeHub-Icons-MIT.txt. The fallback
OpenAI glyph is covered by the existing Remix Icon notice.

The Android client uses Avalonia.Android 12.1.4 (MIT, as Avalonia). Through it the package
includes the .NET bindings Xamarin.AndroidX.AppCompat 1.7.1.3, Xamarin.AndroidX.DocumentFile
1.1.0.3 and Xamarin.AndroidX.Window 1.5.1.2 with their AndroidX, Kotlin, kotlinx, Guava
ListenableFuture, JetBrains Annotations and JSpecify dependencies: the bindings are MIT
(Copyright .NET Foundation Contributors) and the bound libraries Apache-2.0.
See licenses/AndroidX-Bindings.txt. SkiaSharp and HarfBuzzSharp's Android native assets
retain their NuGet package licenses.

The Android terminal
library is libghostty-vt at the commit above and links only the system C library.
Krafs.Publicizer 2.3.2 (MIT) is used while building the Android client and is not
redistributed.

The QR code in Settings › Host is generated with QRCoder 1.8.0 (MIT); see licenses/QRCoder-MIT.txt.
The Android client reads it through Google Play services' code scanner
(Xamarin.GooglePlayServices.Code.Scanner 116.1.0.17 bindings, MIT, over Google's ML Kit code scanner,
which is covered by the Android Software Development Kit License and runs inside Play services); the
two QR icons come from the same Remix Icon set as the other SVG glyphs.
Syntax highlighting uses TextMateSharp and TextMateSharp.Grammars 2.0.4 (MIT),
with their Onigwrap 1.0.11 / Oniguruma native dependency. The engine licence is
in licenses/TextMateSharp-MIT.txt; native dependency notices and licences are in
licenses/Onigwrap-MIT.txt and licenses/Onigwrap-NOTICES.txt. Bundled grammars retain their upstream provenance
in the embedded grammar resources. Supplementary grammar files under the UI kit's Editor/Grammars are:

- Kotlin: fwcd/vscode-kotlin, commit 4a7c1538754828c1d22a8bee8ff3400045b4352a,
  syntaxes/kotlin.tmLanguage.json (MIT; licenses/KotlinGrammar.txt).
- Scala: scala/vscode-scala-syntax, commit 6dbe37fdda3610967e28717b31697e9699865641,
  syntaxes/Scala.tmLanguage.json (MIT; licenses/ScalaGrammar.txt).

Git ignore highlighting uses TextMateSharp.Grammars' bundled source.ignore grammar.

- TOML: tamasfe/taplo, commit 08f343be02ce1b20296470396a42f0fa47820449,
  editors/vscode/toml.tmLanguage.json (MIT; licenses/TomlGrammar.txt).
- Fish: bmalehorn/vscode-fish, commit 0741d59ecf3d2aa018543747874eb8f34c655612,
  syntaxes/fish.tmLanguage.json (MIT; licenses/FishGrammar.txt).

The optional IntelliJ language-service adapter uses published catalog metadata and protocol
conventions from [JetBrains/intellij-lsp.nvim](https://github.com/JetBrains/intellij-lsp.nvim),
revision 9cfad18005470e4187090013e08f92ec9e437219, Copyright 2026 JetBrains s.r.o., Apache 2.0.
SharpRail independently implements these conventions in C#; no Neovim runtime is included.
See `licenses/intellij-lsp.nvim.LICENSE.txt` and `licenses/intellij-lsp.nvim.NOTICE.md`.
The proprietary JetBrains server is a separate product and is not bundled or licensed by this notice.


Optional JetBrains Context uses an unmodified, separately downloaded proprietary CLI.
SharpRail ships its adapter only; no Context binary or proprietary source is bundled.
The actual user must accept the selected artifact's applicable CLI EAP and AI terms.
Public integration documentation at https://github.com/JetBrains/context is a protocol
reference, not a license to redistribute the CLI.


The IntelliJ IDEA product icon is an unmodified asset
from https://www.jetbrains.com/company/brand/ and its published downloads. Used to identify
SharpRail's integrations under the JetBrains Brand Guidelines, not as SharpRail branding.
Copyright © 2026 JetBrains s.r.o. IntelliJ IDEA and the IntelliJ IDEA logo are trademarks of JetBrains s.r.o.

Code Intelligence language icons preserve the original colors of Material Icon Theme v5.38.1
(the same pinned MIT-licensed source as the core file icons).

The unmodified Air product icon is from JetBrains’ Air blog:
https://blog.jetbrains.com/wp-content/uploads/2026/02/logo.svg
Copyright © 2026 JetBrains s.r.o. Air and the Air logo are trademarks of JetBrains s.r.o.
Used to identify Air Context; https://www.jetbrains.com/air/context/. It ships as the Air Context plugin's `assets/air.svg`.

Provider-specific icons are unmodified assets from the publishers’ repositories; each ships as its
language server plugin's `Icon.png` or `Icon.svg`. Assets/LanguageIcons/SOURCES.json records pinned source revisions and SHA-256 digests;
licenses/lsp-icon-* retain their applicable repository licenses. These identify integrations
and do not imply publisher endorsement.

Plugins settings reuses the bundled Claude and Discord mark silhouettes with their brand colors.
Their original provenance and licensing remain those of the bundled plugin assets; this
presentation does not imply affiliation with either vendor.

The Android client embeds Tailscale v1.104.1 (`tsnet`, BSD-3-Clause) and its Go
runtime. Source, pinned module checksums and the Tailscale license are under
`src/SharpRail.Android/Tailscale.Native`. The native build stages the Go license
and module license notices into the APK's `assets/licenses/tailscale` directory.
The browser client downloads, on request, a WebAssembly build of Tailscale v1.104.1 (BSD-3-Clause) adapted from
upstream `cmd/tsconnect`, with its Go runtime and Go's `wasm_exec.js`. Source, pinned module checksums and the
Tailscale license are under `src/SharpRail.Browser/Tailscale.Wasm`; the build serves the Go license and the
module license notices beside the module under `tailscale/licenses`.
Tailscale is a trademark of Tailscale Inc.; this integration does not imply endorsement.
