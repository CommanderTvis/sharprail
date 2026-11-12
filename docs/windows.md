# Windows host and desktop client

The [nightly prerelease](https://github.com/CommanderTvis/tereditor/releases/tag/nightly)
includes `tereditor-nightly-win-x64.zip` and `tereditor-nightly-win-arm64.zip`, each built on a Windows runner of its architecture and gated by the packaged
`--windows` checks. Extract the ZIP and launch `client/Tereditor.UI.exe`; the
standalone host is `host/Tereditor.Host.Remote.exe`. The package is unsigned.
The nightly workflow publishes Windows (x64 and ARM64), macOS and Android together only after all
four builds pass, so a failed Windows gate leaves the previous nightly available.

The Windows target is Windows 10 (1809 or later) / Windows 11, x64 or ARM64. Install Git for
Windows and keep `git.exe` on PATH. The package includes .NET; source builds use
the SDK pinned in `global.json`.

## Run a package

Extract `tereditor-win-x64.zip` (or `tereditor-win-arm64.zip` on ARM64 devices). Run `client/Tereditor.UI.exe` to use local projects
and the embedded host. Windows terminals always use Skia, including when a profile
has a saved Metal preference.

Shells use `pwsh.exe` from PATH when available, otherwise Windows PowerShell.
An existing executable named by `SHELL` overrides that choice. Shells start in
the workspace and load their usual profile. Closing a window detaches its views;
closing a terminal tab ends its shell and child processes. Exiting the embedded
host ends its shells.

To run a standalone host, open PowerShell in the extracted folder:

```powershell
$env:TEREDITOR_ROOT = 'C:\code\project'
$env:TEREDITOR_TOKEN = 'replace-with-your-host-token'
$env:TEREDITOR_BIND = '127.0.0.1'
$env:TEREDITOR_PORT = '54123'
.\host\Tereditor.Host.Remote.exe
```

Use the host's reachable interface address when connecting from another machine.
The existing authenticated WebSocket transport and Settings > Host listener work
the same way on Windows. A Windows desktop client can connect to a Windows,
macOS or Linux host. In a separate PowerShell session:

```powershell
$env:TEREDITOR_REMOTE = 'http://127.0.0.1:54123'
$env:TEREDITOR_TOKEN = 'replace-with-your-host-token'
.\client\Tereditor.UI.exe
```

Remove `TEREDITOR_REMOTE` from that session to return to embedded hosting. Remote
project paths name folders on the host, regardless of the client's OS. Local app
state defaults to `%USERPROFILE%\.tereditor`; the standalone host's default state
directory is `%USERPROFILE%\.tereditor\host`.

## Build and check

Run from the checkout in PowerShell. Git, PowerShell and Windows' `tar.exe` are
required; Zig and native sources are downloaded into checkout caches with pinned
hashes/revisions. No Visual Studio C++ installation is needed.

```powershell
scripts\mise.cmd x -- dotnet build Tereditor.slnx -c Release
scripts\mise.cmd x -- dotnet format Tereditor.slnx --verify-no-changes --no-restore
scripts\mise.cmd x -- dotnet run --project tests/Tereditor.Checks -c Release --no-build -- --windows
scripts\mise.cmd run package:windows
```

The package is `artifacts/tereditor-win-x64.zip`; unpacked client, host and checks
are under `artifacts/windows`. The client and host are NativeAOT binaries, so packaging needs Visual Studio 2022 or
its Build Tools with the C++ build tools for the target architecture and a Windows SDK. On a Windows ARM64 machine run
`scripts\mise.cmd run package:windows -- -RuntimeIdentifier win-arm64` for `artifacts/tereditor-win-arm64.zip` and
`artifacts/windows-arm64`; the bootstrap SDK and Zig compiler match the host, and native caches are per RID.
The ARM64 package has not been exercised by a CI run yet. Stop executables running from that directory before
publishing again. CI builds the package on Windows and runs its `--windows` checks.
The check mode covers local/remote terminals, Unicode, resize, reconnect, watching,
descendant cleanup, file saving and recycling, Git/worktrees, native Skia rendering,
editor shortcuts and portable profile paths.

## Platform limits

Metal rendering, native Mermaid rendering and macOS desktop notifications are
unavailable on Windows. The existing Mermaid source fallback remains available.
Windows terminal busy detection conservatively includes background child
processes. Shell builtins run in the shell process and are not detected as busy.
Terminal file links resolve absolute paths; relative links require process CWD
discovery, which the Windows backend does not provide. Unix-only agent process
discovery and the Codex Unix IDE socket retain their existing platform limits.

Cross-compilation and macOS checks do not establish Windows runtime acceptance.
The [terminal contract](../src/Tereditor.Host.Core/Terminals.SPEC.md#windows-verification-scope) records
what the source checks established. Packaged Windows execution and manual desktop input/IME acceptance
remain distinct; outstanding platform work is tracked in
[STATUS.md](../STATUS.md#outstanding-acceptance-work).
