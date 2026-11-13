# Install

Nightly builds are on the [releases page](https://github.com/CommanderTvis/tereditor/releases/tag/nightly). To build
from source instead, see [Building](develop/building.md).

## macOS

Download the disk image for Apple silicon. It is not notarized. Homebrew works through the
[tap](https://github.com/CommanderTvis/homebrew).

```sh
brew trust --tap commandertvis/homebrew
brew tap commandertvis/homebrew https://github.com/CommanderTvis/homebrew
brew install --cask commandertvis/homebrew/tereditor
```

The cask always downloads the current nightly, so update it with
`brew upgrade --cask --greedy commandertvis/homebrew/tereditor`.

## Windows

Download the unsigned x64 or ARM64 ZIP, extract it and launch `client/tereditor.exe`. The standalone host is
`host/tereditor-host.exe`. Windows 10 1809 or later and Git for Windows are required. See
[Windows](windows.md) for running a host and platform limits.

WinGet also works through the [WinGet source](https://github.com/CommanderTvis/winget). Add the
source once from an administrator terminal, then install. If you added it earlier under the old name, run
`winget source remove --name thinkrail` first.

```powershell
winget source add --name commandertvis --arg https://commandertvis-winget.vercel.app --type Microsoft.Rest
winget install --id CommanderTvis.Tereditor --exact --source commandertvis --scope user
```

WinGet adds no command alias, so launch `client\tereditor.exe` from the install folder.

## Linux

Download the AppImage for x64 or ARM64. It needs glibc 2.31 or newer, an X11 or XWayland session and Git 2.36 or newer.

```sh
chmod +x tereditor-nightly-linux-x64.AppImage
./tereditor-nightly-linux-x64.AppImage
```

Without FUSE, run it with `--appimage-extract-and-run`. See [Linux](linux.md) for the libraries it needs and how it
differs from macOS.

## Android

Install the APK from the releases page. The app is a client for a tereditor host on another machine, so set up
a host first. See [Android client](android.md) and [Pair over Tailscale](tailscale.md).

## First run

Open a project with **+** in Projects or ⌘O, then create a workspace with ⌘N. [Using tereditor](using.md) covers the
rest.
