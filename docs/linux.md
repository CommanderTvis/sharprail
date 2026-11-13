# Linux desktop

The [nightly release](https://github.com/CommanderTvis/tereditor/releases/tag/nightly) has an AppImage for x64
(`tereditor-nightly-linux-x64.AppImage`) and for arm64 (`tereditor-nightly-linux-arm64.AppImage`). Both carry an
embedded GPG signature.

## What you need

- x86-64 or arm64 Linux with glibc 2.31 or newer (Ubuntu 20.04, Debian 11, Fedora 32 and later).
- An X11 or XWayland session.
- Git 2.36 or newer on `PATH`. With older Git, such as Ubuntu 22.04's 2.34, Changes shows a Git error.
- The X11 client libraries (`libX11`, `libXcursor`, `libXrandr`, `libXi`), `fontconfig` and OpenGL, which any desktop
  install has. .NET is bundled.

## Run it

```sh
chmod +x tereditor-nightly-linux-x64.AppImage
./tereditor-nightly-linux-x64.AppImage
```

Without FUSE (containers, some minimal installs), let the image unpack itself:

```sh
./tereditor-nightly-linux-x64.AppImage --appimage-extract-and-run
# or unpack once and run from disk
./tereditor-nightly-linux-x64.AppImage --appimage-extract && squashfs-root/AppRun
```

## Window title bar

On GNOME, KDE, Cinnamon, Budgie, Pantheon, Unity, Deepin, COSMIC and XFCE, tereditor's header strip doubles as the
title bar, with the window controls at its right. On other desktops and tiling window managers such as i3, sway or
Hyprland, the system title bar stays.

To choose, open Settings › Appearance › Window decorations and pick Automatic, Integrated header or System title bar.
If the frame looks wrong on your window manager, start tereditor with `TEREDITOR_WINDOW_DECORATIONS=system` to always
keep the system title bar (or `=client` to force the integrated header). The variable overrides the setting.

## What differs from macOS

- Ctrl replaces Command in the editor and shortcuts.
- Terminals draw with Skia.
- Mermaid diagrams show their source instead of a rendered diagram.
- Away notifications use `notify-send` when it is installed. Clicking one does not bring the app back.
- Show in file manager opens your file manager on the entry, and is hidden when you are connected to a remote host.
  Delete moves files to the trash.
- New workspaces copy configured ignored paths with reflinks where the file system supports them (btrfs, XFS), and with
  a plain copy elsewhere.

## Build from source

[Machine setup](develop/setup.md) lists what to install. On Linux x64, `scripts/mise run package:linux` writes
`artifacts/tereditor-linux-x64.AppImage`. On Linux arm64, `scripts/mise run package:linux linux-arm64` writes
`artifacts/tereditor-linux-arm64.AppImage`.
