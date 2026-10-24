# Development machine setup

What a checkout provisions itself, and what you install. Run every task through `scripts/mise run <task>`
(`scripts\mise.cmd` on Windows); see [scripts/README.md](../../scripts/README.md).

## Provisioned by the repo

mise can be the one on your machine or one the repo provisions:

- System mise: `scripts/mise` runs one found on PATH, in Homebrew or in `~/.local/bin` as is, with your own mise
  configuration. It must meet the `min_version` in `mise.toml`.
- Auto-provisioned mise: without one, `scripts/mise` downloads the pinned, checksum-verified release into mise's data
  directory, as CI does.

Either way there is no bootstrap step. mise installs the tools below from `mise*.toml` (versions and SHA-256 per
platform in the matching `mise*.lock`) on first use, into its data directory (default `~/.local/share/mise`), shared by
every checkout and worktree of the user. `scripts/tool <name>` prints where a pinned tool lives, installing it if
missing; tasks run under `scripts/mise` find `dotnet` and the others on PATH.

| Tool (`scripts/tool` name) | Pinned in | Used by |
| --- | --- | --- |
| .NET SDK (`dotnet`), Zig 0.14.1 and 0.16.0 (`zig-0.14.1`, `zig-0.16.0`; Windows: `zig-windows`), Merman (`merman`, macOS), Temurin JDK 21 (`jdk`; not Windows, where `JAVA_HOME` names a JDK) | `mise.toml` | every build, check and package task |
| Android NDK r27d (`android-ndk`), Merman's Android libraries (`merman-android`) | `mise.android.toml` | `android`, `package:android` |
| Node, pnpm, uv, Binaryen, PDFium WASM (`node`, `pnpm`, `uv`, `binaryen`, `pdfium-wasm`) | `mise.browser.toml` | `browser`, `package:browser` |
| appimagetool and the AppImage runtime (`appimagetool`, `appimage-runtime`) | `mise.toml` | `package:linux` |

The build recipes also fetch, pinned by commit or checksum, the Ghostty sources and Zig dependency mirror
(`scripts/zig-deps.sh`) and Go for the Android Tailscale bridge (`scripts/tailscale-go.sh`). Build caches and those
downloads live in the machine-wide `SHARPRAIL_CACHE_DIR` (`~/.cache/sharprail`), shared by every checkout; see
[scripts/README.md](../../scripts/README.md). NuGet packages restore from nuget.org in locked mode. The app downloads
managed language servers at runtime, not the build.

## Install yourself

### All platforms

- Git 2.36 or newer on `PATH`: the build fetches pinned sources with it, and the app needs `git worktree list -z`
  (see [LINUX.md](../../LINUX.md)).

### macOS (`build`, `dev`, `package:macos`, `check:terminal`)

- `bash` and `curl` (system): `scripts/mise-pinned` downloads mise with them when none is installed.
- Xcode command-line tools (`xcode-select --install`): `xcrun clang` builds the Merman and notification libraries,
  `xcrun --sdk macosx` supplies Zig's SDK (`src/Ghostty.Avalonia/build-native.sh`), and `codesign` and `hdiutil` sign
  the bundle and make the `dmg`.
- The Metal toolchain (`xcodebuild -downloadComponent MetalToolchain`): libghostty compiles its Metal shaders.
- LLVM at `/opt/homebrew/opt/llvm` (`brew install llvm`): `build-native.sh` archives libghostty with its `llvm-ar`,
  since Apple's libtool drops Zig objects. The path is fixed.

### Linux (`build`, `package:linux`, running the AppImage)

- `bash`, `curl`, `tar`, POSIX `sh`, `patch` and `shasum`: the mise download and the native recipes.
- To run the app: glibc 2.31 or newer, an X11 or XWayland session, `libX11`, `libXcursor`, `libXrandr`, `libXi`,
  `fontconfig` and a GL or software Skia stack ([LINUX.md](../../LINUX.md)).
- Pack the AppImage on a host of the target architecture (`SHARPRAIL_APPDIR_ONLY=1` assembles the AppDir anywhere).

### Windows (`build`, `package:windows`)

- Windows 10 1809 or later or Windows 11, x64 or ARM64; Git for Windows, Windows PowerShell and `tar.exe`
  ([WINDOWS.md](../../WINDOWS.md)). No Visual Studio C++ needed: Zig compiles the native libraries
  (`scripts/windows-native.ps1`).
- Android builds need macOS or Linux (the NDK pin has no Windows archives).

### Android (`android`, `package:android`; macOS or Linux)

- The Android SDK with `platforms;android-36` and `build-tools;36.0.0` (as the nightly installs with `sdkmanager`), at
  `ANDROID_HOME` (default `~/Library/Android/sdk`).
- Nothing else: the JDK, NDK and Merman's prebuilt `.so` come from mise, and the .NET `android` workload installs into
  the SDK on first use. Set `JAVA_HOME` only to build with another JDK 17 or 21.
- `adb` (SDK platform-tools) and a device or emulator for `android run`.
