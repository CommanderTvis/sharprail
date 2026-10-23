# Development machine setup

What a source checkout provisions itself, and what you must install on the machine. Run every task through
`scripts/mise run <task>` (`scripts\mise.cmd` on Windows); details are in [scripts/README.md](scripts/README.md).

## Provisioned by the repo

`scripts/mise` runs a mise that is already on the machine (PATH, Homebrew or `~/.local/bin`) as is, with your own mise
configuration; without one it downloads the pinned, checksum-verified mise release into mise's data directory, as CI
does. Either way there is no bootstrap step: mise installs the tools below from `mise*.toml` (versions and SHA-256 per
platform in the matching `mise*.lock`) on first use, into its own data directory (default `~/.local/share/mise`), which
every checkout and worktree of the user shares. `scripts/tool <name>` prints where a pinned tool lives (installing it
first when missing), and builds and tasks that run under `scripts/mise` find `dotnet` and the other binaries on PATH.
A system mise must be at least the `min_version` in `mise.toml`.

| Tool (`scripts/tool` name) | Pinned in | Used by |
| --- | --- | --- |
| .NET SDK (`dotnet`), Zig 0.14.1 and 0.16.0 (`zig-0.14.1`, `zig-0.16.0`; Windows: `zig-windows`), Merman (`merman`, macOS), Temurin JDK 21 (`jdk`; not Windows, where `JAVA_HOME` names a JDK) | `mise.toml` | every build, check and package task |
| Android NDK r27d (`android-ndk`), Merman's Android libraries (`merman-android`) | `mise.android.toml` | `android`, `package:android` |
| Node, pnpm, uv, Binaryen, PDFium WASM (`node`, `pnpm`, `uv`, `binaryen`, `pdfium-wasm`) | `mise.browser.toml` | `browser`, `package:browser` |
| appimagetool and the AppImage runtime (`appimagetool`, `appimage-runtime`) | `mise.toml` | `package:linux` |

Fetched by the build recipes, pinned by commit or checksum: Ghostty sources and the Zig dependency mirror
(`scripts/zig-deps.sh`), Go for the Android Tailscale bridge (`scripts/tailscale-go.sh`). Build caches and those downloads live in the machine-wide `SHARPRAIL_CACHE_DIR`
(`~/.cache/sharprail`), shared by every checkout; see [scripts/README.md](scripts/README.md).
NuGet packages restore from nuget.org in locked mode. Managed language servers are downloaded by the app at
runtime, not by the build.

## Install yourself

### All platforms

- Git 2.36 or newer on `PATH`: the build fetches pinned sources with it, and the app needs `git worktree list -z`
  (older Git shows a Git error in Changes, see [LINUX.md](LINUX.md)).
- Network access to the download hosts in the `mise*.toml` files, GitHub and nuget.org for the first install.

### macOS (desktop build, `build`, `dev`, `package:macos`, `check:terminal`)

- `bash` and `curl` (system): `scripts/mise-pinned` downloads mise with them when no mise is installed.
- Xcode command-line tools (`xcode-select --install`): `xcrun clang` builds the Merman and notification libraries,
  and `xcrun --sdk macosx` supplies the SDK to Zig (`src/Ghostty.Avalonia/build-native.sh`). `codesign` and `hdiutil` (system) sign the bundle and make the
  `dmg`.
- The Metal toolchain (`xcodebuild -downloadComponent MetalToolchain`): libghostty compiles its Metal shaders.
- LLVM at `/opt/homebrew/opt/llvm` (`brew install llvm`): `build-native.sh` archives libghostty with its
  `llvm-ar` because Apple's libtool drops Zig objects. The path is fixed; a missing binary fails the native build.

### Linux (`build`, `package:linux`, running the AppImage)

- `bash`, `curl`, `tar`, POSIX `sh`, `patch` and `shasum` (coreutils/perl): the mise download and the native recipes.
- glibc 2.31 or newer, an X11 or XWayland session, the X11 client libraries (`libX11`, `libXcursor`, `libXrandr`,
  `libXi`), `fontconfig` and a GL or software Skia stack to run the app (see [LINUX.md](LINUX.md)).
- The AppImage is packed on a host of the target architecture (`scripts/tasks/package-linux.sh` refuses
  otherwise; `SHARPRAIL_APPDIR_ONLY=1` assembles the AppDir anywhere).

### Windows (`build`, `package:windows`)

- Windows 10 1809 or later / Windows 11, x64 or ARM64; Git for Windows, Windows PowerShell and Windows' `tar.exe`
  ([WINDOWS.md](WINDOWS.md)). No Visual Studio C++ installation is needed: Zig compiles the native libraries
  (`scripts/windows-native.ps1`).
- Android builds run on macOS or Linux only (the NDK pin lists no Windows archives).

### Android (`android`, `package:android`; macOS or Linux)

- The Android SDK with `platforms;android-36` and `build-tools;36.0.0` (what the nightly installs with
  `sdkmanager`), at `ANDROID_HOME` (default `~/Library/Android/sdk`). `scripts/tasks/android.sh` stops with
  "No Android SDK at ..." when `$ANDROID_HOME/platforms` is missing.
- Nothing else: the JDK, the NDK and Merman's prebuilt `.so` come from mise on first use, and the .NET `android` workload is installed into
  the SDK's directory on first use. Set `JAVA_HOME` only to build with another JDK 17 or 21.
- `adb` (SDK platform-tools) and a device or emulator for `android run`.

### Optional

- `gh`: release and CI work (`scripts/tasks/zig-deps-mirror.sh` prints a `gh release` command to publish the mirror).
- `gpg`: only the nightly signs AppImages; local builds do not need it.
