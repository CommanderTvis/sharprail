# Windows

The [nightly release](https://github.com/CommanderTvis/tereditor/releases/tag/nightly) has
`tereditor-nightly-win-x64.zip` and `tereditor-nightly-win-arm64.zip`. The packages are unsigned.

## What you need

- Windows 10 (1809 or later) or Windows 11, on x64 or ARM64.
- Git for Windows, with `git.exe` on `PATH`. .NET is bundled.

## Run it

Extract the ZIP for your machine and run `client/Tereditor.UI.exe`. It works on local projects with a built-in host.

Shells use `pwsh.exe` when it is on `PATH`, otherwise Windows PowerShell. An executable named by `SHELL` overrides that
choice. Shells start in the workspace and load your usual profile. Closing a terminal tab ends its shell and child
processes, and quitting the app ends all of its shells.

App state lives in `%USERPROFILE%\.tereditor`.

## Run a standalone host

The package also has a host without a window, `host/Tereditor.Host.Remote.exe`. Open PowerShell in the extracted
folder:

```powershell
$env:TEREDITOR_ROOT = 'C:\code\project'
$env:TEREDITOR_TOKEN = 'replace-with-your-host-token'
$env:TEREDITOR_BIND = '127.0.0.1'
$env:TEREDITOR_PORT = '54123'
.\host\Tereditor.Host.Remote.exe
```

To connect from another machine, bind to an address that machine can reach. The host keeps its state in
`%USERPROFILE%\.tereditor\host`. You can also turn on the listener of the desktop app in Settings › Host instead.

## Connect to a host

A Windows desktop app can connect to a Windows, macOS or Linux host. In a separate PowerShell window:

```powershell
$env:TEREDITOR_REMOTE = 'http://127.0.0.1:54123'
$env:TEREDITOR_TOKEN = 'replace-with-your-host-token'
.\client\Tereditor.UI.exe
```

Remove `TEREDITOR_REMOTE` to work locally again. Project paths name folders on the host, whatever the client's OS. See
[Remote hosts and clients](remote.md).

## Limits on Windows

- Terminals draw with Skia.
- Mermaid diagrams show their source instead of a rendered diagram.
- A terminal counts as busy while any background child process runs. Shell builtins do not count.
- Terminal links work for absolute paths only, not relative ones.

## Build from source

Run from the checkout in PowerShell. [Machine setup](develop/setup.md) lists what to install; packaging needs Visual
Studio 2022 or its Build Tools with the C++ tools and a Windows SDK.

```powershell
scripts\mise.cmd x -- dotnet build Tereditor.slnx -c Release
scripts\mise.cmd run package:windows
```

The package is `artifacts/tereditor-win-x64.zip`. On an ARM64 machine, add `-- -RuntimeIdentifier win-arm64` to the
package command for `artifacts/tereditor-win-arm64.zip`.
