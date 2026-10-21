# Development machine setup

What a source checkout provisions itself, and what you must install on the machine. Run every task through
`scripts/mise run <task>` (`scripts\mise.cmd` on Windows); details are in [scripts/README.md](scripts/README.md).

## Provisioned by the repo

`scripts/mise` downloads the pinned mise release, which installs the tools below from `mise*.toml` (versions and
SHA-256 per platform in the matching `mise*.lock`) into the shared store `~/.sharprail/tools` (`SHARPRAIL_TOOLS`)
and links them into `.tools`. The tasks that need a bootstrap run it themselves.

| Command | Installs | Used by |
| --- | --- | --- |
| `scripts/mise run bootstrap` | .NET SDK (`.tools/dotnet`), Zig 0.14.1 and 0.16.0 (Windows: the `http:zig-windows` archive), Merman (macOS) | every build, check and package task |
| `scripts/mise run bootstrap:android` | Android NDK r27d (`.tools/android-ndk`), Temurin JDK 21 (`.tools/jdk`), Merman's Android libraries (`.tools/merman-android`) | `android`, `package:android` |
| `scripts/mise run bootstrap:browser` | Node, pnpm, uv, Binaryen, PDFium WASM | `browser`, `package:browser` |
| `scripts/mise run bootstrap:linux` | appimagetool and the AppImage runtime | `package:linux` |

Fetched by the build recipes, pinned by commit or checksum: Ghostty sources and the Zig dependency mirror
(`scripts/zig-deps.sh`), Go for the Android Tailscale bridge (`scripts/tailscale-go.sh`, `.tools/tailscale-go`).
NuGet packages restore from nuget.org in locked mode. Managed language servers are downloaded by the app at
runtime, not by the build.

## Install yourself

### All platforms

- Git 2.36 or newer on `PATH`: the build fetches pinned sources with it, and the app needs `git worktree list -z`
  (older Git shows a Git error in Changes, see [LINUX.md](LINUX.md)).
- Network access to the download hosts in the `mise*.toml` files, GitHub and nuget.org for the first bootstrap.

### macOS (desktop build, `build`, `dev`, `package:macos`, `check:terminal`)

- `bash` and `curl` (system): `scripts/mise-pinned` downloads mise with them.
- Xcode command-line tools (`xcode-select --install`): `xcrun clang` builds the Merman and notification libraries,
  and `xcrun --sdk macosx` supplies the SDK to Zig (`src/Ghostty.Avalonia/build-native.sh`). `codesign` and `hdiutil` (system) sign the bundle and make the
  `dmg`.
- The Metal toolchain (`xcodebuild -downloadComponent MetalToolchain`): libghostty compiles its Metal shaders.
- LLVM at `/opt/homebrew/opt/llvm` (`brew install llvm`): `build-native.sh` archives libghostty with its
  `llvm-ar` because Apple's libtool drops Zig objects. The path is fixed; a missing binary fails the native build.

### Linux (`build`, `package:linux`, running the AppImage)

- `bash`, `curl`, `tar`, POSIX `sh`, `patch` and `shasum` (coreutils/perl): bootstrap and the native recipes.
- glibc 2.31 or newer, an X11 or XWayland session, the X11 client libraries (`libX11`, `libXcursor`, `libXrandr`,
  `libXi`), `fontconfig` and a GL or software Skia stack to run the app (see [LINUX.md](LINUX.md)).
- The AppImage is packed on a host of the target architecture (`scripts/tasks/package-linux.sh` refuses
  otherwise; `SHARPRAIL_APPDIR_ONLY=1` assembles the AppDir anywhere).

### Windows (`build`, `package:windows`)

- Windows 10 1809 or later / Windows 11, x64 or ARM64; Git for Windows, Windows PowerShell and Windows' `tar.exe`
  ([WINDOWS.md](WINDOWS.md)). No Visual Studio C++ installation is needed: Zig compiles the native libraries
  (`scripts/windows-native.ps1`).
- Android builds run on macOS or Linux only (the NDK and JDK pins list no Windows archives).

### Android (`android`, `package:android`; macOS or Linux)

- The Android SDK with `platforms;android-36` and `build-tools;36.0.0` (what the nightly installs with
  `sdkmanager`), at `ANDROID_HOME` (default `~/Library/Android/sdk`). `scripts/tasks/android.sh` stops with
  "No Android SDK at ..." when `$ANDROID_HOME/platforms` is missing.
- Nothing else: the JDK, NDK and Merman's prebuilt `.so` come from `bootstrap:android`, the .NET `android` workload is installed into
  `.tools/dotnet` on first use. Set `JAVA_HOME` only to build with another JDK 17 or 21.
- `adb` (SDK platform-tools) and a device or emulator for `android run`.

### Optional

- `gh`: release and CI work (`scripts/tasks/zig-deps-mirror.sh` prints a `gh release` command to publish the mirror).
- `gpg`: only the nightly signs AppImages; local builds do not need it.
