# Linux desktop

The [nightly prerelease](https://github.com/CommanderTvis/sharprail/releases/tag/nightly) includes
`SharpRail-nightly-linux-x64.AppImage`, built on an Ubuntu runner and gated by the packaged `--linux` checks, and
`SharpRail-nightly-linux-arm64.AppImage`, built and checked the same way on a native arm64 runner. The arm64 image does not gate the
nightly yet: when its job fails, the release goes out without it. Both are signed (embedded GPG signature).
The target is x86-64 or arm64 (aarch64) Linux with glibc 2.31 or newer (Ubuntu 20.04, Debian 11, Fedora 32 and later) and an X11 or XWayland
session. Each image bundles .NET; it needs Git 2.36 or newer on `PATH` (older Git lacks `worktree list -z`, so Ubuntu 22.04's 2.34 shows a Git error in Changes) and, from the system, the X11 client libraries
(`libX11`, `libXcursor`, `libXrandr`, `libXi`), `fontconfig` and a GL or software Skia stack that any desktop install has.

## Run it

```sh
chmod +x SharpRail-nightly-linux-x64.AppImage
./SharpRail-nightly-linux-x64.AppImage
```

Without FUSE (containers, some minimal installs), let the runtime unpack the image itself:

```sh
./SharpRail-nightly-linux-x64.AppImage --appimage-extract-and-run
# or unpack once and run from disk
./SharpRail-nightly-linux-x64.AppImage --appimage-extract && squashfs-root/AppRun
```

Without ICU installed, `AppRun` starts .NET in invariant-globalization mode instead of failing.

## What differs from macOS

- Terminals always use Skia (libghostty-vt cells); the Settings renderer choice is hidden and a saved Metal preference is ignored.
- The window keeps the system title bar; Ctrl replaces Command in the editor and shortcuts.
- Mermaid diagrams show their source with a notice: Merman publishes its C ABI for Apple platforms only.
- Away notifications use `notify-send` when it is installed; clicking one cannot return to the app.
- Created workspaces clone configured ignored paths with `cp -a --reflink=auto` (shared extents on btrfs and XFS, a copy elsewhere).
- Reveal in file manager opens the containing folder with `xdg-open`; delete uses `gio trash`.
- Child shells inherit `APPIMAGE`, `APPDIR` and `ARGV0` from the runtime.

## Build it

On Linux x64, `scripts/mise run package:linux` writes `artifacts/SharpRail-linux-x64.AppImage`; on Linux arm64,
`scripts/mise run package:linux linux-arm64` writes `artifacts/SharpRail-linux-arm64.AppImage` (appimagetool and its runtime are
per-architecture binaries, so each image is packed on a host of its own architecture). `scripts/mise run check:packaged -- --linux`
runs the packaged gate for the image matching the host. Every tool is pinned and checksummed in `mise.toml`; see
[scripts/README.md](scripts/README.md). The native libraries cross-compile with Zig, so `SHARPRAIL_APPDIR_ONLY=1 scripts/mise run package:linux`
also publishes the Linux client on macOS; add `linux-arm64` for the arm64 client. The arm64 image has been cross-built only; its runtime behaviour is covered by CI on an arm64 runner.
