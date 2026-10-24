# Install

Nightly builds are on the [releases page](https://github.com/CommanderTvis/sharprail/releases/tag/nightly). To build
from source instead, see [Building](develop/building.md).

## macOS

Download the disk image for Apple silicon. It is not notarized. Homebrew works through the
[thinkrail tap](https://github.com/CommanderTvis/homebrew-thinkrail).

```sh
brew trust --tap commandertvis/thinkrail
brew install --cask commandertvis/thinkrail/sharprail
```

The cask always downloads the current nightly, so update it with
`brew upgrade --cask --greedy commandertvis/thinkrail/sharprail`. The tap's own workflow does not test this cask's
installation.

## Windows

Download the unsigned x64 or ARM64 ZIP, extract it and launch `client/SharpRail.UI.exe`. The standalone host is
`host/SharpRail.Host.Remote.exe`. Windows 10 1809 or later and Git for Windows are required. See
[Windows](../WINDOWS.md) for setup and validation limits.

On x64, WinGet also works through the [thinkrail source](https://github.com/CommanderTvis/winget-thinkrail). Add the
source once from an administrator terminal, then install.

```powershell
winget source add --name thinkrail --arg https://winget-thinkrail.vercel.app --type Microsoft.Rest
winget install --id CommanderTvis.SharpRail --exact --source thinkrail --scope user
```

WinGet adds no command alias, so launch `client\SharpRail.UI.exe` from the install folder. The source's own workflow
does not test this package's installation.

## Linux

Download the AppImage for x64 or ARM64. It needs glibc 2.31 or newer, an X11 or XWayland session and Git 2.36 or newer.

```sh
chmod +x SharpRail-nightly-linux-x64.AppImage
./SharpRail-nightly-linux-x64.AppImage
```

Without FUSE, run it with `--appimage-extract-and-run`. See [Linux](../LINUX.md) for the libraries it needs and how it
differs from macOS.

## Android

Install the APK from the releases page. The app is a client for a SharpRail host on another machine, so set up
a host first. See [Android client](android.md) and [Pair over Tailscale](tailscale.md).

## First run

Open a project with **+** in Projects or ⌘O, then create a workspace with ⌘N. [Using SharpRail](using.md) covers the
rest.
